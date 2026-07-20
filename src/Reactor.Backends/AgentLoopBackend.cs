using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Agent;
using Reactor.Agent.Environments;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Llm;

namespace Reactor.Backends;

/// <summary>Options for <see cref="AgentLoopBackend"/>.</summary>
public sealed record AgentLoopBackendOptions
{
    /// <summary>The LLM client (adapters for each provider the pipeline uses).</summary>
    public required Client Client { get; init; }

    /// <summary>Provider used when a node sets neither <c>llm_provider</c> nor a model the catalog recognizes.</summary>
    public string? DefaultProvider { get; init; }

    /// <summary>Model used when a node has no <c>llm_model</c> (otherwise the catalog's latest tool-capable model).</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Creates the provider profile (default: <see cref="ProviderProfiles.Create"/>). Register custom tools here.</summary>
    public Func<string, string, IProviderProfile>? ProfileFactory { get; init; }

    /// <summary>Creates the execution environment for a working directory (default: <see cref="LocalExecutionEnvironment"/>).</summary>
    public Func<string, IExecutionEnvironment>? EnvironmentFactory { get; init; }

    /// <summary>Base session configuration, cloned per session.</summary>
    public SessionConfig? SessionConfig { get; init; }

    /// <summary>Additional interceptors (approval gates, auditing) applied to every session.</summary>
    public IReadOnlyList<IToolInterceptor> Interceptors { get; init; } = [];

    /// <summary>How often to look for a supervisor's <c>steering.md</c> in the stage directory.</summary>
    public TimeSpan SteeringPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Reuse one session per <c>full</c>-fidelity thread (default true).</summary>
    public bool ReuseThreadSessions { get; init; } = true;

    /// <summary>Forward assistant text to the stage log at debug level.</summary>
    public bool LogAssistantText { get; init; } = true;
}

/// <summary>
/// Runs codergen stages with the built-in coding agent loop (Reactor.Agent) — the reference backend. Each stage submits
/// the prompt to a session whose profile matches the node's <c>llm_provider</c>/<c>llm_model</c>; <c>full</c>-fidelity
/// stages on the same thread share one session (conversation continuity). The agent can report outcomes through
/// <c>status.json</c>. Node extension attributes: <c>agent.max_tool_rounds</c>, <c>agent.max_turns</c>,
/// <c>agent.instructions</c>, <c>agent.shell_timeout</c>.
/// </summary>
public sealed class AgentLoopBackend : ICodergenBackend, IAsyncDisposable
{
    private readonly AgentLoopBackendOptions _options;
    private readonly ThreadLeasePool<SessionHolder> _sessions;

    public AgentLoopBackend(AgentLoopBackendOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessions = new ThreadLeasePool<SessionHolder>(h => new ValueTask(h.Session.CloseAsync()));
    }

    public bool SupportsStatusFile => true;

    /// <summary>Number of live thread sessions (for diagnostics).</summary>
    public int ThreadSessionCount => _sessions.Count;

    public async Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(context);
        var stage = StageSettings.From(node, context);
        var run = context.Run;
        var (provider, model) = ResolveModel(stage);
        void Log(string message, string level) => run?.Log(node.Id, message, level);

        var key = _options.ReuseThreadSessions ? stage.ThreadKey("agent:" + provider) : null;
        await using var lease = await _sessions.AcquireAsync(key, () => CreateSession(provider, model, stage)).ConfigureAwait(false);
        var holder = lease.Value;
        var session = holder.Session;
        if (!string.Equals(session.Profile.Model, model, StringComparison.Ordinal))
        {
            session.Profile.Model = model;
        }

        ConfigureForStage(session.Config, node, stage, model);
        holder.Hooks = ToolHookSettings.Resolve(node, run?.Graph, stage, Log);
        if (lease.ContextLost)
        {
            prompt = ThreadContinuity.Recover(prompt, stage, context, Log);
        }

        using var eventLog = new StageEventLog(stage.StageDirectory);
        var stageStats = new StageStats();
        using var subscription = session.Events.Subscribe(evt => OnSessionEvent(evt, eventLog, stageStats, Log));
        using var steeringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var steering = WatchSteeringAsync(stage.StageDirectory, session, Log, steeringCts.Token);

        var historyBefore = session.History.Count;
        var usageBefore = session.TotalUsage;
        var errorBefore = session.LastError;
        Log($"agent session {(lease.IsNew ? "started" : "resumed")}: {provider}/{model}" + (stage.ThreadId is null ? "" : $" (thread {stage.ThreadId})"), "info");
        try
        {
            await session.SubmitAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (AbortError) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The stage was cancelled.", cancellationToken);
        }
        finally
        {
            await steeringCts.CancelAsync().ConfigureAwait(false);
            await steering.ConfigureAwait(false);
            WriteTranscript(stage.StageDirectory, session);
            if (session.State == SessionState.Closed && lease.Pooled)
            {
                await _sessions.InvalidateAsync(lease).ConfigureAwait(false);
            }
        }

        var stageTurns = session.History.Skip(historyBefore).ToList();
        var text = stageTurns.OfType<AssistantTurn>().Select(t => t.Content).LastOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
        var usage = Subtract(session.TotalUsage, usageBefore);
        var metadata = new Dictionary<string, object?>
        {
            ["backend"] = "agent",
            ["provider"] = provider,
            ["model"] = model,
            ["session_id"] = session.Id,
            ["thread_id"] = stage.ThreadId,
            ["thread_context"] = ThreadContinuity.Describe(stage, lease.IsNew, lease.ContextLost),
            ["reasoning_effort"] = session.Config.ReasoningEffort,
            ["assistant_turns"] = stageTurns.Count(t => t is AssistantTurn),
            ["tool_calls"] = stageStats.ToolCalls,
            ["tool_errors"] = stageStats.ToolErrors,
            ["input_tokens"] = usage.InputTokens,
            ["output_tokens"] = usage.OutputTokens,
            ["total_tokens"] = usage.TotalTokens,
            ["reasoning_tokens"] = usage.ReasoningTokens,
            ["cache_read_tokens"] = usage.CacheReadTokens,
        };

        if (session.LastError is ContextLengthError contextError && !ReferenceEquals(contextError, errorBefore))
        {
            metadata["stopped_reason"] = "context_length";
            return Stopped(stage, text, metadata, "The agent's context window was exceeded: " + contextError.Message);
        }

        if (stageStats.TurnLimit is { } limit)
        {
            metadata["stopped_reason"] = limit;
            return Stopped(stage, text, metadata, $"The agent stopped at its turn limit ({limit}) before finishing.");
        }

        if (text.Length == 0)
        {
            text = "(The agent finished without a text response.)";
        }

        return new CodergenResultBuilder(text, metadata).Build();
    }

    public ValueTask DisposeAsync() => _sessions.DisposeAllAsync();

    /// <summary>Resolves the provider and model for a stage.</summary>
    internal (string Provider, string Model) ResolveModel(StageSettings stage) =>
        ModelResolution.Resolve(stage, _options.Client, _options.DefaultProvider, _options.DefaultModel);

    private SessionHolder CreateSession(string provider, string model, StageSettings stage)
    {
        var profile = _options.ProfileFactory?.Invoke(provider, model) ?? ProviderProfiles.Create(provider, model);
        profile.ProviderName = provider;
        var environment = _options.EnvironmentFactory?.Invoke(stage.WorkingDirectory)
            ?? new LocalExecutionEnvironment(new LocalEnvironmentOptions { WorkingDirectory = stage.WorkingDirectory });
        var holder = new SessionHolder();
        var interceptors = new List<IToolInterceptor> { new ToolHookInterceptor(() => holder.Hooks) };
        interceptors.AddRange(_options.Interceptors);
        var config = _options.SessionConfig?.Clone() ?? new SessionConfig();
        holder.Session = new Session(profile, environment, _options.Client, config, new SessionOptions { Interceptors = interceptors });
        return holder;
    }

    private void ConfigureForStage(SessionConfig config, Node node, StageSettings stage, string model)
    {
        var defaults = _options.SessionConfig ?? new SessionConfig();
        config.ReasoningEffort = ModelResolution.ReasoningEffort(stage, model);
        config.MaxToolRoundsPerInput = ParseInt(node["agent.max_tool_rounds"]) ?? defaults.MaxToolRoundsPerInput;
        config.MaxTurns = ParseInt(node["agent.max_turns"]) ?? defaults.MaxTurns;
        config.UserInstructions = Combine(defaults.UserInstructions, node["agent.instructions"]);
        config.CommandTimeoutOverrideMs = AttributeValues.ParseDuration(node["agent.shell_timeout"]) is { } shellTimeout
            ? (int)Math.Clamp(shellTimeout.TotalMilliseconds, 1, int.MaxValue)
            : defaults.CommandTimeoutOverrideMs;
    }

    private void OnSessionEvent(SessionEvent evt, StageEventLog eventLog, StageStats stats, Action<string, string> log)
    {
        eventLog.Write(ToJson(evt));
        var prefix = evt.Data.TryGetValue("agent_id", out var agent) && agent is not null ? $"[{agent}] " : "";
        switch (evt.Kind)
        {
            case EventKind.ToolCallStart:
                Interlocked.Increment(ref stats.ToolCalls);
                var args = evt.GetString("arguments");
                log($"{prefix}→ {evt.GetString("tool_name")} {LogText.SummarizeArguments(JsonHelpersParse(args))}", "info");
                break;
            case EventKind.ToolCallEnd when evt.Data.TryGetValue("error", out var error) && error is not null:
                Interlocked.Increment(ref stats.ToolErrors);
                log($"{prefix}✗ {evt.GetString("tool_name")}: {LogText.Clip(error.ToString(), 300)}", "warn");
                break;
            case EventKind.AssistantTextEnd when _options.LogAssistantText && !string.IsNullOrWhiteSpace(evt.GetString("text")):
                log($"{prefix}assistant: {LogText.Clip(evt.GetString("text"), 300)}", "debug");
                break;
            case EventKind.TurnLimit:
                if (prefix.Length == 0)
                {
                    stats.TurnLimit = evt.GetString("limit") ?? "turn_limit";
                }

                log($"{prefix}turn limit reached ({evt.GetString("limit")})", "warn");
                break;
            case EventKind.LoopDetection:
                log($"{prefix}{evt.GetString("message")}", "warn");
                break;
            case EventKind.Warning:
                log($"{prefix}{evt.GetString("message")}", "warn");
                break;
            case EventKind.Error:
                log($"{prefix}agent error: {evt.GetString("error")}", "error");
                break;
            case EventKind.SteeringInjected:
                log($"{prefix}steering injected: {LogText.Clip(evt.GetString("content"))}", "info");
                break;
        }
    }

    private async Task WatchSteeringAsync(string? stageDirectory, Session session, Action<string, string> log, CancellationToken cancellationToken)
    {
        if (stageDirectory is null)
        {
            return;
        }

        var path = Path.Combine(stageDirectory, "steering.md");
        var consumed = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.SteeringPollInterval, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(path))
                {
                    continue;
                }

                string text;
                try
                {
                    text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    consumed++;
                    File.Move(path, Path.Combine(stageDirectory, $"steering.consumed-{consumed}.md"), overwrite: true);
                }
                catch (IOException)
                {
                    continue; // Still being written; try again next poll.
                }

                if (!string.IsNullOrWhiteSpace(text) && session.State != SessionState.Closed)
                {
                    session.Steer(text.Trim());
                    log("supervisor steering queued for the agent", "info");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void WriteTranscript(string? stageDirectory, Session session)
    {
        if (stageDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(stageDirectory);
            File.WriteAllText(Path.Combine(stageDirectory, "transcript.md"), session.Transcript());
        }
        catch (IOException)
        {
        }
    }

    private static CodergenResult Stopped(StageSettings stage, string text, Dictionary<string, object?> metadata, string reason)
    {
        // An agent that wrote status.json decided its own outcome; let the handler apply it.
        if (stage.StageDirectory is { } dir && File.Exists(Path.Combine(dir, "status.json")))
        {
            return new CodergenResultBuilder(text.Length > 0 ? text : reason, metadata).Build();
        }

        var outcome = Outcome.Fail(reason, new Dictionary<string, object?>
        {
            ["last_stage"] = stage.NodeId,
            ["last_response"] = text.Length <= CodergenHandler.LastResponseLength ? text : text[..CodergenHandler.LastResponseLength],
        });
        return new CodergenResultBuilder(text, metadata) { Outcome = outcome }.Build();
    }

    private static JsonObject ToJson(SessionEvent evt)
    {
        var data = new JsonObject();
        foreach (var (key, value) in evt.Data)
        {
            data[key] = value switch
            {
                null => null,
                string s => s,
                bool b => b,
                int i => i,
                long l => l,
                double d => d,
                JsonNode node => node.DeepClone(),
                Usage usage => JsonSerializer.SerializeToNode(usage),
                _ => value.ToString(),
            };
        }

        return new JsonObject
        {
            ["ts"] = evt.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = evt.Kind.ToString(),
            ["session_id"] = evt.SessionId,
            ["data"] = data,
        };
    }

    private static JsonNode? JsonHelpersParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    private static Usage Subtract(Usage total, Usage before) => new()
    {
        InputTokens = total.InputTokens - before.InputTokens,
        OutputTokens = total.OutputTokens - before.OutputTokens,
        TotalTokens = total.TotalTokens - before.TotalTokens,
        ReasoningTokens = (total.ReasoningTokens ?? 0) - (before.ReasoningTokens ?? 0),
        CacheReadTokens = (total.CacheReadTokens ?? 0) - (before.CacheReadTokens ?? 0),
        CacheWriteTokens = (total.CacheWriteTokens ?? 0) - (before.CacheWriteTokens ?? 0),
    };

    private static int? ParseInt(string? value) => AttributeValues.ParseInt(value);

    private static string? Combine(string? a, string? b) =>
        string.IsNullOrWhiteSpace(b) ? a : string.IsNullOrWhiteSpace(a) ? b : a.TrimEnd() + "\n\n" + b.Trim();

    private sealed class SessionHolder
    {
        public Session Session { get; set; } = null!;

        public ToolHookSettings? Hooks { get; set; }
    }

    private sealed class StageStats
    {
        public int ToolCalls;
        public int ToolErrors;
        public string? TurnLimit;
    }
}

/// <summary>Builds a <see cref="CodergenResult"/> with metadata.</summary>
internal sealed class CodergenResultBuilder(string? text, IReadOnlyDictionary<string, object?> metadata)
{
    public Outcome? Outcome { get; init; }

    public CodergenResult Build() =>
        (Outcome is { } outcome ? CodergenResult.FromOutcome(outcome, text) : CodergenResult.FromText(text ?? "")).WithMetadata(metadata);
}

using System.Globalization;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Backends.Cli;

/// <summary>Options shared by CLI agent backends.</summary>
public record CliAgentOptions
{
    /// <summary>Executable name or path. Defaults to the backend's CLI name (overridable with an environment variable).</summary>
    public string? Executable { get; init; }

    /// <summary>Model used when a node has no <c>llm_model</c> (null = the CLI's own default).</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Extra arguments appended to every invocation.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>Environment overrides for the CLI process (null value = remove the variable).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Resume the CLI's session for stages on the same <c>full</c>-fidelity thread (default true).</summary>
    public bool ReuseThreadSessions { get; init; } = true;

    /// <summary>Forward assistant messages to the stage log at debug level.</summary>
    public bool LogAssistantText { get; init; } = true;
}

/// <summary>What a CLI backend needs to build its command line.</summary>
/// <param name="Stage">Stage settings.</param>
/// <param name="Model">Normalized model, or null for the CLI default.</param>
/// <param name="ReasoningEffort">Mapped reasoning effort, or null to omit.</param>
/// <param name="SessionId">The CLI session id for this stage.</param>
/// <param name="Resume">True when continuing an existing session (same thread).</param>
public sealed record CliInvocation(StageSettings Stage, string? Model, string? ReasoningEffort, string SessionId, bool Resume);

/// <summary>The parsed outcome of one CLI run.</summary>
public sealed class CliRunSummary
{
    /// <summary>The agent's final response text.</summary>
    public string Text { get; set; } = "";

    /// <summary>True once the CLI's terminal result event was seen.</summary>
    public bool Completed { get; set; }

    public bool IsError { get; set; }

    public string? ErrorMessage { get; set; }

    public string? SessionId { get; set; }

    public string? Model { get; set; }

    public int ToolCalls { get; set; }

    public int ToolErrors { get; set; }

    /// <summary>Backend-specific metadata (usage, cost, ...).</summary>
    public Dictionary<string, object?> Metadata { get; } = new(StringComparer.Ordinal);
}

/// <summary>Incremental parser for a CLI's JSON-lines output.</summary>
public interface ICliOutputParser
{
    /// <summary>Processes one stdout line; <paramref name="log"/> receives (message, level) stage-log entries.</summary>
    void ProcessLine(string line, Action<string, string> log);

    /// <summary>Whether to keep the raw line in <c>cli-events.jsonl</c> (noisy streaming deltas are dropped).</summary>
    bool ShouldRecord(string line);

    CliRunSummary Summary { get; }
}

/// <summary>
/// Base class for backends that run an external coding agent CLI per stage: the prompt goes to stdin, JSON-lines output is
/// parsed for the final response, tool activity, usage, and errors. Stages on the same <c>full</c>-fidelity thread resume
/// the same CLI session. Raw output is kept in <c>{stage}/cli-events.jsonl</c> and stderr in <c>{stage}/stderr.log</c>.
/// </summary>
public abstract class CliAgentBackend : ICodergenBackend, IAsyncDisposable
{
    private readonly CliAgentOptions _options;
    private readonly string _defaultExecutable;
    private readonly string _executableVariable;
    private readonly ThreadLeasePool<CliThread> _threads = new() { IdleTimeout = null };
    private ResolvedCommand? _command;

    protected CliAgentBackend(CliAgentOptions options, string defaultExecutable, string executableVariable)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _defaultExecutable = defaultExecutable;
        _executableVariable = executableVariable;
    }

    /// <summary>Backend name used in logs and metadata (e.g. <c>copilot-cli</c>).</summary>
    public abstract string Name { get; }

    public bool SupportsStatusFile => true;

    protected CliAgentOptions Options => _options;

    /// <summary>The executable that will run (resolved lazily from options, environment, then PATH).</summary>
    public ResolvedCommand ResolveCommand()
    {
        if (_command is { } cached)
        {
            return cached;
        }

        var executable = _options.Executable ?? Environment.GetEnvironmentVariable(_executableVariable) ?? _defaultExecutable;
        _command = CommandResolver.Resolve(executable)
            ?? throw new CliAgentException(
                $"{Name}: executable '{executable}' was not found. Install it, put it on PATH, or set {_executableVariable}.",
                retryable: false);
        return _command;
    }

    public async Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(context);
        var stage = StageSettings.From(node, context);
        var run = context.Run;
        void Log(string message, string level) => run?.Log(node.Id, message, level);

        var command = ResolveCommand();
        var model = NormalizeModel(stage.Model ?? _options.DefaultModel, Log);
        var effort = MapReasoningEffort(stage.ExplicitReasoningEffort);
        var key = _options.ReuseThreadSessions ? stage.ThreadKey(Name) : null;
        await using var lease = await _threads.AcquireAsync(key, () => new CliThread()).ConfigureAwait(false);
        var thread = lease.Value;
        var invocation = new CliInvocation(stage, model, effort, thread.SessionId, thread.Started);
        var arguments = BuildArguments(invocation).Concat(_options.ExtraArguments).ToList();
        if (lease.ContextLost)
        {
            prompt = ThreadContinuity.Recover(prompt, stage, context, Log);
        }

        Log($"{Name}: {(invocation.Resume ? "resuming" : "starting")} session {thread.SessionId}" + (model is null ? "" : $" ({model})"), "info");
        var parser = CreateParser();
        CliProcessResult result;
        try
        {
            using var eventLog = new StageEventLog(stage.StageDirectory, "cli-events.jsonl");
            result = await CliProcess.RunAsync(
                command,
                arguments,
                stage.WorkingDirectory,
                prompt,
                _options.Environment,
                line =>
                {
                    if (parser.ShouldRecord(line))
                    {
                        eventLog.WriteRaw(line);
                    }

                    parser.ProcessLine(line, Log);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SettleSession(thread, parser.Summary, succeeded: false);
            throw;
        }

        if (stage.StageDirectory is { } dir && !string.IsNullOrWhiteSpace(result.Stderr))
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "stderr.log"), result.Stderr, CancellationToken.None).ConfigureAwait(false);
        }

        var summary = parser.Summary;
        if (result.ExitCode != 0 || summary.IsError || !summary.Completed)
        {
            SettleSession(thread, summary, succeeded: false);
            var detail = summary.ErrorMessage ?? LastLines(result.Stderr) ?? (summary.Completed ? null : "no result was reported");
            var message = $"{Name} failed (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)})" + (detail is null ? "" : ": " + detail);
            Log(message, "error");
            throw new CliAgentException(message, CliAgentException.IsRetryableMessage(detail));
        }

        SettleSession(thread, summary, succeeded: true);
        var metadata = new Dictionary<string, object?>(summary.Metadata, StringComparer.Ordinal)
        {
            ["backend"] = Name,
            ["executable"] = command.ToString(),
            ["model"] = summary.Model ?? model,
            ["session_id"] = thread.SessionId,
            ["thread_id"] = stage.ThreadId,
            ["thread_context"] = ThreadContinuity.Describe(stage, lease.IsNew, lease.ContextLost),
            ["resumed"] = invocation.Resume,
            ["reasoning_effort"] = effort,
            ["tool_calls"] = summary.ToolCalls,
            ["tool_errors"] = summary.ToolErrors,
            ["duration_ms"] = (long)result.Duration.TotalMilliseconds,
        };
        AddUsageMetadata(summary, thread, metadata);

        var text = summary.Text.Length > 0 ? summary.Text : "(The agent finished without a text response.)";
        return CodergenResult.FromText(text).WithMetadata(metadata);
    }

    public ValueTask DisposeAsync() => _threads.DisposeAllAsync();

    /// <summary>
    /// Records which CLI session the thread continues with. A session id the CLI reported is adopted even when the run failed
    /// (the CLI may have created the session, and creating it again would collide). A run that failed before reporting one
    /// leaves the session's existence unknown, so a thread that has not started yet switches to a fresh id.
    /// </summary>
    private static void SettleSession(CliThread thread, CliRunSummary summary, bool succeeded)
    {
        if (summary.SessionId is { Length: > 0 } reported)
        {
            thread.SessionId = reported;
            thread.Started = true;
        }
        else if (succeeded)
        {
            thread.Started = true;
        }
        else if (!thread.Started)
        {
            thread.SessionId = Guid.NewGuid().ToString();
        }
    }

    /// <summary>Builds the CLI arguments (excluding <see cref="CliAgentOptions.ExtraArguments"/>).</summary>
    protected internal abstract IReadOnlyList<string> BuildArguments(CliInvocation invocation);

    protected internal abstract ICliOutputParser CreateParser();

    /// <summary>Maps a pipeline model id to the CLI's naming (null = use the CLI default).</summary>
    protected internal virtual string? NormalizeModel(string? model, Action<string, string> log) => model;

    /// <summary>Maps the node's reasoning effort to the CLI's levels (null = omit).</summary>
    protected internal virtual string? MapReasoningEffort(string? effort) => effort;

    /// <summary>Converts cumulative usage reported by the CLI into per-stage metadata.</summary>
    protected virtual void AddUsageMetadata(CliRunSummary summary, CliThread thread, Dictionary<string, object?> metadata)
    {
    }

    private static string? LastLines(string text, int count = 5)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(count));
    }

    /// <summary>Per-thread CLI session state.</summary>
    protected internal sealed class CliThread
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>True after the first successful run (subsequent runs resume).</summary>
        public bool Started { get; set; }

        /// <summary>Last cumulative premium-request count reported for this session (Copilot).</summary>
        public double CumulativePremiumRequests { get; set; }
    }
}

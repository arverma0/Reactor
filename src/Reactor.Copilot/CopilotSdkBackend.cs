using System.Text.Json.Nodes;
using Reactor.Backends;
using Reactor.Backends.Cli;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Copilot;

/// <summary>Options for <see cref="CopilotSdkBackend"/>.</summary>
public sealed record CopilotSdkBackendOptions
{
    /// <summary>Model used when a node has no <c>llm_model</c> (null = Copilot's default).</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Path to a Copilot runtime executable, overriding the bundled runtime and <c>COPILOT_CLI_PATH</c>.</summary>
    public string? RuntimePath { get; init; }

    /// <summary>GitHub token to authenticate with (default: the logged-in user).</summary>
    public string? GitHubToken { get; init; }

    /// <summary>Copilot data directory (<c>COPILOT_HOME</c>; default <c>~/.copilot</c>).</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>Continue the same Copilot session for stages on one <c>full</c>-fidelity thread (default true).</summary>
    public bool ReuseThreadSessions { get; init; } = true;

    /// <summary>Upper bound on one stage when the node has no <c>timeout</c> (default 4 hours).</summary>
    public TimeSpan MaxStageDuration { get; init; } = TimeSpan.FromHours(4);

    /// <summary>Forward assistant messages to the stage log at debug level.</summary>
    public bool LogAssistantText { get; init; } = true;

    /// <summary>Copilot tools to disable for pipeline stages (e.g. <c>ask_user</c> is never offered).</summary>
    public IReadOnlyList<string>? ExcludedTools { get; init; }

    /// <summary>
    /// Check requested models against the account's model list before a stage starts (default true). Without this, the
    /// runtime may silently substitute a different (possibly more expensive) model for an unavailable one.
    /// </summary>
    public bool VerifyModels { get; init; } = true;
}

/// <summary>A Copilot stage failed. <see cref="Retryable"/> tells the engine whether a retry may help.</summary>
public sealed class CopilotStageException(string message, bool retryable, Exception? innerException = null) : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Runs codergen stages through the GitHub Copilot SDK: one Copilot runtime per backend, one Copilot session per stage
/// (or per <c>full</c>-fidelity thread, continuing the conversation). Tool permissions are approved automatically,
/// <c>tool_hooks</c> run through the SDK's pre/post tool hooks, and the
/// agent may write <c>status.json</c>. Activity goes to the stage log and <c>{stage}/copilot-events.jsonl</c>.
/// </summary>
public sealed class CopilotSdkBackend : ICodergenBackend, IAsyncDisposable
{
    private readonly CopilotSdkBackendOptions _options;
    private readonly Lazy<ICopilotRuntime> _runtime;
    private readonly ThreadLeasePool<ConversationHolder> _threads;
    private readonly SemaphoreSlim _modelsLock = new(1, 1);
    private IReadOnlyList<CopilotModelInfo>? _models;
    private bool _modelsUnavailable;

    public CopilotSdkBackend(CopilotSdkBackendOptions? options = null)
        : this(options ?? new CopilotSdkBackendOptions(), null)
    {
    }

    internal CopilotSdkBackend(CopilotSdkBackendOptions options, ICopilotRuntime? runtime)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runtime = new Lazy<ICopilotRuntime>(() => runtime ?? new SdkCopilotRuntime(_options), LazyThreadSafetyMode.ExecutionAndPublication);
        _threads = new ThreadLeasePool<ConversationHolder>(holder => holder.Conversation is { } conversation ? conversation.DisposeAsync() : ValueTask.CompletedTask);
    }

    public bool SupportsStatusFile => true;

    public async Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(context);
        var stage = StageSettings.From(node, context);
        var run = context.Run;
        void Log(string message, string level) => run?.Log(node.Id, message, level);

        var model = CopilotModelNames.Normalize(stage.Model ?? _options.DefaultModel);
        var effort = CopilotModelNames.SdkReasoningEffort(stage.ExplicitReasoningEffort);
        if (model is not null && _options.VerifyModels && await GetModelsAsync(Log, cancellationToken).ConfigureAwait(false) is { } models)
        {
            var info = models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
            if (info is null || !info.Enabled)
            {
                var available = string.Join(", ", models.Where(m => m.Enabled).Select(m => m.Id).Order(StringComparer.Ordinal));
                throw new CopilotStageException(
                    $"Copilot model '{model}' is {(info is null ? "not available" : "disabled by policy")} for this account. Available models: {available}.",
                    retryable: false);
            }

            model = info.Id;
            if (effort is not null && !info.ReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            {
                Log($"copilot-sdk: {model} does not support reasoning_effort '{effort}'; using the model default", "debug");
                effort = null;
            }
        }

        var key = _options.ReuseThreadSessions ? stage.ThreadKey("copilot-sdk") : null;
        await using var lease = await _threads.AcquireAsync(key, () => new ConversationHolder()).ConfigureAwait(false);
        var holder = lease.Value;
        holder.Hooks = ToolHookSettings.Resolve(node, run?.Graph, stage, Log);

        var resumed = holder.Conversation is not null;
        if (holder.Conversation is null)
        {
            holder.Conversation = await CreateConversationAsync(holder, stage, model, effort, cancellationToken).ConfigureAwait(false);
            holder.Model = model;
        }
        else if (model is not null && !string.Equals(model, holder.Model, StringComparison.Ordinal))
        {
            await holder.Conversation.SetModelAsync(model, effort, cancellationToken).ConfigureAwait(false);
            holder.Model = model;
        }

        var conversation = holder.Conversation;
        Log($"copilot-sdk: {(resumed ? "continuing" : "started")} session {conversation.SessionId}" + (model is null ? "" : $" ({model})"), "info");

        var stats = new StageStats();
        string? reply;
        using (var eventLog = new StageEventLog(stage.StageDirectory, "copilot-events.jsonl"))
        using (conversation.Subscribe(activity => OnActivity(activity, stats, eventLog, Log)))
        {
            var timeout = stage.Timeout is { } nodeTimeout && nodeTimeout > TimeSpan.Zero ? nodeTimeout + TimeSpan.FromSeconds(30) : _options.MaxStageDuration;
            try
            {
                reply = await conversation.SendAndWaitAsync(prompt, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await AbortQuietlyAsync(conversation).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException ex)
            {
                await AbortQuietlyAsync(conversation).ConfigureAwait(false);
                throw new CopilotStageException($"Copilot did not finish within {timeout}.", retryable: true, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The session (or its connection) is gone; drop it so the next stage starts fresh.
                if (lease.Pooled)
                {
                    await _threads.InvalidateAsync(lease).ConfigureAwait(false);
                }

                throw new CopilotStageException($"Copilot session failed: {ex.Message}", retryable: true, ex);
            }
        }

        var text = !string.IsNullOrWhiteSpace(reply) ? reply.Trim() : stats.LastAssistantText;
        if (stats.LastError is { } error && string.IsNullOrWhiteSpace(text))
        {
            var message = $"Copilot reported an error: {error.Message}";
            Log(message, "error");
            throw new CopilotStageException(message, IsRetryable(error));
        }

        var actualModel = stats.Model ?? model;
        if (model is not null && stats.Model is { } reported && !string.Equals(reported, model, StringComparison.OrdinalIgnoreCase))
        {
            Log($"copilot-sdk: the stage ran on '{reported}' instead of the requested '{model}'", "warn");
        }

        var metadata = new Dictionary<string, object?>
        {
            ["backend"] = "copilot-sdk",
            ["model"] = actualModel,
            ["requested_model"] = model,
            ["session_id"] = conversation.SessionId,
            ["thread_id"] = stage.ThreadId,
            ["resumed"] = resumed,
            ["reasoning_effort"] = effort,
            ["tool_calls"] = stats.ToolCalls,
            ["tool_errors"] = stats.ToolErrors,
            ["model_calls"] = stats.ModelCalls,
            ["input_tokens"] = stats.InputTokens,
            ["output_tokens"] = stats.OutputTokens,
            ["cache_read_tokens"] = stats.CacheReadTokens,
            ["reasoning_tokens"] = stats.ReasoningTokens,
        };
        if (stats.NanoAiu is { } nanoAiu)
        {
            metadata["ai_units"] = Math.Max(0, nanoAiu - holder.NanoAiu) / 1e9;
            holder.NanoAiu = nanoAiu;
        }

        return CodergenResult.FromText(string.IsNullOrWhiteSpace(text) ? "(The agent finished without a text response.)" : text).WithMetadata(metadata);
    }

    public async ValueTask DisposeAsync()
    {
        await _threads.DisposeAllAsync().ConfigureAwait(false);
        if (_runtime.IsValueCreated)
        {
            await _runtime.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The models the signed-in Copilot account can use (starts the runtime if needed).</summary>
    public Task<IReadOnlyList<CopilotModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        _runtime.Value.ListModelsAsync(cancellationToken);

    /// <summary>The cached model list, or null when the runtime cannot list models (verification is then skipped).</summary>
    private async Task<IReadOnlyList<CopilotModelInfo>?> GetModelsAsync(Action<string, string> log, CancellationToken cancellationToken)
    {
        if (_models is not null || _modelsUnavailable)
        {
            return _models;
        }

        await _modelsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_models is null && !_modelsUnavailable)
            {
                try
                {
                    _models = await _runtime.Value.ListModelsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not CopilotStageException)
                {
                    _modelsUnavailable = true;
                    log($"copilot-sdk: could not list models ({ex.Message}); requested models are not verified", "warn");
                }
            }

            return _models;
        }
        finally
        {
            _modelsLock.Release();
        }
    }

    private Task<ICopilotConversation> CreateConversationAsync(ConversationHolder holder, StageSettings stage, string? model, string? effort, CancellationToken cancellationToken)
    {
        var additional = new List<string>();
        if (stage.StageDirectory is { } stageDir && !IsWithin(stageDir, stage.WorkingDirectory))
        {
            additional.Add(stageDir);
        }

        return _runtime.Value.CreateConversationAsync(
            new CopilotConversationOptions
            {
                SessionId = Guid.NewGuid().ToString(),
                Model = model,
                ReasoningEffort = effort,
                WorkingDirectory = stage.WorkingDirectory,
                AdditionalDirectories = additional,
                Hooks = () => holder.Hooks,
                ExcludedTools = _options.ExcludedTools,
            },
            cancellationToken);
    }

    private void OnActivity(CopilotActivity activity, StageStats stats, StageEventLog eventLog, Action<string, string> log)
    {
        var prefix = activity.FromSubagent ? "[subagent] " : "";
        switch (activity)
        {
            case CopilotActivity.AssistantMessage message:
                eventLog.Write(new JsonObject { ["type"] = "assistant.message", ["sub"] = activity.FromSubagent, ["model"] = message.Model, ["content"] = message.Content });
                if (!activity.FromSubagent && !string.IsNullOrWhiteSpace(message.Content))
                {
                    stats.LastAssistantText = message.Content.Trim();
                    stats.Model = message.Model ?? stats.Model;
                }

                if (_options.LogAssistantText && !string.IsNullOrWhiteSpace(message.Content))
                {
                    log($"{prefix}assistant: {LogText.Clip(message.Content, 300)}", "debug");
                }

                break;

            case CopilotActivity.ToolStarted tool:
                eventLog.Write(new JsonObject { ["type"] = "tool.start", ["sub"] = activity.FromSubagent, ["id"] = tool.ToolCallId, ["tool"] = tool.ToolName, ["arguments"] = tool.Arguments?.DeepClone() });
                Interlocked.Increment(ref stats.ToolCalls);
                stats.ToolNames[tool.ToolCallId] = tool.ToolName;
                log($"{prefix}→ {tool.ToolName} {LogText.SummarizeArguments(tool.Arguments)}", "info");
                break;

            case CopilotActivity.ToolCompleted done:
                eventLog.Write(new JsonObject { ["type"] = "tool.complete", ["sub"] = activity.FromSubagent, ["id"] = done.ToolCallId, ["success"] = done.Success, ["error"] = done.ErrorMessage });
                if (!done.Success)
                {
                    Interlocked.Increment(ref stats.ToolErrors);
                    var name = stats.ToolNames.TryGetValue(done.ToolCallId, out var n) ? n : "tool";
                    log($"{prefix}✗ {name}: {LogText.Clip(done.ErrorMessage ?? "failed", 300)}", "warn");
                }

                break;

            case CopilotActivity.Usage usage:
                eventLog.Write(new JsonObject
                {
                    ["type"] = "usage", ["sub"] = activity.FromSubagent, ["model"] = usage.Model, ["input_tokens"] = usage.InputTokens,
                    ["output_tokens"] = usage.OutputTokens, ["cache_read_tokens"] = usage.CacheReadTokens, ["reasoning_tokens"] = usage.ReasoningTokens,
                });
                lock (stats)
                {
                    stats.ModelCalls++;
                    stats.InputTokens += usage.InputTokens;
                    stats.OutputTokens += usage.OutputTokens;
                    stats.CacheReadTokens += usage.CacheReadTokens;
                    stats.ReasoningTokens += usage.ReasoningTokens;
                }

                break;

            case CopilotActivity.Error error:
                eventLog.Write(new JsonObject { ["type"] = "error", ["sub"] = activity.FromSubagent, ["message"] = error.Message, ["error_type"] = error.ErrorType, ["status"] = error.StatusCode });
                if (!activity.FromSubagent)
                {
                    stats.LastError = error;
                }

                log($"{prefix}copilot error: {error.Message}", activity.FromSubagent ? "warn" : "error");
                break;

            case CopilotActivity.Warning warning:
                eventLog.Write(new JsonObject { ["type"] = "warning", ["message"] = warning.Message });
                log($"{prefix}copilot warning: {warning.Message}", "warn");
                break;

            case CopilotActivity.UsageCheckpoint checkpoint:
                stats.NanoAiu = checkpoint.TotalNanoAiu;
                break;
        }
    }

    internal static bool IsRetryable(CopilotActivity.Error error)
    {
        if (error.StatusCode is { } status)
        {
            return status is 408 or 409 or 429 || status >= 500;
        }

        var type = error.ErrorType?.ToLowerInvariant() ?? "";
        if (type.Contains("auth", StringComparison.Ordinal) || type.Contains("quota", StringComparison.Ordinal) || type.Contains("policy", StringComparison.Ordinal))
        {
            return false;
        }

        return CliAgentException.IsRetryableMessage(error.Message);
    }

    private static async Task AbortQuietlyAsync(ICopilotConversation conversation)
    {
        try
        {
            await conversation.AbortAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: the stage is already failing or cancelled.
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private sealed class ConversationHolder
    {
        public ICopilotConversation? Conversation { get; set; }

        public string? Model { get; set; }

        public ToolHookSettings? Hooks { get; set; }

        /// <summary>Last cumulative AI-unit total reported for the session.</summary>
        public double NanoAiu { get; set; }
    }

    private sealed class StageStats
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ToolNames = new(StringComparer.Ordinal);
        public int ToolCalls;
        public int ToolErrors;
        public int ModelCalls;
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadTokens;
        public long ReasoningTokens;
        public double? NanoAiu;
        public string? Model;
        public string LastAssistantText = "";
        public CopilotActivity.Error? LastError;
    }
}

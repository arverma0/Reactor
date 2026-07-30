using System.Text.Json.Nodes;
using Reactor.Backends;
using Reactor.Backends.Cli;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Llm;

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

    /// <summary>
    /// How often a running stage looks for <c>steering.md</c> in its stage directory (written by a supervisor or the
    /// server's steer endpoint); its text is sent to the Copilot turn in progress.
    /// </summary>
    public TimeSpan SteeringPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a stage that ends (or is aborted) waits for the runtime's usage checkpoint covering its last model calls, so
    /// its AI units are attributed to it rather than to the next stage or attempt on the same session (default 3 s).
    /// </summary>
    public TimeSpan UsageSettleTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>A Copilot stage failed. <see cref="Retryable"/> tells the engine whether a retry may help.</summary>
public sealed class CopilotStageException(string message, bool retryable, Exception? innerException = null) : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Runs codergen stages through the GitHub Copilot SDK: one Copilot runtime per backend, one Copilot session per stage
/// (or per <c>full</c>-fidelity thread, continuing the conversation). The stage's standing instructions (see
/// <see cref="StageInstructions"/>) are appended to the session's system message when the session is created; a later
/// stage on the same thread whose instructions differ gets them at the top of its prompt. Tool permissions are approved
/// automatically, <c>tool_hooks</c> run through the SDK's pre/post tool
/// hooks, and the agent may write <c>status.json</c>. A <c>steering.md</c> written into the stage directory while the
/// stage runs is sent to the turn in progress. Activity goes to the stage log and <c>{stage}/copilot-events.jsonl</c>.
/// Each attempt reports its own AI units (from the runtime's cumulative usage checkpoints, falling back to per-call
/// usage), and an attempt that fails, times out, or is cancelled attaches its usage to the exception.
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
        _threads = new ThreadLeasePool<ConversationHolder>(holder => holder.DisposeAsync());
    }

    public bool SupportsStatusFile => true;

    /// <summary>A <c>steering.md</c> in the stage directory is sent to the Copilot turn in progress.</summary>
    public bool SupportsSteering => true;

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

        var instructions = StageInstructions.ResolveAndRecord(node, run?.Graph, stage.StageDirectory);
        var key = _options.ReuseThreadSessions ? stage.ThreadKey("copilot-sdk") : null;
        await using var lease = await _threads.AcquireAsync(key, () => new ConversationHolder()).ConfigureAwait(false);
        var holder = lease.Value;
        holder.Hooks = ToolHookSettings.Resolve(node, run?.Graph, stage, Log);

        var resumed = holder.Conversation is not null;
        if (holder.Conversation is null)
        {
            holder.Attach(await CreateConversationAsync(holder, stage, model, effort, instructions, cancellationToken).ConfigureAwait(false));
            holder.Model = model;
            holder.Instructions = instructions;
        }
        else if (model is not null && !string.Equals(model, holder.Model, StringComparison.Ordinal))
        {
            await holder.Conversation.SetModelAsync(model, effort, cancellationToken).ConfigureAwait(false);
            holder.Model = model;
        }

        var conversation = holder.Conversation!;
        Log($"copilot-sdk: {(resumed ? "continuing" : "started")} session {conversation.SessionId}" + (model is null ? "" : $" ({model})"), "info");
        if (lease.ContextLost)
        {
            prompt = ThreadContinuity.Recover(prompt, stage, context, Log);
        }

        // The system message is fixed when a session is created; a continued session gets changed instructions in the prompt.
        if (resumed && instructions.Length > 0 && !string.Equals(instructions, holder.Instructions, StringComparison.Ordinal))
        {
            prompt = StageInstructions.PromptSection(instructions) + prompt;
            holder.Instructions = instructions;
        }

        var stats = new StageStats();
        var baseline = holder.StartAttempt();
        Dictionary<string, object?> Metadata(double? nanoAiu)
        {
            var actualModel = stats.Model ?? model;
            var metadata = new Dictionary<string, object?>
            {
                ["backend"] = "copilot-sdk",
                ["model"] = actualModel,
                ["requested_model"] = model,
                ["session_id"] = conversation.SessionId,
                ["thread_id"] = stage.ThreadId,
                ["resumed"] = resumed,
                ["thread_context"] = ThreadContinuity.Describe(stage, !resumed, lease.ContextLost),
                ["attempt"] = stage.Attempt,
                ["reasoning_effort"] = effort,
                ["tool_calls"] = stats.ToolCalls,
                ["tool_errors"] = stats.ToolErrors,
                [OutputLimit.MetadataKey] = stats.OutputLimitTruncations,
                ["model_calls"] = stats.ModelCalls,
                ["input_tokens"] = stats.InputTokens,
                ["output_tokens"] = stats.OutputTokens,
                ["cache_read_tokens"] = stats.CacheReadTokens,
                ["cache_write_tokens"] = stats.CacheWriteTokens,
                ["reasoning_tokens"] = stats.ReasoningTokens,
            };
            if (nanoAiu is { } units)
            {
                metadata["ai_units"] = units / 1e9;
            }

            StageCost.AddCopilot(metadata, actualModel, tokenEstimateUsd: stats.TokenEstimateUsd);
            return metadata;
        }

        async Task<Dictionary<string, object?>> FailureMetadataAsync(bool settle)
        {
            var nanoAiu = await SettleUsageAsync(holder, stats, baseline, settle ? _options.UsageSettleTimeout : TimeSpan.Zero).ConfigureAwait(false);
            var metadata = Metadata(nanoAiu);
            metadata[BackendMetadata.LastAssistantText] = stats.LastAssistantText;
            return metadata;
        }

        string? reply;
        double? settledNanoAiu;
        using (var eventLog = new StageEventLog(stage.StageDirectory, "copilot-events.jsonl"))
        using (conversation.Subscribe(activity => OnActivity(activity, stats, eventLog, Log)))
        using (var steeringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            eventLog.WriteAttempt(stage.Attempt);
            var timeout = stage.Timeout is { } nodeTimeout && nodeTimeout > TimeSpan.Zero ? nodeTimeout + TimeSpan.FromSeconds(30) : _options.MaxStageDuration;
            var steering = Task.CompletedTask;
            try
            {
                var turn = conversation.SendAndWaitAsync(prompt, timeout, cancellationToken);
                steering = WatchSteeringAsync(stage, conversation, turn, eventLog, Log, steeringCts.Token);
                reply = await turn.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await AbortQuietlyAsync(conversation).ConfigureAwait(false);
                BackendMetadata.Attach(ex, await FailureMetadataAsync(settle: true).ConfigureAwait(false));
                throw;
            }
            catch (TimeoutException ex)
            {
                await AbortQuietlyAsync(conversation).ConfigureAwait(false);
                throw BackendMetadata.Attach(
                    new CopilotStageException($"Copilot did not finish within {timeout}.", retryable: true, ex),
                    await FailureMetadataAsync(settle: true).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The session (or its connection) is gone; drop it so the next stage starts fresh.
                var metadata = await FailureMetadataAsync(settle: false).ConfigureAwait(false);
                if (lease.Pooled)
                {
                    await _threads.InvalidateAsync(lease).ConfigureAwait(false);
                }

                throw BackendMetadata.Attach(new CopilotStageException($"Copilot session failed: {ex.Message}", retryable: true, ex), metadata);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not CopilotStageException)
            {
                BackendMetadata.Attach(ex, await FailureMetadataAsync(settle: false).ConfigureAwait(false));
                throw;
            }
            finally
            {
                await steeringCts.CancelAsync().ConfigureAwait(false);
                await steering.ConfigureAwait(false);
            }

            // While still subscribed, wait for the checkpoint that covers the turn's last calls: always after an error, else
            // only on sessions that send checkpoints (so a runtime without them adds no delay).
            var failed = stats.LastError is not null && string.IsNullOrWhiteSpace(reply) && string.IsNullOrWhiteSpace(stats.LastAssistantText);
            settledNanoAiu = await SettleUsageAsync(holder, stats, baseline, failed || holder.CheckpointsSeen > 0 ? _options.UsageSettleTimeout : TimeSpan.Zero).ConfigureAwait(false);
        }

        var text = !string.IsNullOrWhiteSpace(reply) ? reply.Trim() : stats.LastAssistantText;
        if (stats.LastError is { } error && string.IsNullOrWhiteSpace(text))
        {
            var message = $"Copilot reported an error: {error.Message}";
            Log(message, "error");
            var metadata = Metadata(settledNanoAiu);
            metadata[BackendMetadata.LastAssistantText] = stats.LastAssistantText;
            throw BackendMetadata.Attach(new CopilotStageException(message, IsRetryable(error)), metadata);
        }

        if (model is not null && stats.Model is { } reported && !string.Equals(reported, model, StringComparison.OrdinalIgnoreCase))
        {
            Log($"copilot-sdk: the stage ran on '{reported}' instead of the requested '{model}'", "warn");
        }

        return CodergenResult.FromText(string.IsNullOrWhiteSpace(text) ? "(The agent finished without a text response.)" : text).WithMetadata(Metadata(settledNanoAiu));
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

    private Task<ICopilotConversation> CreateConversationAsync(ConversationHolder holder, StageSettings stage, string? model, string? effort, string instructions, CancellationToken cancellationToken)
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
                SystemMessage = instructions.Length > 0 ? instructions : null,
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
                    if (OutputLimit.IsTruncation(done.ErrorMessage))
                    {
                        Interlocked.Increment(ref stats.OutputLimitTruncations);
                        log(prefix + OutputLimit.Warning(name), "warn");
                    }
                }

                break;

            case CopilotActivity.Usage usage:
                eventLog.Write(new JsonObject
                {
                    ["type"] = "usage", ["sub"] = activity.FromSubagent, ["model"] = usage.Model, ["input_tokens"] = usage.InputTokens,
                    ["output_tokens"] = usage.OutputTokens, ["cache_read_tokens"] = usage.CacheReadTokens, ["cache_write_tokens"] = usage.CacheWriteTokens,
                    ["reasoning_tokens"] = usage.ReasoningTokens, ["nano_aiu"] = usage.NanoAiu,
                });
                stats.AddUsage(usage, PriceTable.Copilot.Find(usage.Model));
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
                eventLog.Write(new JsonObject { ["type"] = "usage.checkpoint", ["total_nano_aiu"] = checkpoint.TotalNanoAiu });
                stats.AddCheckpoint(checkpoint.TotalNanoAiu);
                break;
        }
    }

    /// <summary>
    /// Settles the AI units of the attempt that started at <paramref name="baseline"/> (the session's cumulative nano AI
    /// units already attributed to earlier stages or attempts) and advances the session's attribution, so the next attempt
    /// on the session reports only its own units. When model calls ran after the last usage checkpoint, waits up to
    /// <paramref name="wait"/> for the checkpoint that covers them; calls still uncovered are counted from their per-call
    /// usage (and remembered, so the next checkpoint does not charge them to the next attempt). Returns null when the
    /// runtime reported no AI units at all.
    /// </summary>
    private static async Task<double?> SettleUsageAsync(ConversationHolder holder, StageStats stats, double baseline, TimeSpan wait)
    {
        if (wait > TimeSpan.Zero && stats.UsageAfterCheckpoint)
        {
            var deadline = DateTime.UtcNow + wait;
            while (stats.UsageAfterCheckpoint && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None).ConfigureAwait(false);
            }
        }

        lock (stats)
        {
            double? units = null;
            if (stats.CheckpointNanoAiu is { } total)
            {
                // The session's own accounting, plus per-call units for calls after its last checkpoint.
                var tail = stats.UsageAfterCheckpoint && stats.PerCallSinceCheckpointComplete ? stats.PerCallNanoSinceCheckpoint : 0;
                units = Math.Max(0, total + tail - baseline);
                holder.CompleteAttempt(Math.Max(baseline, total + tail));
            }
            else if (stats.ModelCalls > 0 && stats.PerCallComplete)
            {
                units = stats.PerCallNanoAiu;
                holder.CompleteAttempt(baseline + stats.PerCallNanoAiu);
            }

            return units;
        }
    }

    /// <summary>
    /// Sends steering (<c>steering.md</c> in the stage directory) to the Copilot turn in progress as an immediate message,
    /// which the runtime folds into the running turn. Nothing is taken once the turn has finished.
    /// </summary>
    private Task WatchSteeringAsync(StageSettings stage, ICopilotConversation conversation, Task turn, StageEventLog eventLog, Action<string, string> log, CancellationToken cancellationToken) =>
        SteeringFile.WatchAsync(
            stage.StageDirectory,
            _options.SteeringPollInterval,
            async text =>
            {
                eventLog.Write(new JsonObject { ["type"] = "steering", ["content"] = text, ["sent"] = !turn.IsCompleted });
                if (turn.IsCompleted)
                {
                    log("steering arrived after Copilot finished the stage; it was not sent", "warn");
                    return;
                }

                try
                {
                    await conversation.SteerAsync(text, cancellationToken).ConfigureAwait(false);
                    log($"steering sent to Copilot: {LogText.Clip(text)}", "info");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"copilot-sdk: could not send steering: {ex.Message}", "warn");
                }
            },
            accepting: () => !turn.IsCompleted,
            cancellationToken);

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

    /// <summary>
    /// A stage's (or a thread's) Copilot conversation plus the session-level accounting: the session's latest cumulative
    /// usage checkpoint (tracked for the conversation's whole life, so a checkpoint that arrives between attempts is not
    /// charged to the next one) and the cumulative units already attributed to finished attempts.
    /// </summary>
    private sealed class ConversationHolder : IAsyncDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _watch;
        private double _sessionNanoAiu;
        private double _attributedNanoAiu;
        private int _checkpoints;

        public ICopilotConversation? Conversation { get; private set; }

        public string? Model { get; set; }

        public ToolHookSettings? Hooks { get; set; }

        /// <summary>The standing instructions the session has (system message at creation, or the last prompt section).</summary>
        public string? Instructions { get; set; }

        /// <summary>Usage checkpoints the session has sent so far.</summary>
        public int CheckpointsSeen => Volatile.Read(ref _checkpoints);

        public void Attach(ICopilotConversation conversation)
        {
            Conversation = conversation;
            _watch = conversation.Subscribe(activity =>
            {
                if (activity is CopilotActivity.UsageCheckpoint checkpoint)
                {
                    lock (_gate)
                    {
                        _sessionNanoAiu = Math.Max(_sessionNanoAiu, checkpoint.TotalNanoAiu);
                        _checkpoints++;
                    }
                }
            });
        }

        /// <summary>The cumulative units that belong to earlier attempts: the attempt's baseline.</summary>
        public double StartAttempt()
        {
            lock (_gate)
            {
                return Math.Max(_attributedNanoAiu, _sessionNanoAiu);
            }
        }

        /// <summary>Records that the session's units up to <paramref name="attributedNanoAiu"/> are attributed.</summary>
        public void CompleteAttempt(double attributedNanoAiu)
        {
            lock (_gate)
            {
                _attributedNanoAiu = Math.Max(_attributedNanoAiu, attributedNanoAiu);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _watch?.Dispose();
            if (Conversation is { } conversation)
            {
                await conversation.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>One attempt's activity and usage. Usage fields are guarded by locking the instance.</summary>
    private sealed class StageStats
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ToolNames = new(StringComparer.Ordinal);
        public int ToolCalls;
        public int ToolErrors;
        public int OutputLimitTruncations;
        public int ModelCalls;
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long ReasoningTokens;
        public string? Model;
        public string LastAssistantText = "";
        public CopilotActivity.Error? LastError;

        /// <summary>The latest cumulative checkpoint seen during the attempt.</summary>
        public double? CheckpointNanoAiu;

        /// <summary>True while model calls have completed since the last checkpoint (their units are not in it yet).</summary>
        public bool UsageAfterCheckpoint;

        /// <summary>Per-call units of all the attempt's calls, and whether every call reported them.</summary>
        public double PerCallNanoAiu;
        public bool PerCallComplete = true;

        /// <summary>Per-call units of the calls since the last checkpoint, and whether every one reported them.</summary>
        public double PerCallNanoSinceCheckpoint;
        public bool PerCallSinceCheckpointComplete = true;

        /// <summary>Tokens × price-table estimate (per call, so long-context tiers apply), when every call could be priced.</summary>
        public double? TokenEstimateUsd = 0;

        public void AddUsage(CopilotActivity.Usage usage, ModelPrice? price)
        {
            lock (this)
            {
                ModelCalls++;
                InputTokens += usage.InputTokens;
                OutputTokens += usage.OutputTokens;
                CacheReadTokens += usage.CacheReadTokens;
                CacheWriteTokens += usage.CacheWriteTokens;
                ReasoningTokens += usage.ReasoningTokens;
                UsageAfterCheckpoint = true;
                if (usage.NanoAiu is { } nano)
                {
                    PerCallNanoAiu += nano;
                    PerCallNanoSinceCheckpoint += nano;
                }
                else
                {
                    PerCallComplete = false;
                    PerCallSinceCheckpointComplete = false;
                }

                var callUsd = price?.EstimateUsd(new TokenUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens), usage.InputTokens);
                TokenEstimateUsd = callUsd is { } usd && TokenEstimateUsd is { } sum ? sum + usd : null;
            }
        }

        public void AddCheckpoint(double totalNanoAiu)
        {
            lock (this)
            {
                CheckpointNanoAiu = Math.Max(CheckpointNanoAiu ?? 0, totalNanoAiu);
                UsageAfterCheckpoint = false;
                PerCallNanoSinceCheckpoint = 0;
                PerCallSinceCheckpointComplete = true;
            }
        }
    }
}

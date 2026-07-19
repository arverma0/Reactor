using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Agent.Prompts;
using Reactor.Agent.Subagents;
using Reactor.Llm;
using Reactor.Llm.Utilities;

namespace Reactor.Agent;

/// <summary>Construction options for a <see cref="Session"/>.</summary>
public sealed record SessionOptions
{
    /// <summary>Session id (default: a new GUID).</summary>
    public string? Id { get; init; }

    /// <summary>Subagent nesting depth (0 for a top-level session).</summary>
    public int Depth { get; init; }

    public IReadOnlyList<IToolInterceptor> Interceptors { get; init; } = [];

    /// <summary>A pre-captured environment snapshot (subagents reuse their parent's).</summary>
    public EnvironmentSnapshot? Environment { get; init; }

    /// <summary>Pre-discovered project instructions; when null they are discovered at session start.</summary>
    public string? ProjectDocs { get; init; }

    /// <summary>Discover AGENTS.md and provider-specific instruction files (default true).</summary>
    public bool DiscoverProjectDocs { get; init; } = true;

    /// <summary>Capture git branch/status/commits for the system prompt (default true).</summary>
    public bool CaptureGitContext { get; init; } = true;
}

/// <summary>
/// The coding agent loop (spec section 2): holds the conversation, calls the LLM through <see cref="Client"/>, executes
/// tool calls through the profile's registry and the execution environment, and emits events. Hosts can steer the agent
/// mid-task, queue follow-ups, change configuration between turns, and abort.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Turn> _history = [];
    private readonly ConcurrentQueue<string> _steering = new();
    private readonly ConcurrentQueue<string> _followUps = new();
    private readonly SemaphoreSlim _processing = new(1, 1);
    private readonly ToolRegistry _sessionTools = new();
    private readonly SessionOptions _options;
    private readonly SubAgentManager? _subagents;
    private CancellationTokenSource? _processingCts;
    private EnvironmentSnapshot? _snapshot;
    private string? _projectDocs;
    private bool _started;
    private bool _contextWarningEmitted;
    private volatile bool _aborting;
    private int _shutdown;
    private Usage _totalUsage = Usage.Empty;
    private volatile SessionState _state = SessionState.Idle;

    public Session(IProviderProfile profile, IExecutionEnvironment environment, Client client, SessionConfig? config = null, SessionOptions? options = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Config = config ?? new SessionConfig();
        _options = options ?? new SessionOptions();
        Id = _options.Id ?? Guid.NewGuid().ToString();
        Depth = _options.Depth;
        _snapshot = _options.Environment;
        _projectDocs = _options.ProjectDocs;

        if (Depth < Config.MaxSubagentDepth)
        {
            _subagents = new SubAgentManager(this);
            foreach (var tool in SubagentTools.Create())
            {
                _sessionTools.Register(tool);
            }
        }
    }

    public string Id { get; }

    public IProviderProfile Profile { get; }

    public IExecutionEnvironment Environment { get; }

    public Client Client { get; }

    public SessionConfig Config { get; }

    public int Depth { get; }

    public SessionState State => _state;

    public SessionEventEmitter Events { get; } = new();

    internal IReadOnlyList<IToolInterceptor> Interceptors => _options.Interceptors;

    internal EnvironmentSnapshot? Snapshot => _snapshot;

    internal string? ProjectDocsText => _projectDocs;

    /// <summary>A snapshot of the conversation history.</summary>
    public IReadOnlyList<Turn> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToList();
            }
        }
    }

    /// <summary>Token usage aggregated across all LLM calls.</summary>
    public Usage TotalUsage
    {
        get
        {
            lock (_gate)
            {
                return _totalUsage;
            }
        }
    }

    /// <summary>Number of LLM responses (assistant turns) so far.</summary>
    public int AssistantTurnCount
    {
        get
        {
            lock (_gate)
            {
                return _history.Count(t => t is AssistantTurn);
            }
        }
    }

    /// <summary>Text of the most recent assistant response.</summary>
    public string? LastAssistantText
    {
        get
        {
            lock (_gate)
            {
                return _history.OfType<AssistantTurn>().LastOrDefault()?.Content;
            }
        }
    }

    /// <summary>The error that closed the session, if any.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Session-scoped tools (subagent tools) in addition to the profile's tools.</summary>
    public ToolRegistry SessionTools => _sessionTools;

    /// <summary>
    /// Submits user input and runs the agentic loop until natural completion, a limit, or an abort; then processes any
    /// queued follow-ups. Concurrent submissions are serialized.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is closed.</exception>
    /// <exception cref="SdkError">A non-recoverable LLM error occurred (the session is closed).</exception>
    public async Task SubmitAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ThrowIfClosed();
        await _processing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _processingCts = cts;
            await EnsureStartedAsync(cts.Token).ConfigureAwait(false);

            string? next = input;
            while (next is not null)
            {
                await ProcessInputAsync(next, cts.Token).ConfigureAwait(false);
                if (_state == SessionState.Closed)
                {
                    return;
                }

                next = _followUps.TryDequeue(out var followUp) ? followUp : null;
            }

            if (_state != SessionState.AwaitingInput)
            {
                _state = SessionState.Idle;
            }

            Emit(EventKind.ProcessingEnd, ("state", _state.ToString()));
        }
        catch (OperationCanceledException) when (_aborting || _state == SessionState.Closed || cancellationToken.IsCancellationRequested)
        {
            await ShutdownAsync("aborted").ConfigureAwait(false);
            throw new AbortError("The session was aborted.");
        }
        finally
        {
            _processingCts = null;
            _processing.Release();
        }
    }

    /// <summary>Queues a message injected after the current tool round (or at the start of the next input).</summary>
    public void Steer(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _steering.Enqueue(message);
    }

    /// <summary>Queues a message processed as a new input after the current input completes.</summary>
    public void FollowUp(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _followUps.Enqueue(message);
    }

    /// <summary>
    /// Aborts any in-flight work: cancels the LLM call, kills running processes, closes subagents, emits SESSION_END,
    /// and transitions to CLOSED.
    /// </summary>
    public async Task AbortAsync()
    {
        if (_state == SessionState.Closed)
        {
            return;
        }

        _aborting = true;
        try
        {
            _processingCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Let the loop unwind (cancelling the LLM stream and killing its processes) before the final cleanup; bounded so an
        // abort issued from inside the loop (e.g. by an event observer) cannot deadlock.
        var acquired = await _processing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            await ShutdownAsync("aborted").ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _processing.Release();
            }
        }
    }

    /// <summary>Closes the session (aborting in-flight work if any).</summary>
    public Task CloseAsync() => _state == SessionState.Processing ? AbortAsync() : ShutdownAsync("closed");

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _snapshot ??= await EnvironmentSnapshot.CaptureAsync(Environment, _options.CaptureGitContext, cancellationToken).ConfigureAwait(false);
        if (_projectDocs is null && _options.DiscoverProjectDocs)
        {
            var fileNames = Profile.ProjectDocFileNames.Contains("AGENTS.md", StringComparer.Ordinal)
                ? Profile.ProjectDocFileNames
                : ["AGENTS.md", .. Profile.ProjectDocFileNames];
            _projectDocs = await ProjectDocs.DiscoverAsync(Environment, _snapshot.GitRoot, fileNames, cancellationToken).ConfigureAwait(false);
        }

        Emit(EventKind.SessionStart, ("model", Profile.Model), ("profile", Profile.Id), ("depth", Depth));
    }

    private async Task ProcessInputAsync(string input, CancellationToken cancellationToken)
    {
        _state = SessionState.Processing;
        _contextWarningEmitted = false;
        AddTurn(new UserTurn(input));
        Emit(EventKind.UserInput, ("content", input));
        DrainSteering();

        var round = 0;
        while (true)
        {
            if (Config.MaxToolRoundsPerInput > 0 && round >= Config.MaxToolRoundsPerInput)
            {
                Emit(EventKind.TurnLimit, ("round", round), ("limit", "max_tool_rounds_per_input"));
                break;
            }

            if (Config.MaxTurns > 0 && AssistantTurnCount >= Config.MaxTurns)
            {
                Emit(EventKind.TurnLimit, ("total_turns", AssistantTurnCount), ("limit", "max_turns"));
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            Response response;
            try
            {
                response = await CallModelAsync(BuildRequest(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AbortError or OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Aborted.", ex, cancellationToken);
            }
            catch (ContextLengthError ex)
            {
                // Spec: context overflow is a warning signal; the session stays usable (no automatic compaction).
                LastError = ex;
                Emit(EventKind.Warning, ("message", "Context window exceeded: " + ex.Message), ("error_type", nameof(ContextLengthError)));
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex;
                Emit(EventKind.Error, ("error", ex.Message), ("error_type", ex.GetType().Name), ("retryable", ex is SdkError { Retryable: true }));
                await ShutdownAsync("error").ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            var turn = new AssistantTurn(response.Message, response.Usage, response.Id);
            AddTurn(turn);
            lock (_gate)
            {
                _totalUsage += response.Usage;
            }

            Emit(EventKind.AssistantTextEnd, ("text", response.Text), ("reasoning", response.Reasoning), ("usage", response.Usage), ("finish_reason", response.FinishReason.Reason));
            CheckContextUsage();

            var calls = response.ToolCalls;
            if (calls.Count == 0)
            {
                if (response.Text.TrimEnd().EndsWith('?'))
                {
                    _state = SessionState.AwaitingInput;
                }

                break;
            }

            round++;
            var results = await ExecuteToolCallsAsync(calls, cancellationToken).ConfigureAwait(false);
            AddTurn(new ToolResultsTurn(results));
            DrainSteering();

            if (Config.EnableLoopDetection && LoopDetector.IsLooping(RecentToolSignatures(), Config.LoopDetectionWindow))
            {
                var warning = $"Loop detected: the last {Config.LoopDetectionWindow} tool calls follow a repeating pattern. Try a different approach.";
                AddTurn(new SteeringTurn(warning));
                Emit(EventKind.LoopDetection, ("message", warning));
            }
        }
    }

    private Request BuildRequest()
    {
        var extraTools = _sessionTools.Definitions().Where(t => !Profile.ToolRegistry.Contains(t.Name)).ToList();
        var tools = Profile.Tools().Concat(extraTools).ToList();
        var systemPrompt = Profile.BuildSystemPrompt(_snapshot, _projectDocs, Config, extraTools);
        var messages = new List<Message> { Message.System(systemPrompt) };
        messages.AddRange(ConvertHistory());
        return new Request
        {
            Model = Profile.Model,
            Messages = messages,
            Provider = Profile.ProviderName,
            Tools = tools.Count > 0 ? tools : null,
            ToolChoice = tools.Count > 0 ? ToolChoice.Auto : null,
            ReasoningEffort = Profile.SupportsReasoning ? Config.ReasoningEffort : null,
            ProviderOptions = Profile.ProviderOptions(Config),
            MaxTokens = Config.MaxTokens,
        };
    }

    private List<Message> ConvertHistory()
    {
        var messages = new List<Message>();
        foreach (var turn in History)
        {
            switch (turn)
            {
                case UserTurn user:
                    messages.Add(Message.User(user.Content));
                    break;
                case AssistantTurn assistant:
                    messages.Add(assistant.Message);
                    break;
                case ToolResultsTurn tools:
                    messages.AddRange(tools.Results.Select(r => r.ToMessage()));
                    break;
                case SteeringTurn steering:
                    messages.Add(Message.User(steering.Content));
                    break;
                case SystemTurn system:
                    messages.Add(Message.User("[System notice] " + system.Content));
                    break;
            }
        }

        return messages;
    }

    private async Task<Response> CallModelAsync(Request request, CancellationToken cancellationToken)
    {
        if (!Config.StreamResponses || !Profile.SupportsStreaming)
        {
            var response = await Retry.ExecuteAsync(ct => Client.CompleteAsync(request, ct), Config.LlmRetryPolicy, cancellationToken).ConfigureAwait(false);
            if (response.Text.Length > 0)
            {
                Emit(EventKind.AssistantTextStart);
            }

            return response;
        }

        for (var attempt = 0; ; attempt++)
        {
            var accumulator = new StreamAccumulator();
            var delivered = false;
            var textStarted = false;
            try
            {
                await foreach (var evt in Client.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    delivered = true;
                    accumulator.Process(evt);
                    if (evt.Type == StreamEventType.TextDelta && evt.Delta is { Length: > 0 } delta)
                    {
                        if (!textStarted)
                        {
                            textStarted = true;
                            Emit(EventKind.AssistantTextStart);
                        }

                        Emit(EventKind.AssistantTextDelta, ("delta", delta));
                    }
                    else if (evt.Type == StreamEventType.Error)
                    {
                        throw evt.Error ?? new StreamError("The stream reported an error.");
                    }
                }

                if (!accumulator.IsFinished)
                {
                    throw new StreamError("The stream ended without a finish event.");
                }

                return accumulator.Response;
            }
            catch (Exception error) when (!delivered && error is not AbortError && !cancellationToken.IsCancellationRequested &&
                                          attempt < Config.LlmRetryPolicy.MaxRetries && Config.LlmRetryPolicy.IsRetryable(error))
            {
                var delay = Config.LlmRetryPolicy.CalculateDelay(attempt);
                if (error is ProviderError { RetryAfter: double retryAfter })
                {
                    if (retryAfter > Config.LlmRetryPolicy.MaxDelay)
                    {
                        throw;
                    }

                    delay = TimeSpan.FromSeconds(retryAfter);
                }

                Config.LlmRetryPolicy.OnRetry?.Invoke(error, attempt + 1, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<ToolResult>> ExecuteToolCallsAsync(IReadOnlyList<ToolCall> calls, CancellationToken cancellationToken)
    {
        if (Profile.SupportsParallelToolCalls && calls.Count > 1)
        {
            return await Task.WhenAll(calls.Select(c => Task.Run(() => ExecuteSingleToolAsync(c, cancellationToken), cancellationToken))).ConfigureAwait(false);
        }

        var results = new List<ToolResult>(calls.Count);
        foreach (var call in calls)
        {
            results.Add(await ExecuteSingleToolAsync(call, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<ToolResult> ExecuteSingleToolAsync(ToolCall call, CancellationToken cancellationToken)
    {
        Emit(EventKind.ToolCallStart, ("tool_name", call.Name), ("call_id", call.Id), ("arguments", call.RawArguments ?? call.Arguments.ToJsonString()));

        var registered = Profile.ToolRegistry.Get(call.Name) ?? _sessionTools.Get(call.Name);
        if (registered is null)
        {
            return Fail(call, $"Unknown tool: {call.Name}");
        }

        if (call.ArgumentsError is not null)
        {
            return Fail(call, $"Invalid arguments for tool: {call.Name}: {call.ArgumentsError}");
        }

        var errors = JsonSchemaValidator.Validate(call.Arguments, registered.Definition.Parameters);
        if (errors.Count > 0)
        {
            return Fail(call, $"Invalid arguments for tool: {call.Name}: {string.Join("; ", errors)}");
        }

        var info = new ToolCallInfo(Id, call.Id, call.Name, (JsonObject)call.Arguments.DeepClone(), Environment.WorkingDirectory);
        foreach (var interceptor in _options.Interceptors)
        {
            var decision = await interceptor.BeforeAsync(info, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                var blocked = Fail(call, $"Tool call blocked: {decision.Reason ?? "denied by policy"}");
                await NotifyAfterAsync(info, blocked.ContentText, isError: true, cancellationToken).ConfigureAwait(false);
                return blocked;
            }
        }

        try
        {
            var context = new ToolContext
            {
                Environment = Environment,
                Config = Config,
                ToolCallId = call.Id,
                ToolName = call.Name,
                OnOutputDelta = delta => Emit(EventKind.ToolCallOutputDelta, ("call_id", call.Id), ("delta", delta)),
                Subagents = _subagents,
                ProfileShellTimeoutMs = Profile.DefaultShellTimeoutMs,
                CancellationToken = cancellationToken,
            };

            var output = await registered.Executor((JsonObject)call.Arguments.DeepClone(), context).ConfigureAwait(false);
            var truncated = ToolOutputTruncation.TruncateToolOutput(output.Text, call.Name, Config);
            Emit(EventKind.ToolCallEnd, ("tool_name", call.Name), ("call_id", call.Id), ("output", output.Text), ("truncated", truncated.Length != output.Text.Length));
            await NotifyAfterAsync(info, output.Text, isError: false, cancellationToken).ConfigureAwait(false);
            return new ToolResult(call.Id, JsonValue.Create(truncated)) { ImageData = output.ImageData, ImageMediaType = output.ImageMediaType };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var result = Fail(call, $"Tool error ({call.Name}): {ex.Message}");
            await NotifyAfterAsync(info, result.ContentText, isError: true, cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    private ToolResult Fail(ToolCall call, string message)
    {
        Emit(EventKind.ToolCallEnd, ("tool_name", call.Name), ("call_id", call.Id), ("error", message));
        return ToolResult.FromText(call.Id, message, isError: true);
    }

    private async Task NotifyAfterAsync(ToolCallInfo info, string output, bool isError, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _options.Interceptors)
        {
            try
            {
                await interceptor.AfterAsync(info, output, isError, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Emit(EventKind.Warning, ("message", $"Post-tool interceptor failed: {ex.Message}"));
            }
        }
    }

    private void DrainSteering()
    {
        while (_steering.TryDequeue(out var message))
        {
            AddTurn(new SteeringTurn(message));
            Emit(EventKind.SteeringInjected, ("content", message));
        }
    }

    private List<string> RecentToolSignatures()
    {
        lock (_gate)
        {
            return _history.OfType<AssistantTurn>()
                .SelectMany(t => t.ToolCalls)
                .Select(c => LoopDetector.Signature(c.Name, c.Arguments))
                .ToList();
        }
    }

    private void CheckContextUsage()
    {
        if (_contextWarningEmitted || Profile.ContextWindowSize <= 0)
        {
            return;
        }

        long chars = 0;
        lock (_gate)
        {
            foreach (var turn in _history)
            {
                chars += turn switch
                {
                    UserTurn u => u.Content.Length,
                    AssistantTurn a => a.Content.Length + a.ToolCalls.Sum(c => c.Arguments.ToJsonString().Length) + (a.Reasoning?.Length ?? 0),
                    ToolResultsTurn r => r.Results.Sum(x => x.ContentText.Length),
                    SteeringTurn s => s.Content.Length,
                    SystemTurn s => s.Content.Length,
                    _ => 0,
                };
            }
        }

        var approxTokens = chars / 4;
        if (approxTokens > Profile.ContextWindowSize * 0.8)
        {
            _contextWarningEmitted = true;
            var percent = (int)Math.Round(approxTokens * 100.0 / Profile.ContextWindowSize);
            Emit(EventKind.Warning, ("message", $"Context usage at ~{percent}% of context window"), ("approx_tokens", approxTokens));
        }
    }

    private async Task ShutdownAsync(string reason)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 1)
        {
            return;
        }

        _state = SessionState.Closed;
        try
        {
            await Environment.CleanupAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Emit(EventKind.Warning, ("message", $"Environment cleanup failed: {ex.Message}"));
        }

        if (_subagents is not null)
        {
            await _subagents.CloseAllAsync().ConfigureAwait(false);
        }

        Emit(EventKind.SessionEnd, ("state", SessionState.Closed.ToString()), ("reason", reason), ("usage", TotalUsage));
        Events.Complete();
    }

    private void AddTurn(Turn turn)
    {
        lock (_gate)
        {
            _history.Add(turn);
        }
    }

    private void ThrowIfClosed()
    {
        if (_state == SessionState.Closed)
        {
            throw new InvalidOperationException("The session is closed.");
        }
    }

    internal void Emit(EventKind kind, params (string Key, object? Value)[] data)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            payload[key] = value;
        }

        Events.Emit(new SessionEvent(kind, DateTimeOffset.UtcNow, Id, payload));
    }

    /// <summary>Renders the history as plain text (for logs and transcripts).</summary>
    public string Transcript()
    {
        var sb = new StringBuilder();
        foreach (var turn in History)
        {
            switch (turn)
            {
                case UserTurn u:
                    sb.Append("## User\n").Append(u.Content).Append("\n\n");
                    break;
                case AssistantTurn a:
                    sb.Append("## Assistant\n");
                    if (a.Content.Length > 0)
                    {
                        sb.Append(a.Content).Append('\n');
                    }

                    foreach (var call in a.ToolCalls)
                    {
                        sb.Append("-> ").Append(call.Name).Append(' ').Append(call.Arguments.ToJsonString()).Append('\n');
                    }

                    sb.Append('\n');
                    break;
                case ToolResultsTurn r:
                    foreach (var result in r.Results)
                    {
                        var text = result.ContentText;
                        sb.Append("<- ").Append(result.IsError ? "[error] " : "").Append(text.Length > 2000 ? text[..2000] + "…" : text).Append("\n\n");
                    }

                    break;
                case SteeringTurn s:
                    sb.Append("## Steering\n").Append(s.Content).Append("\n\n");
                    break;
                case SystemTurn s:
                    sb.Append("## System\n").Append(s.Content).Append("\n\n");
                    break;
            }
        }

        return sb.ToString();
    }
}

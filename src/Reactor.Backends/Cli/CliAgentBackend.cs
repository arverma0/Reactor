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
public sealed record CliInvocation(StageSettings Stage, string? Model, string? ReasoningEffort, string SessionId, bool Resume)
{
    /// <summary>
    /// The stage's effective standing instructions (see <see cref="StageInstructions"/>) when the backend passes them on
    /// the command line (<see cref="CliAgentBackend.TakesInstructionsAsArgument"/>); otherwise empty, and they are part of
    /// the prompt.
    /// </summary>
    public string Instructions { get; init; } = "";

    /// <summary>True when the command runs through <c>cmd.exe</c>, which cannot pass line breaks inside an argument.</summary>
    public bool ViaShell { get; init; }
}

/// <summary>The parsed outcome of one CLI run.</summary>
public sealed class CliRunSummary
{
    /// <summary>The agent's final response text.</summary>
    public string Text { get; set; } = "";

    /// <summary>The agent's latest non-empty message so far (handed to a retry when the run is cut off).</summary>
    public string LastAssistantText { get; set; } = "";

    /// <summary>True once the CLI's terminal result event was seen.</summary>
    public bool Completed { get; set; }

    public bool IsError { get; set; }

    public string? ErrorMessage { get; set; }

    public string? SessionId { get; set; }

    public string? Model { get; set; }

    public int ToolCalls { get; set; }

    public int ToolErrors { get; set; }

    /// <summary>Model calls observed in the output (when the CLI reports them).</summary>
    public int ModelCalls { get; set; }

    /// <summary>Tool calls whose arguments the model cut off at its output-token limit.</summary>
    public int OutputLimitTruncations { get; set; }

    /// <summary>Backend-specific metadata (usage, cost, ...).</summary>
    public Dictionary<string, object?> Metadata { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Usage counters the CLI reports cumulatively for its whole (possibly resumed) session, keyed by the per-stage
    /// <c>backend.json</c> field they become (e.g. <c>ai_units</c>, <c>input_tokens</c>).
    /// </summary>
    public Dictionary<string, double> Cumulative { get; } = new(StringComparer.Ordinal);
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
/// the same CLI session. Raw output is kept in <c>{stage}/cli-events.jsonl</c> (each attempt starts with a
/// <c>reactor.attempt</c> record) and stderr in <c>{stage}/stderr.log</c>. A run that fails, times out, or is cancelled
/// attaches the usage parsed so far to its exception (<see cref="BackendMetadata"/>), so the stage still records it.
/// </summary>
public abstract class CliAgentBackend : ICodergenBackend, IAsyncDisposable
{
    /// <summary>Longest instructions passed as a command-line argument; longer ones go into the prompt.</summary>
    protected const int MaxInstructionArgumentLength = 16_000;

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
        var instructions = StageInstructions.ResolveAndRecord(node, run?.Graph, stage.StageDirectory);
        var asArgument = instructions.Length > 0 && TakesInstructionsAsArgument(instructions);
        var invocation = new CliInvocation(stage, model, effort, thread.SessionId, thread.Started)
        {
            Instructions = asArgument ? instructions : "",
            ViaShell = command.ViaCmd,
        };
        var arguments = BuildArguments(invocation).Concat(_options.ExtraArguments).ToList();
        if (lease.ContextLost)
        {
            prompt = ThreadContinuity.Recover(prompt, stage, context, Log);
        }

        // Agents that cannot take system-level instructions get them in the prompt, once per session (or when they change).
        if (instructions.Length > 0 && !asArgument && (!invocation.Resume || !string.Equals(thread.Instructions, instructions, StringComparison.Ordinal)))
        {
            prompt = StageInstructions.PromptSection(instructions) + prompt;
            thread.Instructions = instructions;
        }

        Log($"{Name}: {(invocation.Resume ? "resuming" : "starting")} session {thread.SessionId}" + (model is null ? "" : $" ({model})"), "info");
        var parser = CreateParser();
        var startedAt = DateTimeOffset.UtcNow;
        var sessionId = thread.SessionId;
        Dictionary<string, object?> Metadata(TimeSpan duration) => new(parser.Summary.Metadata, StringComparer.Ordinal)
        {
            ["backend"] = Name,
            ["executable"] = command.ToString(),
            ["model"] = parser.Summary.Model ?? model,
            ["session_id"] = thread.SessionId,
            ["thread_id"] = stage.ThreadId,
            ["thread_context"] = ThreadContinuity.Describe(stage, lease.IsNew, lease.ContextLost),
            ["resumed"] = invocation.Resume,
            ["attempt"] = stage.Attempt,
            ["reasoning_effort"] = effort,
            ["tool_calls"] = parser.Summary.ToolCalls,
            ["tool_errors"] = parser.Summary.ToolErrors,
            [OutputLimit.MetadataKey] = parser.Summary.OutputLimitTruncations,
            ["duration_ms"] = (long)duration.TotalMilliseconds,
        };

        void Annotate(Exception exception, bool cancelled)
        {
            var metadata = Metadata(DateTimeOffset.UtcNow - startedAt);
            if (parser.Summary.ModelCalls > 0)
            {
                metadata["model_calls"] = parser.Summary.ModelCalls;
            }

            // Usage first, against the session the run used; settling may then switch the thread to a fresh session.
            if (parser.Summary.Cumulative.Count > 0)
            {
                AddUsageMetadata(parser.Summary, thread, metadata);
            }
            else
            {
                AddInterruptedUsageMetadata(new InterruptedRun(sessionId, startedAt, invocation, parser.Summary, cancelled), thread, metadata);
            }

            SettleSession(thread, parser.Summary, succeeded: false);
            AddCostMetadata(metadata);
            metadata[BackendMetadata.LastAssistantText] = parser.Summary.LastAssistantText;
            BackendMetadata.Attach(exception, metadata);
        }

        CliProcessResult result;
        try
        {
            using var eventLog = new StageEventLog(stage.StageDirectory, "cli-events.jsonl");
            eventLog.WriteAttempt(stage.Attempt);
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
        catch (OperationCanceledException ex)
        {
            // The process tree is being killed; let the output reader deliver what was already written before accounting.
            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
            Annotate(ex, cancelled: true);
            throw;
        }

        if (stage.StageDirectory is { } dir && !string.IsNullOrWhiteSpace(result.Stderr))
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "stderr.log"), result.Stderr, CancellationToken.None).ConfigureAwait(false);
        }

        var summary = parser.Summary;
        if (result.ExitCode != 0 || summary.IsError || !summary.Completed)
        {
            var detail = summary.ErrorMessage ?? LastLines(result.Stderr) ?? (summary.Completed ? null : "no result was reported");
            var message = $"{Name} failed (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)})" + (detail is null ? "" : ": " + detail);
            Log(message, "error");
            var failure = new CliAgentException(message, CliAgentException.IsRetryableMessage(detail));
            Annotate(failure, cancelled: false);
            throw failure;
        }

        SettleSession(thread, summary, succeeded: true);
        var metadata = Metadata(result.Duration);
        if (summary.ModelCalls > 0)
        {
            metadata["model_calls"] = summary.ModelCalls;
        }

        AddUsageMetadata(summary, thread, metadata);
        AddCostMetadata(metadata);

        var text = summary.Text.Length > 0 ? summary.Text : "(The agent finished without a text response.)";
        return CodergenResult.FromText(text).WithMetadata(metadata);
    }

    public ValueTask DisposeAsync() => _threads.DisposeAllAsync();

    /// <summary>
    /// Records which CLI session the thread continues with. A session id the CLI reported is adopted even when the run failed
    /// (the CLI may have created the session, and creating it again would collide). A run that failed before reporting one
    /// leaves the session's existence unknown, so a thread that has not started yet switches to a fresh id (whose usage
    /// counters start at zero).
    /// </summary>
    private static void SettleSession(CliThread thread, CliRunSummary summary, bool succeeded)
    {
        if (summary.SessionId is { Length: > 0 } reported)
        {
            if (!string.Equals(thread.SessionId, reported, StringComparison.Ordinal))
            {
                thread.Cumulative.Clear();
            }

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
            thread.Cumulative.Clear();
            thread.Instructions = null;
        }
    }

    /// <summary>Builds the CLI arguments (excluding <see cref="CliAgentOptions.ExtraArguments"/>).</summary>
    protected internal abstract IReadOnlyList<string> BuildArguments(CliInvocation invocation);

    protected internal abstract ICliOutputParser CreateParser();

    /// <summary>
    /// True when the CLI takes <paramref name="instructions"/> as system-level instructions on its command line
    /// (<see cref="CliInvocation.Instructions"/>); false puts them in a delimited section at the top of the prompt.
    /// </summary>
    protected internal virtual bool TakesInstructionsAsArgument(string instructions) => false;

    /// <summary>Maps a pipeline model id to the CLI's naming (null = use the CLI default).</summary>
    protected internal virtual string? NormalizeModel(string? model, Action<string, string> log) => model;

    /// <summary>Maps the node's reasoning effort to the CLI's levels (null = omit).</summary>
    protected internal virtual string? MapReasoningEffort(string? effort) => effort;

    /// <summary>
    /// Converts the usage the CLI reported cumulatively for its session (<see cref="CliRunSummary.Cumulative"/>) into
    /// this stage's share: each counter minus the thread's previous value, which then advances.
    /// </summary>
    protected virtual void AddUsageMetadata(CliRunSummary summary, CliThread thread, Dictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (var (field, cumulative) in summary.Cumulative)
        {
            var previous = thread.Cumulative.GetValueOrDefault(field);
            metadata[field] = Math.Max(0, cumulative - previous);
            thread.Cumulative[field] = cumulative;
        }
    }

    /// <summary>
    /// Adds what is known about the usage of a run that ended without reporting it (killed, timed out, or crashed):
    /// by default nothing beyond what was parsed.
    /// </summary>
    protected virtual void AddInterruptedUsageMetadata(InterruptedRun run, CliThread thread, Dictionary<string, object?> metadata)
    {
    }

    /// <summary>Adds <c>cost_usd</c>, <c>cost_basis</c>, and <c>cost_estimated</c> (see <see cref="StageCost"/>).</summary>
    protected virtual void AddCostMetadata(Dictionary<string, object?> metadata)
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

    /// <summary>A run that ended before the CLI reported its usage.</summary>
    /// <param name="SessionId">The session the run used.</param>
    /// <param name="StartedAt">When the run started (UTC).</param>
    /// <param name="Invocation">How it was invoked.</param>
    /// <param name="Summary">What was parsed from its output.</param>
    /// <param name="Cancelled">True when it was cancelled or timed out (the process was killed).</param>
    protected internal sealed record InterruptedRun(string SessionId, DateTimeOffset StartedAt, CliInvocation Invocation, CliRunSummary Summary, bool Cancelled);

    /// <summary>Per-thread CLI session state.</summary>
    protected internal sealed class CliThread
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>True after the first successful run (subsequent runs resume).</summary>
        public bool Started { get; set; }

        /// <summary>The session's usage counters attributed to stages so far (see <see cref="CliRunSummary.Cumulative"/>).</summary>
        public Dictionary<string, double> Cumulative { get; } = new(StringComparer.Ordinal);

        /// <summary>The standing instructions the session last received in a prompt, if any.</summary>
        public string? Instructions { get; set; }
    }
}

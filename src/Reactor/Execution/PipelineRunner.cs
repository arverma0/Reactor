using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Conditions;
using Reactor.Events;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Transforms;
using Reactor.Validation;

namespace Reactor.Execution;

/// <summary>
/// The pipeline execution engine (spec section 3): PARSE → TRANSFORM → VALIDATE → INITIALIZE → EXECUTE → FINALIZE.
/// Traversal is single-threaded; parallelism happens inside the parallel handler, whose branches run on isolated
/// context clones through <see cref="IBranchExecutor"/>.
/// </summary>
public sealed partial class PipelineRunner
{
    private readonly List<IGraphTransform> _transforms = [];
    private readonly List<ILintRule> _lintRules = [];

    public PipelineRunner(PipelineRunnerOptions? options = null)
    {
        Options = options ?? new PipelineRunnerOptions();
        Handlers = CreateDefaultRegistry();
    }

    public PipelineRunnerOptions Options { get; }

    public HandlerRegistry Handlers { get; }

    /// <summary>Custom transforms (run after the built-in transforms, in registration order).</summary>
    public IReadOnlyList<IGraphTransform> Transforms => _transforms;

    /// <summary>Custom lint rules (run after the built-in rules).</summary>
    public IReadOnlyList<ILintRule> LintRules => _lintRules;

    /// <summary>Observer invoked for every event of every run.</summary>
    public event Action<PipelineEvent>? OnEvent;

    /// <summary>The engine version recorded in run manifests.</summary>
    public static string Version { get; } =
        typeof(PipelineRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public void RegisterTransform(IGraphTransform transform) => _transforms.Add(transform ?? throw new ArgumentNullException(nameof(transform)));

    public void RegisterLintRule(ILintRule rule) => _lintRules.Add(rule ?? throw new ArgumentNullException(nameof(rule)));

    public void RegisterHandler(string type, INodeHandler handler) => Handlers.Register(type, handler);

    /// <summary>The built-in handler set; codergen is the default handler.</summary>
    public static HandlerRegistry CreateDefaultRegistry()
    {
        var registry = new HandlerRegistry();
        var codergen = new CodergenHandler();
        registry.Register(HandlerTypes.Start, new StartHandler());
        registry.Register(HandlerTypes.Exit, new ExitHandler());
        registry.Register(HandlerTypes.Codergen, codergen);
        registry.Register(HandlerTypes.WaitHuman, new WaitForHumanHandler());
        registry.Register(HandlerTypes.Conditional, new ConditionalHandler());
        registry.Register(HandlerTypes.Parallel, new ParallelHandler());
        registry.Register(HandlerTypes.FanIn, new FanInHandler());
        registry.Register(HandlerTypes.Tool, new ToolHandler());
        registry.Register(HandlerTypes.ManagerLoop, new ManagerLoopHandler());
        registry.DefaultHandler = codergen;
        return registry;
    }

    /// <summary>Parses, transforms, and validates DOT source.</summary>
    /// <exception cref="DotParseException">The source is not valid DOT.</exception>
    public PreparedPipeline Prepare(string dotSource)
    {
        ArgumentNullException.ThrowIfNull(dotSource);
        return Prepare(DotParser.Parse(dotSource));
    }

    /// <summary>Transforms and validates a parsed graph (the input is not modified).</summary>
    public PreparedPipeline Prepare(Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var transformed = GraphTransforms.ApplyAll(graph, _transforms);
        var diagnostics = PipelineValidator.Validate(transformed, _lintRules, Handlers.Types);
        return new PreparedPipeline(transformed, diagnostics);
    }

    /// <summary>Parses, validates (throwing on errors), and executes DOT source.</summary>
    /// <exception cref="ValidationException">The pipeline has error-severity diagnostics.</exception>
    public Task<PipelineResult> RunAsync(string dotSource, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dotSource);
        return RunAsync(DotParser.Parse(dotSource), options, cancellationToken);
    }

    /// <summary>Transforms, validates (throwing on errors), and executes a graph.</summary>
    /// <exception cref="ValidationException">The pipeline has error-severity diagnostics.</exception>
    public async Task<PipelineResult> RunAsync(Graph graph, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(graph);
        if (prepared.HasErrors)
        {
            throw new ValidationException(prepared.Diagnostics);
        }

        var execution = new PipelineExecution(this, prepared.Graph, options ?? new RunOptions(), prepared.Diagnostics);
        try
        {
            return await execution.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await execution.ReleaseWorkspaceAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resumes a run from <c>{logsRoot}/checkpoint.json</c>: restores context, completed nodes, outcomes, and retry counters,
    /// then continues after the last completed node. A run that ended in failure re-executes its last node; a completed
    /// run returns immediately.
    /// </summary>
    public async Task<PipelineResult> ResumeAsync(string logsRoot, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        var checkpoint = Checkpoint.TryLoadFromRunDirectory(logsRoot)
                         ?? throw new FileNotFoundException($"No checkpoint found in '{logsRoot}'.", Path.Combine(logsRoot, Checkpoint.FileName));
        var graphPath = Path.Combine(logsRoot, PipelineExecution.GraphFileName);
        if (!File.Exists(graphPath))
        {
            throw new FileNotFoundException($"The run directory '{logsRoot}' does not contain {PipelineExecution.GraphFileName}.", graphPath);
        }

        var prepared = Prepare(await File.ReadAllTextAsync(graphPath, cancellationToken).ConfigureAwait(false));
        if (prepared.HasErrors)
        {
            throw new ValidationException(prepared.Diagnostics);
        }

        var runOptions = (options ?? new RunOptions()) with { LogsRoot = logsRoot, RunId = checkpoint.RunId.Length > 0 ? checkpoint.RunId : options?.RunId };
        var execution = new PipelineExecution(this, prepared.Graph, runOptions, prepared.Diagnostics);
        try
        {
            return await execution.ResumeAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await execution.ReleaseWorkspaceAsync().ConfigureAwait(false);
        }
    }

    internal void RaiseEvent(PipelineEvent pipelineEvent) => OnEvent?.Invoke(pipelineEvent);

    /// <summary>One execution of a pipeline (including loop restarts). Also serves as the branch executor.</summary>
    private sealed partial class PipelineExecution : IBranchExecutor, IPipelineEventSink, IChildPipelineLauncher
    {
        internal const string GraphFileName = "graph.dot";
        private const string ResultFileName = "result.json";

        private readonly PipelineRunner _runner;
        private readonly Graph _graph;
        private readonly RunOptions _options;
        private readonly IReadOnlyList<Diagnostic> _diagnostics;
        private readonly object _stateLock = new();
        private readonly Stopwatch _stopwatch = new();
        private readonly string _runId;
        private readonly string _baseLogsRoot;

        private string _logsRoot;
        private RunServices _services = null!;
        private PipelineContext _context = new();
        private JsonlEventSink? _eventLog;
        private List<string> _completed = [];
        private Dictionary<string, Outcome> _outcomes = new(StringComparer.Ordinal);
        private Dictionary<string, int> _retries = new(StringComparer.Ordinal);
        private Dictionary<string, int> _visits = new(StringComparer.Ordinal);
        private List<StageRecord> _history = [];
        private int _stageIndex;
        private int _loopRestarts;
        private string? _lastFidelity;
        private string? _lastThreadId;
        private string? _previousNodeId;
        private bool _degradeNextFidelity;
        private readonly UsageLedger _usage = new();
        private bool _unpricedWarned;

        public PipelineExecution(PipelineRunner runner, Graph graph, RunOptions options, IReadOnlyList<Diagnostic> diagnostics)
        {
            _runner = runner;
            _graph = graph;
            _options = options;
            _diagnostics = diagnostics;
            _runId = options.RunId ?? Guid.NewGuid().ToString("N")[..12];
            _baseLogsRoot = Path.GetFullPath(options.LogsRoot ?? Path.Combine(
                runner.Options.LogsBaseDirectory,
                $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}-{SafeName(graph.Name)}-{_runId}"));
            _logsRoot = _baseLogsRoot;
        }

        private ICodergenBackend? Backend => _options.Backend ?? _runner.Options.Backend;

        private bool Persist => _runner.Options.PersistRunState;

        /// <summary>The run's budget in US dollars: <see cref="RunOptions.MaxCostUsd"/>, else the graph's <c>max_cost</c>; 0 = none.</summary>
        private double MaxCostUsd => _options.MaxCostUsd ?? (double.TryParse(_graph[AttrKeys.MaxCost]?.Trim().TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture, out var max) ? max : 0);

        public async Task<PipelineResult> RunAsync(CancellationToken cancellationToken)
        {
            InitializeRunDirectory(fresh: true);
            InitializeContext();
            _stopwatch.Start();
            Emit(new PipelineStarted(_graph.Name, _graph.Goal, _logsRoot));

            var start = _options.StartAt is { } startAt ? _graph.GetNode(startAt) : _graph.FindStartNode();
            if (start is null)
            {
                return Finish(Outcome.Fail(_options.StartAt is null ? "Pipeline has no start node." : $"Start node '{_options.StartAt}' does not exist."));
            }

            return await LoopAsync(start.Id, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<PipelineResult> ResumeAsync(Checkpoint checkpoint, CancellationToken cancellationToken)
        {
            InitializeRunDirectory(fresh: false);
            _context = new PipelineContext();
            _context.Restore(checkpoint.ContextValues, checkpoint.Logs);
            _completed = checkpoint.CompletedNodes.ToList();
            _outcomes = new Dictionary<string, Outcome>(checkpoint.NodeOutcomes, StringComparer.Ordinal);
            _retries = new Dictionary<string, int>(checkpoint.NodeRetries, StringComparer.Ordinal);
            _history = checkpoint.History.ToList();
            _usage.Restore(checkpoint.Usage);
            _loopRestarts = checkpoint.LoopRestarts;
            _lastFidelity = checkpoint.LastFidelity;
            _lastThreadId = checkpoint.LastThreadId;
            _previousNodeId = checkpoint.CurrentNode.Length > 0 ? checkpoint.CurrentNode : null;
            foreach (var id in _completed)
            {
                _visits[id] = _visits.GetValueOrDefault(id) + 1;
            }

            _stageIndex = _completed.Count;
            AttachServices();
            _stopwatch.Start();
            Emit(new PipelineStarted(_graph.Name, _graph.Goal, _logsRoot));
            _context.AppendLog($"Resumed from checkpoint at '{checkpoint.CurrentNode}'.");

            // In-memory LLM sessions cannot be restored: degrade full fidelity for one hop (spec 5.3).
            _degradeNextFidelity = checkpoint.LastFidelity == FidelityModes.Full;

            if (checkpoint.FinalStatus == "success")
            {
                return Finish(Outcome.Success("Pipeline already completed."), saveCheckpoint: false);
            }

            var last = checkpoint.CurrentNode.Length > 0 ? _graph.GetNode(checkpoint.CurrentNode) : null;
            if (last is null)
            {
                var start = _graph.FindStartNode();
                return start is null
                    ? Finish(Outcome.Fail("Pipeline has no start node."))
                    : await LoopAsync(start.Id, null, cancellationToken).ConfigureAwait(false);
            }

            if (checkpoint.FinalStatus == "fail")
            {
                return await LoopAsync(last.Id, null, cancellationToken).ConfigureAwait(false);
            }

            if (Graph.IsExitNode(last))
            {
                return Finish(Outcome.Success("Pipeline completed."));
            }

            var outcome = _outcomes.GetValueOrDefault(last.Id) ?? Outcome.Success();
            var transition = DetermineNext(last, outcome, _context);
            return await ContinueAfterAsync(last, outcome, transition, cancellationToken).ConfigureAwait(false);
        }

        private async Task<PipelineResult> ContinueAfterAsync(Node node, Outcome outcome, Transition transition, CancellationToken cancellationToken)
        {
            switch (transition.Kind)
            {
                case TransitionKind.Edge when transition.Edge!.LoopRestart:
                    return await LoopAsync(transition.Edge.To, transition.Edge, cancellationToken, restart: true).ConfigureAwait(false);
                case TransitionKind.Edge:
                    return await LoopAsync(transition.Edge!.To, transition.Edge, cancellationToken).ConfigureAwait(false);
                case TransitionKind.Jump:
                case TransitionKind.RetryTarget:
                    return await LoopAsync(transition.Target!, null, cancellationToken).ConfigureAwait(false);
                case TransitionKind.Complete:
                    return await CompleteAtDeadEndAsync(node, cancellationToken).ConfigureAwait(false);
                default:
                    return Finish(outcome.Status == StageStatus.Fail ? outcome : Outcome.Fail(transition.Reason));
            }
        }

        private async Task<PipelineResult> CompleteAtDeadEndAsync(Node node, CancellationToken cancellationToken)
        {
            var gate = CheckGoalGates();
            if (gate is null)
            {
                return Finish(Outcome.Success($"Pipeline completed (no outgoing edges from '{node.Id}')."));
            }

            var target = RetryTargetFor(gate.Value.Node);
            Emit(new GoalGateUnsatisfied(gate.Value.Node.Id, gate.Value.Outcome.Status.ToWire(), target));
            return target is null
                ? Finish(Outcome.Fail($"Goal gate '{gate.Value.Node.Id}' unsatisfied ({gate.Value.Outcome.Status.ToWire()}) and no retry target."))
                : await LoopAsync(target, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task<PipelineResult> LoopAsync(string currentId, Edge? incoming, CancellationToken cancellationToken, bool restart = false)
        {
            var steps = 0;
            var redirects = 0;
            while (true)
            {
                if (restart)
                {
                    restart = false;
                    if (_loopRestarts >= _options.MaxLoopRestarts)
                    {
                        return Finish(Outcome.Fail($"Maximum loop restarts ({_options.MaxLoopRestarts}) exceeded."));
                    }

                    RestartRun(currentId);
                    incoming = null;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Cancelled();
                }

                if (_options.MaxSteps > 0 && ++steps > _options.MaxSteps)
                {
                    return Finish(Outcome.Fail($"Maximum step count ({_options.MaxSteps}) exceeded."));
                }

                var node = _graph.GetNode(currentId);
                if (node is null)
                {
                    return Finish(Outcome.Fail($"Node '{currentId}' does not exist."));
                }

                if (!Graph.IsExitNode(node))
                {
                    if (CheckVisitLimit(node, _context, emitEdge: true) is { } limit)
                    {
                        if (limit.Target is null)
                        {
                            return Finish(Outcome.Fail(limit.Reason));
                        }

                        // Stages whose on_max_visits targets are all used up would bounce between each other forever.
                        if (++redirects > _graph.Nodes.Count)
                        {
                            return Finish(Outcome.Fail($"Visit limits redirect in a cycle without running a stage (last: '{node.Id}' -> '{limit.Target}')."));
                        }

                        currentId = limit.Target;
                        incoming = null;
                        continue;
                    }

                    if (StopIfOverBudget(node) is { } stopped)
                    {
                        return stopped;
                    }
                }

                if (Graph.IsExitNode(node))
                {
                    var gate = CheckGoalGates();
                    if (gate is { } failed)
                    {
                        var target = RetryTargetFor(failed.Node);
                        Emit(new GoalGateUnsatisfied(failed.Node.Id, failed.Outcome.Status.ToWire(), target));
                        if (target is null)
                        {
                            return Finish(Outcome.Fail($"Goal gate '{failed.Node.Id}' unsatisfied ({failed.Outcome.Status.ToWire()}) and no retry target."));
                        }

                        currentId = target;
                        incoming = null;
                        continue;
                    }

                    Outcome exitOutcome;
                    try
                    {
                        exitOutcome = await ExecuteTopLevelNodeAsync(node, incoming, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return Cancelled();
                    }

                    return exitOutcome.Status == StageStatus.Fail
                        ? Finish(exitOutcome)
                        : Finish(Outcome.Success("Pipeline completed."));
                }

                redirects = 0;
                Outcome outcome;
                try
                {
                    outcome = await ExecuteTopLevelNodeAsync(node, incoming, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Cancelled();
                }

                var transition = DetermineNext(node, outcome, _context);
                switch (transition.Kind)
                {
                    case TransitionKind.Jump:
                    case TransitionKind.RetryTarget:
                        if (transition.Kind == TransitionKind.RetryTarget)
                        {
                            _context.AppendLog($"{node.Id} failed; routing to retry target '{transition.Target}'.");
                        }

                        Emit(new EdgeSelected(node.Id, transition.Target!, transition.Reason, ""));
                        currentId = transition.Target!;
                        incoming = null;
                        break;
                    case TransitionKind.Edge:
                        var edge = transition.Edge!;
                        if (FlowsOnwardSilently(outcome, transition))
                        {
                            Emit(new FailedStageContinued(node.Id, edge.To, outcome.FailureReason));
                        }

                        Emit(new EdgeSelected(node.Id, edge.To, transition.Reason, edge.Label));
                        currentId = edge.To;
                        incoming = edge;
                        restart = edge.LoopRestart;
                        break;
                    case TransitionKind.Complete:
                        return await CompleteAtDeadEndAsync(node, cancellationToken).ConfigureAwait(false);
                    default:
                        return Finish(outcome.Status == StageStatus.Fail
                            ? outcome with { FailureReason = outcome.FailureReason.Length > 0 ? outcome.FailureReason : transition.Reason }
                            : Outcome.Fail(transition.Reason));
                }
            }
        }

        private async Task<Outcome> ExecuteTopLevelNodeAsync(Node node, Edge? incoming, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _stageIndex);
            var fidelity = ResolveFidelity(node, incoming);
            var threadId = fidelity == FidelityModes.Full
                ? FidelityResolver.ResolveThread(incoming, node, _graph, _previousNodeId, _lastThreadId)
                : null;

            var (outcome, attempts) = await RunTrackedNodeAsync(node, _context, fidelity, threadId, index, cancellationToken).ConfigureAwait(false);
            Record(node, outcome, attempts, _context, topLevel: true);
            _lastFidelity = fidelity;
            _lastThreadId = threadId;
            _previousNodeId = node.Id;
            SaveCheckpoint(node.Id, finalStatus: null, snapshotIndex: index);
            await RecordCheckpointWorkspaceAsync(node, index, cancellationToken).ConfigureAwait(false);
            return outcome;
        }

        private string ResolveFidelity(Node node, Edge? incoming)
        {
            var fidelity = FidelityResolver.Resolve(incoming, node, _graph);
            if (_degradeNextFidelity)
            {
                _degradeNextFidelity = false;
                if (fidelity == FidelityModes.Full)
                {
                    _context.AppendLog($"Degraded fidelity for '{node.Id}' from full to summary:high after resume.");
                    return FidelityModes.SummaryHigh;
                }
            }

            return fidelity;
        }

        /// <summary>Resolves the handler and executes the node with its retry policy. Shared by top-level and branch traversal.</summary>
        private async Task<(Outcome Outcome, int Attempts)> RunNodeAsync(
            Node node,
            PipelineContext context,
            string fidelity,
            string? threadId,
            int index,
            CancellationToken cancellationToken)
        {
            var handler = _runner.Handlers.TryResolve(node, out var handlerType);
            if (handler is null)
            {
                return (Outcome.Fail($"No handler registered for node '{node.Id}' (type '{handlerType}')."), 1);
            }

            context.Set("current_node", node.Id);
            context.Set("internal.fidelity", fidelity);
            if (threadId is null)
            {
                context.Remove("internal.thread_id");
            }
            else
            {
                context.Set("internal.thread_id", threadId);
            }

            var stageDir = _services.StageDirectory(node.Id);
            var visit = PrepareStageDirectory(node.Id, stageDir);
            SetVisitCount(node.Id, visit, context);
            var policy = handler is ISingleExecutionHandler
                ? new NodeRetryPolicy { MaxAttempts = 1 }
                : NodeRetryPolicy.For(node, _graph, _options.Backoff);
            var timing = StageTiming.For(node, handler, handlerType, Backend);
            var previousKey = PreviousAttempt.ContextKey(node.Id);
            context.Remove(previousKey);

            for (var attempt = 1; ; attempt++)
            {
                SetRetryCount(node.Id, attempt - 1, context);
                Emit(new StageStarted(node.Id, node.Label, index, handlerType, attempt));
                if (Persist)
                {
                    DeleteFile(Path.Combine(stageDir, "status.json"));
                    DeleteFile(Path.Combine(stageDir, StageMetrics.FileName));
                }

                var attemptStartUtc = DateTime.UtcNow.AddSeconds(-1);
                var stopwatch = Stopwatch.StartNew();
                Outcome? outcome = null;
                Exception? error = null;
                var timedOut = false;
                using (var nodeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var timersCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var timers = timing.HardTimeout is null
                        ? Task.CompletedTask
                        : RunStageTimersAsync(node, stageDir, timing, () => timedOut = true, nodeCts, timersCts.Token);
                    try
                    {
                        outcome = await handler.ExecuteAsync(node, context, _graph, _logsRoot, nodeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (timedOut)
                    {
                        // The engine stopped the stage at its time limit (whatever the handler threw while being stopped).
                        error = new TimeoutException($"Stage '{node.Id}' timed out after {AttributeValues.FormatDuration(timing.HardTimeout!.Value)}.", ex);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        await timersCts.CancelAsync().ConfigureAwait(false);
                        await timers.ConfigureAwait(false);
                    }
                }

                RecordUsage(node, stageDir, attemptStartUtc, timedOut ? timing.HardTimeout : null);
                var willRetry = attempt < policy.MaxAttempts;
                if (error is not null)
                {
                    var retryable = policy.ShouldRetry(error) && !(timedOut && timing.OnTimeout == StageTiming.OnTimeoutFail);
                    var message = DescribeError(error);
                    Emit(new StageFailed(node.Id, index, message, retryable && willRetry));
                    if (retryable && willRetry)
                    {
                        PrepareRetry(node, context, stageDir, attempt, policy, timedOut ? AttemptEndings.Timeout : AttemptEndings.Error, message, stopwatch.Elapsed);
                        await DelayBeforeRetryAsync(node, index, attempt, policy, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    context.Remove(previousKey);
                    var failed = Outcome.Fail(message);
                    EnsureStatusFile(stageDir, failed);
                    Emit(new StageCompleted(node.Id, index, stopwatch.Elapsed, failed.Status.ToWire(), ""));
                    return (failed, attempt);
                }

                outcome ??= ReadStatusFileOrDefault(node, stageDir);
                outcome = WithFileMetrics(node, outcome, context, stageDir, attemptStartUtc);
                switch (outcome.Status)
                {
                    case StageStatus.Success:
                    case StageStatus.PartialSuccess:
                    case StageStatus.Skipped:
                        if (outcome.Status != StageStatus.Skipped)
                        {
                            SetRetryCount(node.Id, 0, context);
                        }

                        context.Remove(previousKey);
                        EnsureStatusFile(stageDir, outcome);
                        Emit(new StageCompleted(node.Id, index, stopwatch.Elapsed, outcome.Status.ToWire(), outcome.Notes));
                        return (outcome, attempt);

                    case StageStatus.Retry:
                        if (willRetry)
                        {
                            Emit(new StageFailed(node.Id, index, DescribeOutcome(outcome, "requested retry"), WillRetry: true));
                            PrepareRetry(node, context, stageDir, attempt, policy, AttemptEndings.Retry, outcome.FailureReason.Length > 0 ? outcome.FailureReason : outcome.Notes, stopwatch.Elapsed);
                            await DelayBeforeRetryAsync(node, index, attempt, policy, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        context.Remove(previousKey);
                        var exhausted = node.AllowPartial
                            ? Outcome.PartialSuccess("retries exhausted, partial accepted", outcome.ContextUpdates) with { PreferredLabel = outcome.PreferredLabel, SuggestedNextIds = outcome.SuggestedNextIds, Metrics = outcome.Metrics }
                            : Outcome.Fail("max retries exceeded" + (outcome.FailureReason.Length > 0 ? $": {outcome.FailureReason}" : ""), outcome.ContextUpdates) with { Metrics = outcome.Metrics };
                        EnsureStatusFile(stageDir, exhausted, overwrite: true);
                        Emit(new StageCompleted(node.Id, index, stopwatch.Elapsed, exhausted.Status.ToWire(), exhausted.Notes));
                        return (exhausted, attempt);

                    default:
                        // A tool stage enforces its own time limit and reports it as a failure: it is still a timeout.
                        var toolTimedOut = outcome.ContextUpdates.TryGetValue(ToolHandler.TimedOutKey, out var timedOutFlag) && timedOutFlag is true;
                        if (willRetry && !(toolTimedOut && timing.OnTimeout == StageTiming.OnTimeoutFail))
                        {
                            Emit(new StageFailed(node.Id, index, DescribeOutcome(outcome, "stage failed"), WillRetry: true));
                            PrepareRetry(node, context, stageDir, attempt, policy, toolTimedOut ? AttemptEndings.Timeout : AttemptEndings.Fail, outcome.FailureReason, stopwatch.Elapsed);
                            await DelayBeforeRetryAsync(node, index, attempt, policy, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        context.Remove(previousKey);
                        EnsureStatusFile(stageDir, outcome);
                        Emit(new StageFailed(node.Id, index, DescribeOutcome(outcome, "stage failed"), WillRetry: false));
                        Emit(new StageCompleted(node.Id, index, stopwatch.Elapsed, outcome.Status.ToWire(), outcome.Notes));
                        return (outcome, attempt);
                }
            }
        }

        /// <summary>
        /// Archives the attempt's stage files (so the next attempt starts fresh and the record of each attempt is kept) and
        /// records how it ended for the next attempt's hand-off (see <see cref="PreviousAttempt"/>).
        /// </summary>
        private void PrepareRetry(Node node, PipelineContext context, string stageDir, int attempt, NodeRetryPolicy policy, string ending, string reason, TimeSpan elapsed)
        {
            var archive = Persist ? StageAttemptArchive.Archive(stageDir, attempt) : null;
            var previous = new PreviousAttempt(attempt, policy.MaxAttempts, ending, reason, (long)elapsed.TotalMilliseconds, archive);
            context.Set(PreviousAttempt.ContextKey(node.Id), previous.ToContextValue());
        }

        /// <summary>
        /// The stage's time limits: the soft-deadline reminder, the hard timeout, and (for <c>on_timeout=wrap_up</c>) a final
        /// "time is up" message followed by a grace period. Sets <paramref name="markTimedOut"/> before cancelling the stage.
        /// </summary>
        private async Task RunStageTimersAsync(Node node, string stageDir, StageTiming timing, Action markTimedOut, CancellationTokenSource stage, CancellationToken cancellationToken)
        {
            try
            {
                var started = Stopwatch.StartNew();
                var hard = timing.HardTimeout!.Value;
                if (timing.SoftDeadline is { } soft && soft < hard)
                {
                    await Task.Delay(soft, cancellationToken).ConfigureAwait(false);
                    var left = hard - started.Elapsed;
                    await SteerAsync(node, stageDir, StageTiming.SoftDeadlineMessage(left, hard), $"soft deadline: about {AttributeValues.FormatDuration(RoundMinutes(left))} left; reminded the agent to wrap up").ConfigureAwait(false);
                }

                var remaining = hard - started.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }

                if (timing.WrapUpGrace is { } grace)
                {
                    await SteerAsync(node, stageDir, StageTiming.TimeIsUpMessage(grace), $"time limit reached; told the agent to stop and summarize within {AttributeValues.FormatDuration(grace)}").ConfigureAwait(false);
                    await Task.Delay(grace, cancellationToken).ConfigureAwait(false);
                }

                markTimedOut();
                await stage.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The stage finished (or the run was cancelled) first.
            }
        }

        private async Task SteerAsync(Node node, string stageDir, string message, string logLine)
        {
            try
            {
                await SteeringMessages.AppendAsync(stageDir, message).ConfigureAwait(false);
                _services.Log(node.Id, logLine, "info");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _services.Log(node.Id, $"could not deliver a time reminder: {ex.Message}", "warn");
            }
        }

        private static TimeSpan RoundMinutes(TimeSpan span) => span >= TimeSpan.FromMinutes(2)
            ? TimeSpan.FromMinutes(Math.Round(span.TotalMinutes))
            : TimeSpan.FromSeconds(Math.Max(0, Math.Round(span.TotalSeconds)));

        /// <summary>
        /// Adds the attempt's <c>backend.json</c> (if it wrote one) to the run's usage totals; for an attempt stopped at its
        /// time limit, marks the record's <c>outcome</c> as <c>timeout</c>. Warns once when a budget is set but a stage
        /// reports no <c>cost_usd</c>.
        /// </summary>
        private JsonObject? RecordUsage(Node node, string stageDir, DateTime attemptStartUtc, TimeSpan? timedOutAfter)
        {
            var path = Path.Combine(stageDir, "backend.json");
            var metadata = _usage.TryAddFile(path, attemptStartUtc);
            if (metadata is null)
            {
                return null;
            }

            if (timedOutAfter is { } limit && Persist)
            {
                try
                {
                    metadata["outcome"] = "timeout";
                    metadata["timeout_ms"] = (long)limit.TotalMilliseconds;
                    File.WriteAllText(path, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (MaxCostUsd > 0 && metadata["cost_usd"] is null && !_unpricedWarned)
            {
                _unpricedWarned = true;
                _services.Log(node.Id, "max_cost is set, but this stage's backend reported no cost_usd; stages without a price are not counted against the budget", "warn");
            }

            return metadata;
        }

        /// <summary>Merges metrics from <c>{stage}/metrics.json</c> and the node's <c>metrics_file</c> (status.json metrics win).</summary>
        private Outcome WithFileMetrics(Node node, Outcome outcome, PipelineContext context, string stageDir, DateTime attemptStartUtc)
        {
            if (outcome.Status == StageStatus.Skipped)
            {
                return outcome;
            }

            IReadOnlyDictionary<string, double> fromFiles = new Dictionary<string, double>(StringComparer.Ordinal);
            var stageFile = Path.Combine(stageDir, StageMetrics.FileName);
            if (File.Exists(stageFile) && File.GetLastWriteTimeUtc(stageFile) >= attemptStartUtc)
            {
                fromFiles = ReadMetricsFile(node, stageFile, fromFiles);
            }

            if (node[AttrKeys.MetricsFile] is { Length: > 0 } relative && !string.IsNullOrWhiteSpace(relative))
            {
                var path = Path.GetFullPath(Path.Combine(context.WorkingDirectory, relative.Trim()));
                if (File.Exists(path))
                {
                    fromFiles = ReadMetricsFile(node, path, fromFiles);
                }
                else if ((_services.Backend is not null || Persist) && outcome.Status is not (StageStatus.Fail or StageStatus.Retry))
                {
                    // A failed attempt may well have stopped before writing its report; its failure says more.
                    _services.Log(node.Id, $"metrics_file '{relative}' does not exist; no metrics recorded from it", "warn");
                }
            }

            return fromFiles.Count == 0 ? outcome : outcome with { Metrics = StageMetrics.Merge(fromFiles, outcome.Metrics) };
        }

        private IReadOnlyDictionary<string, double> ReadMetricsFile(Node node, string path, IReadOnlyDictionary<string, double> merged)
        {
            try
            {
                return StageMetrics.Merge(merged, StageMetrics.ReadFile(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _services.Log(node.Id, $"could not read metrics from '{path}': {ex.Message}", "warn");
                return merged;
            }
        }

        private async Task DelayBeforeRetryAsync(Node node, int index, int attempt, NodeRetryPolicy policy, CancellationToken cancellationToken)
        {
            var delay = policy.Backoff.DelayForAttempt(attempt);
            Emit(new StageRetrying(node.Id, index, attempt + 1, delay));
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        private Outcome ReadStatusFileOrDefault(Node node, string stageDir)
        {
            var path = Path.Combine(stageDir, "status.json");
            if (File.Exists(path))
            {
                try
                {
                    return Outcome.FromStatusJson(JsonNode.Parse(File.ReadAllText(path)));
                }
                catch (Exception ex) when (ex is JsonException or FormatException or IOException)
                {
                    return Outcome.Fail($"Invalid status.json written for stage '{node.Id}': {ex.Message}");
                }
                finally
                {
                    // In-memory runs skip the per-attempt cleanup, so consume the file: a later attempt must not read it again.
                    if (!Persist)
                    {
                        TryDeleteFile(path);
                    }
                }
            }

            return node.AutoStatus
                ? Outcome.Success("auto-status: handler completed without writing status")
                : Outcome.Fail($"Handler for '{node.Id}' completed without producing an outcome or status.json.");
        }

        private void Record(Node node, Outcome outcome, int attempts, PipelineContext context, bool topLevel)
        {
            lock (_stateLock)
            {
                _completed.Add(node.Id);
                if (outcome.Status != StageStatus.Skipped)
                {
                    _outcomes[node.Id] = outcome;
                }

                var record = new StageRecord(node.Id, node.Label, outcome.Status, outcome.Notes, outcome.FailureReason, DateTimeOffset.UtcNow, attempts, outcome.Metrics.Count > 0 ? outcome.Metrics : null);
                _history.Add(record);
                if (topLevel)
                {
                    _services.HistoryList.Add(record);
                }
            }

            context.ApplyUpdates(outcome.ContextUpdates);
            context.Set("outcome", outcome.Status.ToWire());

            // Reactor extension: outcome metrics become metrics.<name> context values (routable with numeric conditions).
            if (outcome.Metrics.Count > 0)
            {
                foreach (var (name, value) in outcome.Metrics)
                {
                    context.Set(Outcome.MetricsContextPrefix + name, value);
                }

                Emit(new MetricsRecorded(node.Id, outcome.Metrics));
            }

            // Artifact records are announced only once they are part of the context (never for retried attempts).
            foreach (var artifact in outcome.Artifacts)
            {
                Emit(new ArtifactProduced(node.Id, artifact.Name, artifact.Kind, artifact.Path, artifact.Bytes, artifact.Version, artifact.Sha256));
            }

            // Always refresh (clearing stale values) so pass-through routing nodes see the latest stage's routing data.
            context.Set("preferred_label", outcome.PreferredLabel);
            context.Set("failure_reason", outcome.FailureReason);

            var detail = outcome.Status == StageStatus.Fail ? outcome.FailureReason : outcome.Notes;
            context.AppendLog(detail.Length > 0 ? $"{node.Id}: {outcome.Status.ToWire()} — {detail}" : $"{node.Id}: {outcome.Status.ToWire()}");
        }

        private Transition DetermineNext(Node node, Outcome outcome, PipelineContext context)
        {
            if (outcome.JumpTo is { Length: > 0 } jump)
            {
                return _graph.ContainsNode(jump)
                    ? Transition.JumpTo(jump, "jump")
                    : Transition.Fail($"Stage '{node.Id}' requested a jump to unknown node '{jump}'.");
            }

            if (outcome.Status == StageStatus.Fail)
            {
                // Spec 3.2 + 3.7: normal edge selection first (a fail edge, or any edge that routes the failure onward —
                // e.g. to a diamond that inspects the outcome); then retry_target / fallback_retry_target; else terminate.
                // Nodes whose edges are alternatives rather than a "next step" only follow explicit routing
                // (conditions/labels/suggestions) on failure: a fan-in's edges lead onward as if it had succeeded,
                // a parallel node's edges are its branches, and a human gate's edges are the human's choices.
                // Reactor extension: with on_fail=stop (or default_on_fail=stop) an unconditional edge is not followed after a
                // failure either — except into a diamond, which inspects the outcome, or a fan-in, which receives the failed
                // branch as its result.
                var resolvedHandler = _runner.Handlers.TryResolve(node, out var nodeType);
                var explicitOnly = resolvedHandler is not null && nodeType is HandlerTypes.FanIn or HandlerTypes.Parallel or HandlerTypes.WaitHuman;
                var failRoute = EdgeSelector.Select(node, outcome, context, _graph, explicitRoutingOnly: explicitOnly);
                if (failRoute is { Reason: EdgeSelector.ByWeight } && StopsOnFailure(node, _graph) && !ReceivesFailures(failRoute.Edge.To))
                {
                    failRoute = null;
                }

                if (failRoute is not null)
                {
                    return Transition.Follow(failRoute.Edge, failRoute.Reason);
                }

                foreach (var target in new[] { node.RetryTarget, node.FallbackRetryTarget })
                {
                    if (target.Length > 0 && _graph.ContainsNode(target))
                    {
                        return Transition.Retry(target);
                    }
                }

                return Transition.Fail(outcome.FailureReason.Length > 0 ? outcome.FailureReason : $"Stage '{node.Id}' failed.");
            }

            var selection = EdgeSelector.Select(node, outcome, context, _graph);
            if (selection is not null)
            {
                return Transition.Follow(selection.Edge, selection.Reason);
            }

            return _graph.OutgoingEdges(node.Id).Count == 0
                ? Transition.CompleteRun()
                : Transition.Fail($"No eligible outgoing edge from '{node.Id}' for outcome '{outcome.Status.ToWire()}' (no condition matched and no unconditional edge).");
        }

        /// <summary>
        /// True when a failed stage's failure is carried on implicitly: edge selection fell through to an unconditional edge
        /// by weight (no condition, label, suggestion, or retry target handled it), and the target neither inspects the failure
        /// (a conditional/diamond node re-emits it for its own edges) nor receives it as a result (a fan-in node ends the
        /// branch, whose result records the failure).
        /// </summary>
        private bool FlowsOnwardSilently(Outcome outcome, Transition transition) =>
            outcome.Status == StageStatus.Fail &&
            transition is { Kind: TransitionKind.Edge, Reason: EdgeSelector.ByWeight, Edge: { } edge } &&
            !ReceivesFailures(edge.To);

        /// <summary>True for diamonds (which route on the failed outcome they re-emit) and fan-ins (which take the failed branch as a result).</summary>
        private bool ReceivesFailures(string nodeId) =>
            _graph.GetNode(nodeId) is { } target && _runner.Handlers.TryResolve(target, out var type) is not null &&
            type is HandlerTypes.Conditional or HandlerTypes.FanIn;

        /// <summary>
        /// Reactor extension: true when a failure of <paramref name="node"/> must be routed explicitly — its <c>on_fail</c> (or
        /// the graph's <c>default_on_fail</c>) is <c>stop</c> — so unconditional edges are not followed after a failure.
        /// </summary>
        internal static bool StopsOnFailure(Node node, Graph graph) =>
            string.Equals((node[AttrKeys.OnFail] ?? graph[AttrKeys.DefaultOnFail] ?? "").Trim(), "stop", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reactor extension (<c>max_visits</c>): when <paramref name="node"/> has already run its maximum number of times, emits
        /// <see cref="VisitLimitReached"/> and returns where to go instead (<c>on_max_visits</c>; null target = fail with the
        /// reason). Null when the node may run.
        /// </summary>
        private (string? Target, string Reason)? CheckVisitLimit(Node node, PipelineContext context, bool emitEdge)
        {
            if (node.MaxVisits is not { } max)
            {
                return null;
            }

            int visits;
            lock (_stateLock)
            {
                visits = _visits.GetValueOrDefault(node.Id);
            }

            if (visits < max)
            {
                return null;
            }

            var target = node.OnMaxVisits.Length > 0 && node.OnMaxVisits != node.Id && _graph.ContainsNode(node.OnMaxVisits) ? node.OnMaxVisits : null;
            Emit(new VisitLimitReached(node.Id, max, target));
            if (target is null)
            {
                return (null, $"Stage '{node.Id}' reached its visit limit (max_visits={max.ToString(CultureInfo.InvariantCulture)}) and has no on_max_visits target.");
            }

            context.AppendLog($"{node.Id} reached its visit limit ({max.ToString(CultureInfo.InvariantCulture)}); continuing at '{target}'.");
            if (emitEdge)
            {
                Emit(new EdgeSelected(node.Id, target, "max_visits", ""));
            }

            return (target, "");
        }

        /// <summary>
        /// Reactor extension (<c>max_cost</c>): stops the run before <paramref name="next"/> once its recorded cost reached the
        /// budget. The last checkpoint is kept unfinished, so resuming (with a higher budget) continues at <paramref name="next"/>.
        /// </summary>
        private PipelineResult? StopIfOverBudget(Node next)
        {
            var limit = MaxCostUsd;
            var spent = _usage.CostUsd;
            if (limit <= 0 || spent < limit)
            {
                return null;
            }

            Emit(new BudgetExceeded(spent, limit, next.Id));
            var reason = $"Budget exceeded: {UsageLedger.FormatUsd(spent)} spent, max_cost is {UsageLedger.FormatUsd(limit)}. Resume with a higher budget to continue at '{next.Id}'.";
            _context.AppendLog(reason);
            return Finish(Outcome.Fail(reason), saveCheckpoint: false, budgetExceeded: true);
        }

        private (Node Node, Outcome Outcome)? CheckGoalGates()
        {
            lock (_stateLock)
            {
                foreach (var (nodeId, outcome) in _outcomes)
                {
                    if (_graph.GetNode(nodeId) is { GoalGate: true } node && !outcome.IsSuccessful)
                    {
                        return (node, outcome);
                    }
                }
            }

            return null;
        }

        private string? RetryTargetFor(Node node)
        {
            foreach (var target in new[] { node.RetryTarget, node.FallbackRetryTarget, _graph.RetryTarget, _graph.FallbackRetryTarget })
            {
                if (target.Length > 0 && _graph.ContainsNode(target))
                {
                    return target;
                }
            }

            return null;
        }

        public ChildPipelineHandle Start(string dotFilePath, string logsRoot, string workingDirectory, CancellationToken cancellationToken)
        {
            var handle = new ChildPipelineHandle(logsRoot);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handle.Cancellation = cts;
            var childOptions = new RunOptions
            {
                LogsRoot = logsRoot,
                WorkingDirectory = workingDirectory,
                Interviewer = _services.Interviewer,
                Backend = Backend,
                Backoff = _options.Backoff,
                MaxLoopRestarts = _options.MaxLoopRestarts,
                OnEvent = handle.Observe,
            };
            handle.Completion = Task.Run(async () =>
            {
                try
                {
                    var source = await File.ReadAllTextAsync(dotFilePath, cts.Token).ConfigureAwait(false);
                    var childGraph = DotParser.Parse(source);
                    childGraph.SourcePath = Path.GetFullPath(dotFilePath);
                    return await _runner.RunAsync(childGraph, childOptions, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    cts.Dispose();
                }
            }, CancellationToken.None);
            return handle;
        }

        public async Task<BranchResult> ExecuteBranchAsync(string startNodeId, PipelineContext branchContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(branchContext);
            var stopwatch = Stopwatch.StartNew();
            var completed = new List<string>();
            var current = startNodeId;
            Edge? incoming = null;
            string? previous = null;
            string? previousThread = null;
            var last = Outcome.Success();

            BranchResult Result(StageStatus status, string? stopNode, string notes = "", string failure = "") => new()
            {
                BranchId = startNodeId,
                Status = status,
                StopNodeId = stopNode,
                Notes = notes,
                FailureReason = failure,
                CompletedNodes = completed,
                Context = branchContext.Snapshot(),
                Duration = stopwatch.Elapsed,
            };

            for (var step = 0; step < _runner.Options.BranchMaxSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = _graph.GetNode(current);
                if (node is null)
                {
                    return Result(StageStatus.Fail, null, failure: $"Node '{current}' does not exist.");
                }

                var type = _runner.Handlers.TryResolve(node, out var resolvedType) is null ? HandlerTypes.Codergen : resolvedType;
                if (type == HandlerTypes.FanIn || Graph.IsExitNode(node))
                {
                    return Result(last.Status == StageStatus.Skipped ? StageStatus.Success : last.Status, node.Id, last.Notes, last.FailureReason);
                }

                if (CheckVisitLimit(node, branchContext, emitEdge: false) is { } limit)
                {
                    if (limit.Target is null)
                    {
                        return Result(StageStatus.Fail, null, last.Notes, limit.Reason);
                    }

                    incoming = null;
                    current = limit.Target;
                    continue;
                }

                var fidelity = FidelityResolver.Resolve(incoming, node, _graph);
                var thread = fidelity == FidelityModes.Full ? FidelityResolver.ResolveThread(incoming, node, _graph, previous, previousThread) : null;
                var index = Interlocked.Increment(ref _stageIndex);
                var (outcome, attempts) = await RunTrackedNodeAsync(node, branchContext, fidelity, thread, index, cancellationToken).ConfigureAwait(false);
                Record(node, outcome, attempts, branchContext, topLevel: false);
                completed.Add(node.Id);
                last = outcome;
                previous = node.Id;
                previousThread = thread;

                var transition = DetermineNext(node, outcome, branchContext);
                switch (transition.Kind)
                {
                    case TransitionKind.Edge:
                        if (FlowsOnwardSilently(outcome, transition))
                        {
                            Emit(new FailedStageContinued(node.Id, transition.Edge!.To, outcome.FailureReason));
                        }

                        incoming = transition.Edge;
                        current = transition.Edge!.To;
                        break;
                    case TransitionKind.Jump:
                    case TransitionKind.RetryTarget:
                        incoming = null;
                        current = transition.Target!;
                        break;
                    case TransitionKind.Complete:
                        return Result(outcome.Status, null, outcome.Notes, outcome.FailureReason);
                    default:
                        return Result(StageStatus.Fail, null, outcome.Notes, outcome.FailureReason.Length > 0 ? outcome.FailureReason : transition.Reason);
                }
            }

            return Result(StageStatus.Fail, null, failure: $"Branch '{startNodeId}' exceeded {_runner.Options.BranchMaxSteps} steps.");
        }

        private void InitializeRunDirectory(bool fresh)
        {
            if (Persist)
            {
                Directory.CreateDirectory(_logsRoot);
                var graphPath = Path.Combine(_logsRoot, GraphFileName);
                if (fresh || !File.Exists(graphPath))
                {
                    File.WriteAllText(graphPath, _graph.Source ?? DotWriter.Write(_graph));
                }

                if (fresh)
                {
                    WriteManifest();
                }
            }

            OpenEventLog();
            AttachServices();
        }

        private void WriteManifest()
        {
            var manifest = new JsonObject
            {
                ["name"] = _graph.Name,
                ["label"] = _graph.Label,
                ["goal"] = _graph.Goal,
                ["run_id"] = _runId,
                ["started_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["logs_root"] = _logsRoot,
                ["working_directory"] = WorkingDirectory,
                ["node_count"] = _graph.Nodes.Count,
                ["edge_count"] = _graph.Edges.Count,
                ["loop_restarts"] = _loopRestarts,
                ["reactor_version"] = Version,
            };
            File.WriteAllText(Path.Combine(_logsRoot, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private string WorkingDirectory => Path.GetFullPath(_options.WorkingDirectory ?? Directory.GetCurrentDirectory());

        private void OpenEventLog()
        {
            _eventLog?.Dispose();
            _eventLog = _runner.Options.WriteEventLog ? new JsonlEventSink(Path.Combine(_logsRoot, "events.jsonl")) : null;
        }

        private void AttachServices()
        {
            _services = new RunServices
            {
                RunId = _runId,
                LogsRoot = _logsRoot,
                Graph = _graph,
                Events = this,
                Artifacts = new ArtifactStore(_logsRoot),
                Interviewer = _options.Interviewer ?? _runner.Options.Interviewer,
                Backend = Backend,
                BranchExecutor = this,
                ChildLauncher = this,
                WorkingDirectory = WorkingDirectory,
                Workspace = WorkspaceService,
                Options = _options,
                RunnerOptions = _runner.Options,
                HistoryList = _history.ToList(),
            };
            _context.Run = _services;
            _context.Artifacts = _services.Artifacts;
        }

        private void InitializeContext()
        {
            _context = new PipelineContext();
            MirrorGraphAttributes(_context);
            if (_options.InitialContext is not null)
            {
                _context.ApplyUpdates(_options.InitialContext);
            }

            _context.Set("internal.run_id", _runId);
            AttachServices();
        }

        private void MirrorGraphAttributes(PipelineContext context)
        {
            foreach (var (key, value) in _graph.Attributes)
            {
                if (key != AttrKeys.ModelStylesheet)
                {
                    context.Set("graph." + key, value);
                }
            }

            context.Set("graph.goal", _graph.Goal);
        }

        private void RestartRun(string targetNodeId)
        {
            _loopRestarts++;
            _logsRoot = Path.GetFullPath($"{_baseLogsRoot}-restart-{_loopRestarts.ToString(CultureInfo.InvariantCulture)}");
            lock (_stateLock)
            {
                _completed = [];
                _outcomes = new Dictionary<string, Outcome>(StringComparer.Ordinal);
                _retries = new Dictionary<string, int>(StringComparer.Ordinal);
                _visits = new Dictionary<string, int>(StringComparer.Ordinal);
                _history = [];
            }

            _lastFidelity = null;
            _lastThreadId = null;
            _previousNodeId = null;
            if (Persist)
            {
                Directory.CreateDirectory(_logsRoot);
                File.WriteAllText(Path.Combine(_logsRoot, GraphFileName), _graph.Source ?? DotWriter.Write(_graph));
                WriteManifest();
            }

            OpenEventLog();
            InitializeContext();
            _context.Set("internal.loop_restart_count", _loopRestarts);
            Emit(new LoopRestarted(targetNodeId, _loopRestarts, _logsRoot));
        }

        /// <summary>Counts a visit of <paramref name="nodeId"/> (archiving the previous visit's files) and returns its number.</summary>
        private int PrepareStageDirectory(string nodeId, string stageDir)
        {
            int visit;
            lock (_stateLock)
            {
                visit = _visits.GetValueOrDefault(nodeId) + 1;
                _visits[nodeId] = visit;
            }

            if (!Persist)
            {
                return visit;
            }

            Directory.CreateDirectory(stageDir);
            if (visit <= 1)
            {
                return visit;
            }

            // Re-entering a node (loops, retries via routing): archive the previous visit's files and attempt archives.
            var archive = Path.Combine(stageDir, "history", $"visit-{(visit - 1).ToString(CultureInfo.InvariantCulture)}");
            var files = Directory.GetFiles(stageDir);
            var attempts = Path.Combine(stageDir, StageAttemptArchive.DirectoryName);
            if (files.Length == 0 && !Directory.Exists(attempts))
            {
                return visit;
            }

            Directory.CreateDirectory(archive);
            foreach (var file in files)
            {
                File.Move(file, Path.Combine(archive, Path.GetFileName(file)), overwrite: true);
            }

            if (Directory.Exists(attempts))
            {
                try
                {
                    Directory.Move(attempts, Path.Combine(archive, StageAttemptArchive.DirectoryName));
                }
                catch (IOException)
                {
                    // An archive from an earlier, interrupted visit already exists there; keep both by leaving this one in place.
                }
            }

            return visit;
        }

        /// <summary>
        /// Records the node's visit count as <c>internal.visits.&lt;node&gt;</c> (read by <c>visits.&lt;node&gt;</c> conditions) in
        /// the executing context and, for stages inside parallel branches, in the run's context too.
        /// </summary>
        private void SetVisitCount(string nodeId, int visit, PipelineContext context)
        {
            var key = ConditionEvaluator.VisitsContextPrefix + nodeId;
            context.Set(key, visit);
            if (!ReferenceEquals(context, _context))
            {
                _context.Set(key, visit);
            }
        }

        private void SetRetryCount(string nodeId, int count, PipelineContext context)
        {
            lock (_stateLock)
            {
                _retries[nodeId] = count;
            }

            context.Set($"internal.retry_count.{nodeId}", count);
        }

        private void EnsureStatusFile(string stageDir, Outcome outcome, bool overwrite = false)
        {
            if (!Persist)
            {
                return;
            }

            var path = Path.Combine(stageDir, "status.json");
            if (overwrite || !File.Exists(path))
            {
                Directory.CreateDirectory(stageDir);
                File.WriteAllText(path, outcome.ToStatusJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        /// <summary>
        /// Saves <c>checkpoint.json</c>; with <paramref name="snapshotIndex"/> (top-level stages only) and
        /// <see cref="RunOptions.KeepCheckpointHistory"/>, also keeps a copy under <c>checkpoints/</c>.
        /// </summary>
        private void SaveCheckpoint(string nodeId, string? finalStatus, bool emitEvent = true, int? snapshotIndex = null)
        {
            if (!Persist)
            {
                return;
            }

            Checkpoint checkpoint;
            lock (_stateLock)
            {
                checkpoint = new Checkpoint
                {
                    RunId = _runId,
                    CurrentNode = nodeId,
                    CompletedNodes = _completed.ToList(),
                    NodeRetries = new Dictionary<string, int>(_retries, StringComparer.Ordinal),
                    ContextValues = _context.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                    Logs = _context.Logs.ToList(),
                    NodeOutcomes = new Dictionary<string, Outcome>(_outcomes, StringComparer.Ordinal),
                    LastFidelity = _lastFidelity,
                    LastThreadId = _lastThreadId,
                    LoopRestarts = _loopRestarts,
                    History = _history.ToList(),
                    FinalStatus = finalStatus,
                    Usage = new Dictionary<string, double>(_usage.Snapshot(), StringComparer.Ordinal),
                };
            }

            var path = Path.Combine(_logsRoot, Checkpoint.FileName);
            checkpoint.Save(path);
            if (snapshotIndex is { } index && _options.KeepCheckpointHistory && !_graph.ContainsNode(Checkpoint.SnapshotDirectoryName))
            {
                try
                {
                    checkpoint.Save(Path.Combine(_logsRoot, Checkpoint.SnapshotDirectoryName, Checkpoint.SnapshotName(index, nodeId) + ".json"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Snapshots only enable forking; the run itself does not depend on them.
                    Emit(new StageLog(nodeId, $"Could not save the checkpoint snapshot: {ex.Message}", "warn"));
                }
            }

            if (emitEvent)
            {
                Emit(new CheckpointSaved(nodeId, path));
            }
        }

        private PipelineResult Cancelled()
        {
            var outcome = Outcome.Fail("Pipeline cancelled.");
            return Finish(outcome, cancelled: true, saveCheckpoint: false);
        }

        private PipelineResult Finish(Outcome outcome, bool cancelled = false, bool saveCheckpoint = true, bool budgetExceeded = false)
        {
            _stopwatch.Stop();
            var status = outcome.Status == StageStatus.Fail ? StageStatus.Fail : StageStatus.Success;
            if (saveCheckpoint && _previousNodeId is not null)
            {
                SaveCheckpoint(_previousNodeId, status.ToWire(), emitEvent: false);
            }

            PipelineResult result;
            lock (_stateLock)
            {
                result = new PipelineResult
                {
                    RunId = _runId,
                    LogsRoot = _logsRoot,
                    Status = status,
                    FinalOutcome = outcome,
                    CompletedNodes = _completed.ToList(),
                    NodeOutcomes = new Dictionary<string, Outcome>(_outcomes, StringComparer.Ordinal),
                    Context = _context.Snapshot(),
                    Duration = _stopwatch.Elapsed,
                    LoopRestarts = _loopRestarts,
                    Cancelled = cancelled,
                    Diagnostics = _diagnostics,
                    Usage = _usage.Snapshot(),
                    BudgetExceeded = budgetExceeded,
                };
            }

            WriteResultFile(result);
            if (status == StageStatus.Success)
            {
                Emit(new PipelineCompleted(result.Duration, _services.Artifacts.Count, status.ToWire(), outcome.Notes));
            }
            else
            {
                Emit(new PipelineFailed(outcome.FailureReason, result.Duration));
            }

            _eventLog?.Dispose();
            return result;
        }

        private void WriteResultFile(PipelineResult result)
        {
            if (!Persist)
            {
                return;
            }

            try
            {
                var json = new JsonObject
                {
                    ["run_id"] = result.RunId,
                    ["status"] = result.Status.ToWire(),
                    ["failure_reason"] = result.FailureReason,
                    ["notes"] = result.FinalOutcome.Notes,
                    ["cancelled"] = result.Cancelled,
                    ["duration_ms"] = (long)result.Duration.TotalMilliseconds,
                    ["completed_nodes"] = new JsonArray(result.CompletedNodes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                    ["loop_restarts"] = result.LoopRestarts,
                    ["finished_at"] = DateTimeOffset.UtcNow.ToString("O"),
                };
                File.WriteAllText(Path.Combine(_logsRoot, ResultFileName), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException)
            {
                // The result file is informational.
            }
        }

        public void Emit(PipelineEvent pipelineEvent)
        {
            if (_workspace?.Hold(pipelineEvent) == true)
            {
                return;
            }

            var stamped = pipelineEvent.RunId.Length == 0 ? pipelineEvent with { RunId = _runId } : pipelineEvent;
            _eventLog?.Emit(stamped);
            _runner.RaiseEvent(stamped);
            try
            {
                _options.OnEvent?.Invoke(stamped);
            }
            catch (Exception)
            {
                // Observers must never break execution.
            }

            if (_options.EventSinks is not null)
            {
                foreach (var sink in _options.EventSinks)
                {
                    try
                    {
                        sink.Emit(stamped);
                    }
                    catch (Exception)
                    {
                        // Sinks must never break execution.
                    }
                }
            }
        }

        private static void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the handler may still hold the file open.
            }
        }

        private static string DescribeError(Exception error) => error switch
        {
            TimeoutException => error.Message,
            _ when error.Message.Length > 0 => error.Message,
            _ => error.GetType().Name,
        };

        private static string DescribeOutcome(Outcome outcome, string fallback) =>
            outcome.FailureReason.Length > 0 ? outcome.FailureReason : outcome.Notes.Length > 0 ? outcome.Notes : fallback;

        private static string SafeName(string name)
        {
            var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
            var safe = new string(chars).Trim('-');
            return safe.Length == 0 ? "pipeline" : safe.Length > 40 ? safe[..40] : safe;
        }
    }

    private enum TransitionKind
    {
        Edge,
        Jump,
        RetryTarget,
        Complete,
        Fail,
    }

    private sealed record Transition(TransitionKind Kind, Edge? Edge, string? Target, string Reason)
    {
        public static Transition Follow(Edge edge, string reason) => new(TransitionKind.Edge, edge, edge.To, reason);

        public static Transition JumpTo(string target, string reason) => new(TransitionKind.Jump, null, target, reason);

        public static Transition Retry(string target) => new(TransitionKind.RetryTarget, null, target, "retry_target");

        public static Transition CompleteRun() => new(TransitionKind.Complete, null, null, "no outgoing edges");

        public static Transition Fail(string reason) => new(TransitionKind.Fail, null, null, reason);
    }
}

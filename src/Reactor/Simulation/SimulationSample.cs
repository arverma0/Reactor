using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;

namespace Reactor.Simulation;

/// <summary>How a simulated run decides stage outcomes.</summary>
public enum SimulationMode
{
    /// <summary>Stage outcomes and human-gate choices are drawn from the scenario.</summary>
    Stochastic,

    /// <summary>
    /// Every simulated stage asks the run's interviewer for its outcome (a step-by-step walkthrough); human gates are
    /// asked for real.
    /// </summary>
    Interactive,
}

/// <summary>
/// One simulated execution of a pipeline: the scenario, the sample's identity (<see cref="Seed"/>, <see cref="RunIndex"/>),
/// and per-run state (execution counts per node). Every random draw is a pure function of (seed, run index, node id,
/// execution number), and except in <see cref="RealTime"/> samples parallel branches run one at a time in edge order, so a
/// sample replays identically.
/// <para>
/// <see cref="CreateRunner"/> builds a dedicated <see cref="PipelineRunner"/> whose stage behaviors are simulated:
/// codergen, tool, manager-loop, and custom handler types return scenario-drawn outcomes (tool commands are never run and
/// child pipelines are never launched); human gates use the real handler with the simulated interviewer from
/// <see cref="CreateInterviewer"/> (or a real interviewer in <see cref="SimulationMode.Interactive"/> mode); start, exit,
/// conditional, parallel, and fan-in nodes run their real handlers. Routing, retries, goal gates, and parallelism are the
/// real engine's.
/// </para>
/// </summary>
public sealed class SimulationSample
{
    private readonly ConcurrentDictionary<string, int> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeSpend> _spend = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<ParallelRecord>> _parallel = new(StringComparer.Ordinal);
    private long _simulatedTimeMs;
    private long _simulatedCostMicros;

    public SimulationSample(SimulationScenario scenario, long seed, int runIndex = 0, SimulationMode mode = SimulationMode.Stochastic)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentOutOfRangeException.ThrowIfNegative(runIndex);
        Scenario = scenario;
        Seed = seed;
        RunIndex = runIndex;
        Mode = mode;
    }

    public SimulationScenario Scenario { get; }

    public long Seed { get; }

    public int RunIndex { get; }

    public SimulationMode Mode { get; }

    /// <summary>
    /// Wait out simulated durations (<c>duration_ms</c>/<c>step_delay_ms</c>) with <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// and run parallel branches concurrently, as in a real run (for animated runs). When false (the default) durations only
    /// add to <see cref="SimulatedTimeMs"/> and branches run one at a time in edge order.
    /// </summary>
    public bool RealTime { get; init; }

    /// <summary>
    /// Latency of an execution when neither the node, its handler type, the scenario's <c>step_delay_ms</c>, nor a
    /// duration prior sets one.
    /// </summary>
    public int DefaultStepDelayMs { get; init; }

    /// <summary>
    /// The scenario's <c>step_delay_ms</c>, else <see cref="DefaultStepDelayMs"/> (executions without their own duration use
    /// the scenario's duration prior for their handler type in between).
    /// </summary>
    public int StepDelayMs => Scenario.StepDelayMs ?? DefaultStepDelayMs;

    /// <summary>Total simulated stage time so far (sum over executions; parallel branches add up).</summary>
    public long SimulatedTimeMs => Interlocked.Read(ref _simulatedTimeMs);

    /// <summary>Total simulated cost so far, in USD (the scenario's <c>cost</c> of every execution).</summary>
    public double SimulatedCostUsd => Interlocked.Read(ref _simulatedCostMicros) / MicrosPerUsd;

    /// <summary>Costs are accounted in millionths of a dollar, so totals do not depend on the order of additions.</summary>
    internal const double MicrosPerUsd = 1_000_000;

    /// <summary>Simulated time and cost per node so far.</summary>
    internal IReadOnlyDictionary<string, NodeSpend> Spend => _spend;

    /// <summary>A fresh random seed (31 bits, so it survives JSON round trips through JavaScript).</summary>
    public static long NewSeed() => Random.Shared.Next(1, int.MaxValue);

    /// <summary>
    /// The uniform draw in [0, 1) for one decision: a pure function of its inputs (64-bit FNV-1a over the inputs, then the
    /// MurmurHash3 finalizer). <paramref name="purpose"/> separates independent decisions of the same execution
    /// (e.g. <c>outcome</c>, <c>label</c>, <c>choice</c>).
    /// </summary>
    public static double Draw(long seed, int runIndex, string nodeId, int execution, string purpose)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(purpose);
        var hash = 14695981039346656037UL;
        hash = Mix(hash, unchecked((ulong)seed));
        hash = Mix(hash, unchecked((ulong)runIndex));
        hash = Mix(hash, nodeId);
        hash = Mix(hash, unchecked((ulong)execution));
        hash = Mix(hash, purpose);
        unchecked
        {
            hash ^= hash >> 33;
            hash *= 0xff51afd7ed558ccdUL;
            hash ^= hash >> 33;
            hash *= 0xc4ceb9fe1a85ec53UL;
            hash ^= hash >> 33;
        }

        return (hash >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>How many times <paramref name="nodeId"/> has executed (attempts included) in this sample.</summary>
    public int ExecutionCount(string nodeId) => _executions.GetValueOrDefault(nodeId);

    /// <summary>
    /// A runner with simulated stage handlers (see the class remarks). <paramref name="options"/> supplies the logs base
    /// directory, event-log setting, and limits; its backend is ignored. <paramref name="customHandlerTypes"/> are the
    /// non-built-in handler types of the runner being simulated: they are simulated like codergen stages (and may have
    /// their own <c>defaults</c> entry).
    /// </summary>
    public PipelineRunner CreateRunner(PipelineRunnerOptions? options = null, IEnumerable<string>? customHandlerTypes = null)
    {
        var runner = new PipelineRunner((options ?? new PipelineRunnerOptions()) with { Backend = null, RealWorkspace = false });
        var codergen = new SimulatedStageHandler(this, HandlerTypes.Codergen, SimulatedStageKind.Codergen);
        runner.RegisterHandler(HandlerTypes.Codergen, codergen);
        runner.Handlers.DefaultHandler = codergen;
        runner.RegisterHandler(HandlerTypes.Tool, new SimulatedStageHandler(this, HandlerTypes.Tool, SimulatedStageKind.Tool));
        runner.RegisterHandler(HandlerTypes.ManagerLoop, new SimulatedStageHandler(this, HandlerTypes.ManagerLoop, SimulatedStageKind.ManagerLoop));
        runner.RegisterHandler(HandlerTypes.WaitHuman, new SimulatedHumanGateHandler(this));
        runner.RegisterHandler(HandlerTypes.Parallel, new RecordingParallelHandler(this));
        foreach (var type in customHandlerTypes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(type) && !HandlerTypes.BuiltIn.Contains(type))
            {
                runner.RegisterHandler(type, new SimulatedStageHandler(this, type, SimulatedStageKind.Codergen));
            }
        }

        return runner;
    }

    /// <summary>An interviewer that answers human gates from the scenario (<c>script</c>, <c>outcomes</c>, <c>choices</c>).</summary>
    public IInterviewer CreateInterviewer(Graph graph) => new SimulatedInterviewer(this, graph ?? throw new ArgumentNullException(nameof(graph)));

    internal double Draw(string nodeId, int execution, string purpose) => Draw(Seed, RunIndex, nodeId, execution, purpose);

    internal int NextExecution(string nodeId) => _executions.AddOrUpdate(nodeId, 1, static (_, count) => count + 1);

    /// <summary>
    /// The simulated duration of one execution: the behavior's <c>duration_ms</c>, else the scenario's
    /// <c>step_delay_ms</c>, else the handler type's duration prior capped at <paramref name="limit"/> (a prior never
    /// times a stage out), else <see cref="DefaultStepDelayMs"/>.
    /// </summary>
    internal int DurationFor(string handlerType, ResolvedBehavior behavior, TimeSpan? limit)
    {
        if ((behavior.DurationMs ?? Scenario.StepDelayMs) is { } explicitMs)
        {
            return explicitMs;
        }

        if (Scenario.PriorFor(handlerType) is { } prior)
        {
            return limit is { } l && l > TimeSpan.Zero && prior > l.TotalMilliseconds ? (int)Math.Floor(l.TotalMilliseconds) : prior;
        }

        return DefaultStepDelayMs;
    }

    /// <summary>
    /// Spends a simulated duration, bounded by <paramref name="limit"/> when set: waits (in <see cref="RealTime"/> mode) and
    /// accounts the time and <paramref name="costUsd"/> to the sample and to <paramref name="nodeId"/>. Returns false when
    /// the limit cut the duration short (the stage timed out).
    /// </summary>
    internal async Task<bool> SpendAsync(string nodeId, int durationMs, TimeSpan? limit, double? costUsd, CancellationToken cancellationToken)
    {
        var micros = costUsd is { } cost and > 0 ? (long)Math.Round(cost * MicrosPerUsd) : 0;
        if (micros > 0)
        {
            Interlocked.Add(ref _simulatedCostMicros, micros);
        }

        if (durationMs <= 0)
        {
            AddSpend(nodeId, 0, micros);
            return true;
        }

        var exceeded = limit is { } l && l > TimeSpan.Zero && durationMs > l.TotalMilliseconds;
        var spent = (long)(exceeded ? Math.Ceiling(limit!.Value.TotalMilliseconds) : durationMs);
        Interlocked.Add(ref _simulatedTimeMs, spent);
        AddSpend(nodeId, spent, micros);
        if (RealTime)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(spent), cancellationToken).ConfigureAwait(false);
        }

        return !exceeded;
    }

    private void AddSpend(string nodeId, long ms, long micros)
    {
        if (ms > 0 || micros > 0)
        {
            _spend.AddOrUpdate(nodeId, new NodeSpend(ms, micros), (_, current) => new NodeSpend(current.TimeMs + ms, current.CostMicros + micros));
        }
    }

    internal void RecordParallel(ParallelRecord record)
    {
        lock (_parallel)
        {
            if (!_parallel.TryGetValue(record.NodeId, out var queue))
            {
                _parallel[record.NodeId] = queue = new Queue<ParallelRecord>();
            }

            queue.Enqueue(record);
        }
    }

    internal bool TryTakeParallel(string nodeId, [NotNullWhen(true)] out ParallelRecord? record)
    {
        lock (_parallel)
        {
            record = null;
            return _parallel.TryGetValue(nodeId, out var queue) && queue.TryDequeue(out record);
        }
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (8 * i));
                hash *= 1099511628211UL;
            }
        }

        return hash;
    }

    private static ulong Mix(ulong hash, string text)
    {
        hash = Mix(hash, (ulong)text.Length);
        unchecked
        {
            foreach (var c in text)
            {
                hash ^= (byte)c;
                hash *= 1099511628211UL;
                hash ^= (byte)(c >> 8);
                hash *= 1099511628211UL;
            }
        }

        return hash;
    }
}

/// <summary>One branch of a recorded parallel execution (from the parallel handler's <c>parallel.results</c>).</summary>
internal sealed record ParallelBranchRecord(string Id, string Status, string? StopNode, IReadOnlyList<string> CompletedNodes);

/// <summary>Simulated time (milliseconds) and cost (millionths of a dollar) spent on one node in a sample.</summary>
internal readonly record struct NodeSpend(long TimeMs, long CostMicros);

/// <summary>A completed execution of a parallel node and the exact path of each branch, in branch order.</summary>
internal sealed record ParallelRecord(string NodeId, IReadOnlyList<ParallelBranchRecord> Branches);

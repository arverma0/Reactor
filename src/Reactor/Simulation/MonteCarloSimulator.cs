using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Validation;

namespace Reactor.Simulation;

/// <summary>Options for <see cref="MonteCarloSimulator"/> and <see cref="SimulationTrace"/>.</summary>
public sealed record MonteCarloOptions
{
    /// <summary>Upper bound for <see cref="Runs"/>.</summary>
    public const int MaxRuns = 5000;

    /// <summary>Upper bound for <see cref="MaxSteps"/>.</summary>
    public const int MaxStepsCap = 5000;

    /// <summary>Number of samples (1 to <see cref="MaxRuns"/>).</summary>
    public int Runs { get; init; } = 500;

    /// <summary>Per-run limit on top-level node executions, as <see cref="RunOptions.MaxSteps"/> (1 to <see cref="MaxStepsCap"/>).</summary>
    public int MaxSteps { get; init; } = 200;

    /// <summary>Samples executed concurrently.</summary>
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Overrides the scenario's seed.</summary>
    public long? Seed { get; init; }

    /// <summary>Values seeded into each run's context.</summary>
    public IReadOnlyDictionary<string, object?>? InitialContext { get; init; }

    /// <summary>Non-built-in handler types of the runner being simulated (simulated like codergen stages).</summary>
    public IReadOnlyCollection<string> CustomHandlerTypes { get; init; } = [];

    /// <summary>Where each sample's scratch run directory is created and deleted (default <c>{temp}/reactor-sim</c>).</summary>
    public string? TempDirectory { get; init; }

    internal void Validate(bool checkRuns = true)
    {
        if (checkRuns && Runs is < 1 or > MaxRuns)
        {
            throw new ArgumentOutOfRangeException(nameof(Runs), Runs, $"Runs must be between 1 and {MaxRuns}.");
        }

        if (MaxSteps is < 1 or > MaxStepsCap)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSteps), MaxSteps, $"MaxSteps must be between 1 and {MaxStepsCap}.");
        }
    }
}

/// <summary>
/// The "wind tunnel": runs a pipeline many times through the real engine with simulated stage behaviors (see
/// <see cref="SimulationSample"/>) and reports how runs end, how long they take, and where they go. Sample <c>i</c> uses
/// (seed, <c>i</c>), so any sample can be replayed exactly with <see cref="SimulationTrace.RunAsync"/>.
/// </summary>
public static partial class MonteCarloSimulator
{
    /// <summary>Simulates a prepared pipeline.</summary>
    /// <exception cref="ValidationException">The pipeline has validation errors.</exception>
    public static Task<SimulationReport> RunAsync(PreparedPipeline pipeline, SimulationScenario scenario, MonteCarloOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.HasErrors)
        {
            throw new ValidationException(pipeline.Diagnostics);
        }

        return RunAsync(pipeline.Graph, scenario, options, cancellationToken);
    }

    /// <summary>Simulates a (transformed) pipeline graph.</summary>
    /// <exception cref="ValidationException">The pipeline has validation errors.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Runs or MaxSteps are outside their bounds.</exception>
    public static async Task<SimulationReport> RunAsync(Graph graph, SimulationScenario scenario, MonteCarloOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(scenario);
        options ??= new MonteCarloOptions();
        options.Validate();
        var seed = options.Seed ?? scenario.Seed ?? SimulationSample.NewSeed();
        SampleRunner.EnsureValid(graph, scenario, options);

        var stopwatch = Stopwatch.StartNew();
        var results = new SampleResult[options.Runs];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, options.Runs), parallel, async (index, token) =>
            results[index] = await SampleRunner.RunAsync(graph, scenario, seed, index, options, SampleDetail.Summary, token).ConfigureAwait(false)).ConfigureAwait(false);

        // Samples keep no paths (they can be long); replaying the few notable ones is cheap and exact.
        async Task<SimulationSampleSummary> SummarizeAsync(SampleResult result)
        {
            var replay = await SampleRunner.RunAsync(graph, scenario, seed, result.RunIndex, options, SampleDetail.Path, cancellationToken).ConfigureAwait(false);
            return new SimulationSampleSummary(seed, result.RunIndex, result.Status, result.Kind, result.FailureReason, result.Steps, replay.Path)
            {
                FailedStages = result.FailedStages,
                SimulatedDurationMs = result.SimulatedDurationMs,
                SimulatedCostUsd = result.SimulatedCostUsd,
            };
        }

        var steps = results.Select(r => r.Steps).Order().ToArray();
        var median = Percentile(steps, 0.5);
        var firstFailure = results.Where(r => r.Kind != SimulationOutcomeKinds.Success).MinBy(r => r.RunIndex);
        var firstFailedStageLanding = results.Where(r => r.LandedWithFailedStages).MinBy(r => r.RunIndex);
        var longest = results.OrderByDescending(r => r.Steps).ThenBy(r => r.RunIndex).First();
        var typical = results.OrderBy(r => Math.Abs(r.Steps - median)).ThenBy(r => r.RunIndex).First();
        var samples = new NotableSamples(
            firstFailure is null ? null : await SummarizeAsync(firstFailure).ConfigureAwait(false),
            firstFailedStageLanding is null ? null : await SummarizeAsync(firstFailedStageLanding).ConfigureAwait(false),
            await SummarizeAsync(longest).ConfigureAwait(false),
            await SummarizeAsync(typical).ConfigureAwait(false));
        stopwatch.Stop();

        return Aggregate(graph, scenario, seed, options, results, steps, samples, stopwatch.Elapsed);
    }

    private sealed record NotableSamples(
        SimulationSampleSummary? FirstFailure,
        SimulationSampleSummary? FirstFailedStageLanding,
        SimulationSampleSummary Longest,
        SimulationSampleSummary Typical);

    private static SimulationReport Aggregate(
        Graph graph,
        SimulationScenario scenario,
        long seed,
        MonteCarloOptions options,
        SampleResult[] results,
        int[] steps,
        NotableSamples samples,
        TimeSpan elapsed)
    {
        var runs = results.Length;
        var outcomes = SimulationOutcomeKinds.All.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var result in results)
        {
            outcomes[result.Kind]++;
        }

        var successes = outcomes[SimulationOutcomeKinds.Success];
        var dirty = results.Count(r => r.LandedWithFailedStages);
        var clean = successes - dirty;
        var durations = results.Select(r => r.SimulatedDurationMs).Order().ToArray();
        var costs = results.Select(r => r.SimulatedCostUsd).Order().ToArray();

        var nodeIds = graph.Nodes.Keys.Concat(results.SelectMany(r => r.Nodes.Keys)).Distinct(StringComparer.Ordinal).ToList();
        var nodes = new Dictionary<string, SimulationNodeStats>(StringComparer.Ordinal);
        foreach (var id in nodeIds)
        {
            var tallies = results.Select(r => r.Nodes.GetValueOrDefault(id)).ToList();
            var outcomeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var metrics = new SortedDictionary<string, MetricTally>(StringComparer.Ordinal);
            foreach (var tally in tallies.OfType<NodeTally>())
            {
                foreach (var (status, count) in tally.Outcomes)
                {
                    outcomeCounts[status] = outcomeCounts.GetValueOrDefault(status) + count;
                }

                foreach (var (name, metric) in tally.Metrics)
                {
                    metrics[name] = metrics.TryGetValue(name, out var merged) ? merged.Merge(metric) : metric;
                }
            }

            var visits = tallies.Sum(t => t?.Visits ?? 0);
            nodes[id] = new SimulationNodeStats(
                visits,
                tallies.Count(t => t is { Visits: > 0 }),
                runs == 0 ? 0 : (double)visits / runs,
                tallies.Max(t => t?.Visits ?? 0),
                outcomeCounts,
                tallies.Sum(t => t?.Retries ?? 0))
            {
                FailuresContinued = tallies.Sum(t => t?.FailuresContinued ?? 0),
                FailedStageLandings = results.Count(r => r.LandedWithFailedStages && r.FailedStages.Contains(id, StringComparer.Ordinal)),
                MeanDurationMs = runs == 0 ? 0 : tallies.Sum(t => t?.TimeMs ?? 0) / (double)runs,
                MeanCostUsd = runs == 0 ? 0 : tallies.Sum(t => t?.CostMicros ?? 0) / SimulationSample.MicrosPerUsd / runs,
                Metrics = metrics.ToDictionary(kv => kv.Key, kv => kv.Value.ToStats(), StringComparer.Ordinal),
            };
        }

        var edgeCounts = new Dictionary<EdgeKey, (int Count, int Runs)>();
        foreach (var result in results)
        {
            foreach (var (key, count) in result.Edges)
            {
                var current = edgeCounts.GetValueOrDefault(key);
                edgeCounts[key] = (current.Count + count, current.Runs + 1);
            }
        }

        var edges = edgeCounts
            .Select(kv => new SimulationEdgeStats(kv.Key.From, kv.Key.To, kv.Key.Kind, kv.Key.Label, kv.Value.Count, kv.Value.Runs))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Label, StringComparer.Ordinal)
            .ToList();

        return new SimulationReport
        {
            Runs = runs,
            Seed = seed,
            DurationMs = (long)elapsed.TotalMilliseconds,
            MaxSteps = options.MaxSteps,
            Outcomes = outcomes,
            SuccessRate = runs == 0 ? 0 : (double)successes / runs,
            SuccessInterval = SimulationReport.WilsonInterval(successes, runs),
            CleanSuccessRate = runs == 0 ? 0 : (double)clean / runs,
            CleanSuccessInterval = SimulationReport.WilsonInterval(clean, runs),
            FailedStageLandings = dirty,
            Steps = StepStats(steps),
            SimulatedDuration = new SimulationDurationStats(durations.Average(), Percentile(durations, 0.5), Percentile(durations, 0.9), durations[^1]),
            SimulatedCost = new SimulationCostStats(costs.Average(), Percentile(costs, 0.5), Percentile(costs, 0.9), costs[^1]),
            Nodes = nodes,
            Edges = edges,
            Failures = GroupFailures(results, seed),
            LandedMetrics = LandedMetrics(results),
            FirstFailure = samples.FirstFailure,
            FirstFailedStageLanding = samples.FirstFailedStageLanding,
            Longest = samples.Longest,
            Typical = samples.Typical,
            Warnings = scenario.Check(graph, options.CustomHandlerTypes),
        };
    }

    /// <summary>
    /// Groups failed runs by (kind, last node, normalized reason), so each group points at one stage; step-limit runs are
    /// grouped across nodes (the same loop, cut off wherever the limit struck) with their last nodes counted. Each group's
    /// sample is its lowest-indexed run that ended at the group's last node.
    /// </summary>
    private static List<SimulationFailureGroup> GroupFailures(SampleResult[] results, long seed) => results
        .Where(r => r.Kind != SimulationOutcomeKinds.Success)
        .GroupBy(r => (r.Kind, Node: r.Kind == SimulationOutcomeKinds.StepLimit ? null : r.LastNode, Reason: NormalizeReason(r.FailureReason)))
        .Select(g =>
        {
            var perNode = new OrderedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in g.Where(r => r.LastNode is not null)
                         .GroupBy(r => r.LastNode!, StringComparer.Ordinal)
                         .OrderByDescending(n => n.Count())
                         .ThenBy(n => n.Key, StringComparer.Ordinal))
            {
                perNode[node.Key] = node.Count();
            }

            var lastNode = g.Key.Node ?? (perNode.Count > 0 ? perNode.GetAt(0).Key : null);

            // The sample to replay ends where the group says (for step limits, at its most frequent last node).
            var first = g.Where(r => r.LastNode == lastNode).MinBy(r => r.RunIndex) ?? g.MinBy(r => r.RunIndex)!;
            return new SimulationFailureGroup(g.Key.Kind, g.Key.Reason, g.Count(), new SimulationSampleRef(seed, first.RunIndex), lastNode) { Nodes = perNode };
        })
        .OrderByDescending(f => f.Count)
        .ThenBy(f => f.Kind, StringComparer.Ordinal)
        .ThenBy(f => f.LastNode, StringComparer.Ordinal)
        .ThenBy(f => f.Reason, StringComparer.Ordinal)
        .ToList();

    /// <summary>Distributions of the final <c>metrics.*</c> values over the runs that landed.</summary>
    private static Dictionary<string, SimulationLandedMetric> LandedMetrics(SampleResult[] results)
    {
        var values = new SortedDictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var result in results.Where(r => r.Kind == SimulationOutcomeKinds.Success))
        {
            foreach (var (name, value) in result.LandedMetrics)
            {
                if (!values.TryGetValue(name, out var list))
                {
                    values[name] = list = [];
                }

                list.Add(value);
            }
        }

        var metrics = new Dictionary<string, SimulationLandedMetric>(StringComparer.Ordinal);
        foreach (var (name, list) in values)
        {
            var sorted = list.Order().ToArray();
            var distinct = sorted.Distinct().Count();
            metrics[name] = new SimulationLandedMetric(sorted.Length, sorted.Average(), sorted[0], Percentile(sorted, 0.1), Percentile(sorted, 0.5), Percentile(sorted, 0.9), sorted[^1])
            {
                Values = distinct <= SimulationLandedMetric.MaxDistinctValues
                    ? sorted.GroupBy(v => v).Select(g => KeyValuePair.Create(g.Key, g.Count())).ToList()
                    : null,
            };
        }

        return metrics;
    }

    private static SimulationStepStats StepStats(int[] sorted)
    {
        var min = sorted[0];
        var max = sorted[^1];
        var bucket = Math.Max(1, (int)Math.Ceiling((max - min + 1) / 30.0));
        while ((max / bucket) - (min / bucket) + 1 > 30)
        {
            bucket++;
        }

        var start = min / bucket * bucket;
        var counts = new int[(max - start) / bucket + 1];
        foreach (var value in sorted)
        {
            counts[(value - start) / bucket]++;
        }

        return new SimulationStepStats(
            min,
            sorted.Average(),
            Percentile(sorted, 0.5),
            Percentile(sorted, 0.9),
            Percentile(sorted, 0.95),
            max,
            bucket,
            counts.Select((count, i) => new SimulationHistogramBucket(start + (i * bucket), count)).ToList());
    }

    /// <summary>Nearest-rank percentile of sorted values.</summary>
    private static T Percentile<T>(T[] sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>Collapses whitespace and replaces run-specific ids so equal failures group together.</summary>
    internal static string NormalizeReason(string reason)
    {
        var text = string.Join(' ', (reason ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = IdPattern().Replace(text, "<id>");
        return text.Length > 500 ? text[..500] + "…" : text;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}

/// <summary>A complete replay of one Monte Carlo sample: every engine event in order, for animating a run.</summary>
public sealed record SimulationTrace
{
    public required long Seed { get; init; }

    public required int RunIndex { get; init; }

    /// <summary><c>success</c> or <c>fail</c>.</summary>
    public required string Status { get; init; }

    /// <summary>One of <see cref="SimulationOutcomeKinds.All"/>.</summary>
    public required string Kind { get; init; }

    public required string FailureReason { get; init; }

    /// <summary>Stage executions (every <c>StageStarted</c>).</summary>
    public required int Steps { get; init; }

    public required long SimulatedDurationMs { get; init; }

    /// <summary>The sample's simulated cost (USD).</summary>
    public double SimulatedCostUsd { get; init; }

    /// <summary>Nodes whose failure flowed onward along an unconditional edge and that no later visit made good (in order).</summary>
    public IReadOnlyList<string> FailedStages { get; init; } = [];

    /// <summary>Node visits in order (parallel branches in branch order).</summary>
    public required IReadOnlyList<string> Path { get; init; }

    /// <summary>The engine's events in emission order.</summary>
    public required IReadOnlyList<PipelineEvent> Events { get; init; }

    /// <summary>Runs sample (<paramref name="seed"/>, <paramref name="runIndex"/>) exactly as <see cref="MonteCarloSimulator"/> does.</summary>
    /// <exception cref="ValidationException">The pipeline has validation errors.</exception>
    public static async Task<SimulationTrace> RunAsync(
        Graph graph,
        SimulationScenario scenario,
        long seed,
        int runIndex = 0,
        MonteCarloOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentOutOfRangeException.ThrowIfNegative(runIndex);
        options ??= new MonteCarloOptions();
        options.Validate(checkRuns: false);
        SampleRunner.EnsureValid(graph, scenario, options);
        var result = await SampleRunner.RunAsync(graph, scenario, seed, runIndex, options, SampleDetail.Events, cancellationToken).ConfigureAwait(false);
        return new SimulationTrace
        {
            Seed = seed,
            RunIndex = runIndex,
            Status = result.Status,
            Kind = result.Kind,
            FailureReason = result.FailureReason,
            Steps = result.Steps,
            SimulatedDurationMs = result.SimulatedDurationMs,
            SimulatedCostUsd = result.SimulatedCostUsd,
            FailedStages = result.FailedStages,
            Path = result.Path,
            Events = result.Events,
        };
    }

    /// <summary>JSON form; each event is <see cref="PipelineEvent.ToJson"/> plus its 1-based <c>seq</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["seed"] = Seed,
        ["run_index"] = RunIndex,
        ["status"] = Status,
        ["kind"] = Kind,
        ["failure_reason"] = FailureReason,
        ["steps"] = Steps,
        ["simulated_duration_ms"] = SimulatedDurationMs,
        ["simulated_cost_usd"] = Math.Round(SimulatedCostUsd, 4),
        ["failed_stages"] = new JsonArray(FailedStages.Select(n => (JsonNode?)n).ToArray()),
        ["path"] = new JsonArray(Path.Select(p => (JsonNode?)p).ToArray()),
        ["events"] = new JsonArray(Events.Select((e, i) =>
        {
            var json = e.ToJson();
            json["seq"] = i + 1;
            return (JsonNode?)json;
        }).ToArray()),
    };
}

/// <summary>Per-node tallies of one sample.</summary>
internal sealed class NodeTally
{
    public int Visits { get; set; }

    public int Retries { get; set; }

    /// <summary>Failures that flowed onward along an unconditional edge (<c>FailedStageContinued</c>).</summary>
    public int FailuresContinued { get; set; }

    public long TimeMs { get; set; }

    public long CostMicros { get; set; }

    public Dictionary<string, int> Outcomes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, MetricTally> Metrics { get; } = new(StringComparer.Ordinal);

    public void AddMetric(string name, double value) =>
        Metrics[name] = Metrics.TryGetValue(name, out var tally) ? tally.Merge(new MetricTally(1, value, value, value)) : new MetricTally(1, value, value, value);
}

/// <summary>Count, sum, and range of a metric's recorded values.</summary>
internal readonly record struct MetricTally(int Count, double Sum, double Min, double Max)
{
    public MetricTally Merge(MetricTally other) => new(Count + other.Count, Sum + other.Sum, Math.Min(Min, other.Min), Math.Max(Max, other.Max));

    public SimulationMetricStats ToStats() => new(Count, Count == 0 ? 0 : Sum / Count, Min, Max);
}

internal readonly record struct EdgeKey(string From, string To, string Kind, string Label);

/// <summary>How much of a sample to keep beyond its statistics.</summary>
internal enum SampleDetail
{
    /// <summary>Statistics and the last node only (Monte Carlo samples).</summary>
    Summary,

    /// <summary>Plus the path (notable samples).</summary>
    Path,

    /// <summary>Plus the path and every event (traces).</summary>
    Events,
}

/// <summary>What one sample did, derived from its events.</summary>
internal sealed class SampleResult
{
    public required int RunIndex { get; init; }

    public required string Status { get; init; }

    public required string Kind { get; init; }

    public required string FailureReason { get; init; }

    public required int Steps { get; init; }

    /// <summary>The last node of the path (kept even when the path is not).</summary>
    public required string? LastNode { get; init; }

    public required Dictionary<string, NodeTally> Nodes { get; init; }

    public required Dictionary<EdgeKey, int> Edges { get; init; }

    public required long SimulatedDurationMs { get; init; }

    public double SimulatedCostUsd { get; init; }

    /// <summary>Nodes whose failure flowed onward along an unconditional edge and that no later visit made good (in order).</summary>
    public IReadOnlyList<string> FailedStages { get; init; } = [];

    /// <summary>The run landed (outcome <c>success</c>) with failed stages behind it.</summary>
    public bool LandedWithFailedStages => Kind == SimulationOutcomeKinds.Success && FailedStages.Count > 0;

    /// <summary>Final <c>metrics.*</c> context values of a landed run (by metric name).</summary>
    public IReadOnlyDictionary<string, double> LandedMetrics { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    public IReadOnlyList<string> Path { get; init; } = [];

    public IReadOnlyList<PipelineEvent> Events { get; init; } = [];
}

/// <summary>Executes one sample through the engine and analyzes its events.</summary>
internal static class SampleRunner
{
    public static string DefaultTempDirectory => Path.Combine(Path.GetTempPath(), "reactor-sim");

    /// <summary>Validates once up front, so an invalid pipeline fails fast instead of erroring in every sample.</summary>
    public static void EnsureValid(Graph graph, SimulationScenario scenario, MonteCarloOptions options)
    {
        var prepared = new SimulationSample(scenario, 0).CreateRunner(customHandlerTypes: options.CustomHandlerTypes).Prepare(graph);
        if (prepared.HasErrors)
        {
            throw new ValidationException(prepared.Diagnostics);
        }
    }

    public static async Task<SampleResult> RunAsync(
        Graph graph,
        SimulationScenario scenario,
        long seed,
        int runIndex,
        MonteCarloOptions options,
        SampleDetail detail,
        CancellationToken cancellationToken)
    {
        var sample = new SimulationSample(scenario, seed, runIndex);
        var directory = Path.Combine(options.TempDirectory ?? DefaultTempDirectory, Guid.NewGuid().ToString("N"));
        var runner = sample.CreateRunner(new PipelineRunnerOptions { WriteEventLog = false, PersistRunState = false, LogsBaseDirectory = directory }, options.CustomHandlerTypes);
        var events = new List<PipelineEvent>();
        PipelineResult? result = null;
        Exception? error = null;
        try
        {
            result = await runner.RunAsync(graph, new RunOptions
            {
                RunId = string.Create(CultureInfo.InvariantCulture, $"sim-{runIndex}"),
                LogsRoot = Path.Combine(directory, "run"),
                WorkingDirectory = directory,
                MaxSteps = options.MaxSteps,
                Backoff = BackoffConfig.Immediate,
                InitialContext = options.InitialContext,
                Interviewer = sample.CreateInterviewer(graph),
                OnEvent = e =>
                {
                    lock (events)
                    {
                        events.Add(e);
                    }
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            TryDeleteDirectory(directory);
        }

        // A cancelled engine run returns a (cancelled) result rather than throwing.
        cancellationToken.ThrowIfCancellationRequested();
        List<PipelineEvent> snapshot;
        lock (events)
        {
            snapshot = [.. events];
        }

        return Analyze(graph, sample, snapshot, result, error, detail);
    }

    /// <summary>
    /// Derives a sample's statistics from its events: visits/retries from <c>StageStarted</c>, final statuses from
    /// <c>StageCompleted</c>, top-level transitions from <c>EdgeSelected</c> and <c>GoalGateUnsatisfied</c>, failures that
    /// flowed onward from <c>FailedStageContinued</c> (a node stays a failed stage of the run until a later visit of it
    /// succeeds), and reported metrics from <c>MetricsRecorded</c> (the last three at any depth). Parallel sections are
    /// expanded from the recorded branch results, in branch order, so the path does not depend on how the branches were
    /// scheduled.
    /// </summary>
    internal static SampleResult Analyze(Graph graph, SimulationSample sample, IReadOnlyList<PipelineEvent> events, PipelineResult? result, Exception? error, SampleDetail detail)
    {
        var nodes = new Dictionary<string, NodeTally>(StringComparer.Ordinal);
        var edges = new Dictionary<EdgeKey, int>();
        var path = new List<string>();
        var failedStages = new List<string>();
        var steps = 0;
        var depth = 0;
        string? section = null;
        string? position = null;

        NodeTally Tally(string id)
        {
            if (!nodes.TryGetValue(id, out var tally))
            {
                nodes[id] = tally = new NodeTally();
            }

            return tally;
        }

        void AddEdge(string from, string to, string kind, string label)
        {
            var key = new EdgeKey(from, to, kind, label);
            edges[key] = edges.GetValueOrDefault(key) + 1;
        }

        void AddStep(string from, string to)
        {
            var edge = graph.OutgoingEdges(from).FirstOrDefault(e => e.To == to);
            AddEdge(from, to, edge is null ? "jump" : "edge", edge?.Label ?? "");
        }

        void Expand(ParallelRecord record)
        {
            foreach (var branch in record.Branches)
            {
                if (branch.CompletedNodes.Count == 0 && branch.Status == StageStatus.Skipped.ToWire())
                {
                    continue;
                }

                AddStep(record.NodeId, branch.Id);
                string? previous = null;
                foreach (var id in branch.CompletedNodes)
                {
                    if (previous is not null)
                    {
                        AddStep(previous, id);
                    }

                    path.Add(id);
                    previous = id;
                    if (graph.GetNode(id) is { } node && HandlerTypes.Resolve(node) == HandlerTypes.Parallel && sample.TryTakeParallel(id, out var nested))
                    {
                        Expand(nested);
                    }
                }

                if (previous is not null && branch.StopNode is { } stop)
                {
                    AddStep(previous, stop);
                }
            }
        }

        foreach (var pipelineEvent in events)
        {
            switch (pipelineEvent)
            {
                case StageStarted started:
                    steps++;
                    if (started.Attempt > 1)
                    {
                        Tally(started.NodeId).Retries++;
                        break;
                    }

                    Tally(started.NodeId).Visits++;
                    if (depth == 0)
                    {
                        path.Add(started.NodeId);
                        position = started.NodeId;
                    }

                    break;
                case StageCompleted completed:
                    var outcomes = Tally(completed.NodeId).Outcomes;
                    outcomes[completed.Status] = outcomes.GetValueOrDefault(completed.Status) + 1;

                    // A later successful visit makes good a failure that flowed onward (e.g. a goal gate sent the run back).
                    if (StageStatusExtensions.TryParse(completed.Status, out var completedStatus) && completedStatus.IsSuccessful())
                    {
                        failedStages.Remove(completed.NodeId);
                    }

                    break;
                case ParallelStarted parallelStarted:
                    if (depth++ == 0)
                    {
                        section = parallelStarted.NodeId;
                    }

                    break;
                case ParallelCompleted parallel when depth > 0:
                    // A nested parallel cancelled mid-way never reports completion, so the outermost section ends when its own
                    // node completes rather than when the counter returns to zero.
                    depth = parallel.NodeId == section ? 0 : Math.Max(1, depth - 1);
                    if (depth == 0)
                    {
                        section = null;
                        if (sample.TryTakeParallel(parallel.NodeId, out var record))
                        {
                            Expand(record);
                        }
                    }

                    break;
                case EdgeSelected selected when depth == 0:
                    AddEdge(selected.From, selected.To, selected.Reason is "jump" or "retry_target" or "max_visits" ? selected.Reason : "edge", selected.Label);
                    position = selected.To;
                    break;
                case GoalGateUnsatisfied { RetryTarget: { } target } gate when depth == 0:
                    AddEdge(position ?? gate.NodeId, target, "goal_gate", "");
                    position = target;
                    break;
                case FailedStageContinued continued:
                    Tally(continued.NodeId).FailuresContinued++;
                    if (!failedStages.Contains(continued.NodeId))
                    {
                        failedStages.Add(continued.NodeId);
                    }

                    break;
                case MetricsRecorded recorded:
                    var metricsTally = Tally(recorded.NodeId);
                    foreach (var (name, value) in recorded.Metrics)
                    {
                        metricsTally.AddMetric(name, value);
                    }

                    break;
            }
        }

        foreach (var (id, spend) in sample.Spend)
        {
            if (graph.ContainsNode(id))
            {
                var tally = Tally(id);
                tally.TimeMs += spend.TimeMs;
                tally.CostMicros += spend.CostMicros;
            }
        }

        var kind = SimulationOutcomeKinds.Classify(result, error);
        return new SampleResult
        {
            RunIndex = sample.RunIndex,
            Status = kind == SimulationOutcomeKinds.Success ? StageStatus.Success.ToWire() : StageStatus.Fail.ToWire(),
            Kind = kind,
            FailureReason = error is not null ? error.GetBaseException().Message : result?.FailureReason ?? "",
            Steps = steps,
            LastNode = path.Count > 0 ? path[^1] : null,
            Nodes = nodes,
            Edges = edges,
            SimulatedDurationMs = sample.SimulatedTimeMs,
            SimulatedCostUsd = sample.SimulatedCostUsd,
            FailedStages = failedStages,
            LandedMetrics = kind == SimulationOutcomeKinds.Success && result is not null ? FinalMetrics(result.Context) : new Dictionary<string, double>(StringComparer.Ordinal),
            Path = detail >= SampleDetail.Path ? path : [],
            Events = detail >= SampleDetail.Events ? events : [],
        };
    }

    /// <summary>The <c>metrics.&lt;name&gt;</c> values of a run's final context.</summary>
    private static Dictionary<string, double> FinalMetrics(IReadOnlyDictionary<string, object?> context)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in context)
        {
            if (key.StartsWith(Outcome.MetricsContextPrefix, StringComparison.Ordinal) && key.Length > Outcome.MetricsContextPrefix.Length)
            {
                double? number = value switch
                {
                    double d => d,
                    float f => f,
                    long l => l,
                    int i => i,
                    decimal m => (double)m,
                    bool b => b ? 1 : 0,
                    string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => null,
                };
                if (number is { } n && double.IsFinite(n))
                {
                    metrics[key[Outcome.MetricsContextPrefix.Length..]] = n;
                }
            }
        }

        return metrics;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: scratch directories live under the temp directory.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

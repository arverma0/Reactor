using System.Text.Json.Nodes;
using Reactor.Execution;

namespace Reactor.Simulation;

/// <summary>How a simulated run ended (the keys of <see cref="SimulationReport.Outcomes"/>).</summary>
public static class SimulationOutcomeKinds
{
    /// <summary>
    /// The run reached the exit with every goal gate satisfied — possibly with failed stages behind it, whose failure
    /// flowed onward along unconditional edges (see <see cref="SimulationReport.CleanSuccessRate"/>).
    /// </summary>
    public const string Success = "success";

    /// <summary>A stage failed and nothing routed the failure onward (or another engine failure).</summary>
    public const string Fail = "fail";

    /// <summary>The run hit the step limit (typically a loop that does not terminate).</summary>
    public const string StepLimit = "step_limit";

    /// <summary>The exit was reached with an unsatisfied goal gate and no retry target.</summary>
    public const string GoalGate = "goal_gate";

    /// <summary>A stage finished but none of its outgoing edges was eligible.</summary>
    public const string DeadEnd = "dead_end";

    /// <summary>The engine threw (e.g. a handler bug or invalid pipeline).</summary>
    public const string Error = "error";

    public static IReadOnlyList<string> All { get; } = [Success, Fail, StepLimit, GoalGate, DeadEnd, Error];

    /// <summary>Classifies a finished run from its result (or the exception the engine threw).</summary>
    public static string Classify(PipelineResult? result, Exception? error)
    {
        if (error is not null || result is null)
        {
            return Error;
        }

        if (result.Status.IsSuccessful())
        {
            return Success;
        }

        var reason = result.FailureReason;
        if (reason.StartsWith("Maximum step count", StringComparison.Ordinal))
        {
            return StepLimit;
        }

        if (reason.Contains("Goal gate", StringComparison.Ordinal))
        {
            return GoalGate;
        }

        return reason.StartsWith("No eligible outgoing edge", StringComparison.Ordinal) ? DeadEnd : Fail;
    }
}

/// <summary>Identifies one Monte Carlo sample; replay it with <see cref="SimulationTrace.RunAsync"/>.</summary>
public sealed record SimulationSampleRef(long Seed, int RunIndex)
{
    public JsonObject ToJson() => new() { ["seed"] = Seed, ["run_index"] = RunIndex };
}

/// <summary>A histogram bucket: runs whose step count is in [<see cref="Steps"/>, <see cref="Steps"/> + bucket size).</summary>
public sealed record SimulationHistogramBucket(int Steps, int Count);

/// <summary>Distribution of stage executions per run (every <c>StageStarted</c>: retries and parallel branches included).</summary>
public sealed record SimulationStepStats(int Min, double Mean, int P50, int P90, int P95, int Max, int BucketSize, IReadOnlyList<SimulationHistogramBucket> Histogram)
{
    public JsonObject ToJson() => new()
    {
        ["min"] = Min,
        ["mean"] = Math.Round(Mean, 2),
        ["p50"] = P50,
        ["p90"] = P90,
        ["p95"] = P95,
        ["max"] = Max,
        ["bucket_size"] = BucketSize,
        ["histogram"] = new JsonArray(Histogram.Select(b => (JsonNode?)new JsonObject { ["steps"] = b.Steps, ["count"] = b.Count }).ToArray()),
    };
}

/// <summary>Distribution of simulated time per run (the sum of the simulated durations of its executions), in milliseconds.</summary>
public sealed record SimulationDurationStats(double Mean, long P50, long P90, long Max)
{
    public JsonObject ToJson() => new() { ["mean"] = Math.Round(Mean, 1), ["p50"] = P50, ["p90"] = P90, ["max"] = Max };
}

/// <summary>Distribution of simulated cost per run (the sum of the scenario's <c>cost</c> over its executions), in USD.</summary>
public sealed record SimulationCostStats(double Mean, double P50, double P90, double Max)
{
    public JsonObject ToJson() => new()
    {
        ["mean"] = Math.Round(Mean, 4),
        ["p50"] = Math.Round(P50, 4),
        ["p90"] = Math.Round(P90, 4),
        ["max"] = Math.Round(Max, 4),
    };
}

/// <summary>An outcome metric reported by a node over all runs (one value per recorded visit).</summary>
/// <param name="Count">Visits that reported the metric.</param>
/// <param name="Mean">Mean value.</param>
/// <param name="Min">Smallest value.</param>
/// <param name="Max">Largest value.</param>
public sealed record SimulationMetricStats(int Count, double Mean, double Min, double Max)
{
    public JsonObject ToJson() => new() { ["count"] = Count, ["mean"] = Math.Round(Mean, 4), ["min"] = Min, ["max"] = Max };
}

/// <summary>
/// An outcome metric's final value (<c>metrics.&lt;name&gt;</c> in the context) over the flights that landed with it:
/// "how good is what the factory ships". <see cref="Values"/> counts flights per value when there are at most
/// <see cref="MaxDistinctValues"/> distinct values (scores, counts).
/// </summary>
public sealed record SimulationLandedMetric(int Flights, double Mean, double Min, double P10, double P50, double P90, double Max)
{
    /// <summary>Most distinct values listed in <see cref="Values"/>.</summary>
    public const int MaxDistinctValues = 20;

    /// <summary>Flights per final value, ascending (null when there are more than <see cref="MaxDistinctValues"/> values).</summary>
    public IReadOnlyList<KeyValuePair<double, int>>? Values { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["flights"] = Flights,
            ["mean"] = Math.Round(Mean, 4),
            ["min"] = Min,
            ["p10"] = P10,
            ["p50"] = P50,
            ["p90"] = P90,
            ["max"] = Max,
        };
        if (Values is { } values)
        {
            json["values"] = new JsonObject(values.Select(kv => KeyValuePair.Create(kv.Key.ToString("R", System.Globalization.CultureInfo.InvariantCulture), (JsonNode?)kv.Value)));
        }

        return json;
    }
}

/// <summary>Per-node statistics over all runs.</summary>
/// <param name="Visits">Executions of the node (first attempts; retries are counted separately).</param>
/// <param name="Runs">Runs that visited the node at least once.</param>
/// <param name="MeanVisits">Visits per run, over all runs.</param>
/// <param name="MaxVisits">Most visits in a single run.</param>
/// <param name="Outcomes">Final status of each visit (after retries).</param>
/// <param name="Retries">Engine retry attempts.</param>
public sealed record SimulationNodeStats(int Visits, int Runs, double MeanVisits, int MaxVisits, IReadOnlyDictionary<string, int> Outcomes, int Retries)
{
    /// <summary>
    /// Failed visits whose failure flowed onward along an unconditional edge (the engine's <c>FailedStageContinued</c>):
    /// the run went on as if the stage had succeeded.
    /// </summary>
    public int FailuresContinued { get; init; }

    /// <summary>
    /// Runs that landed (outcome <c>success</c>) with this node's failure behind them: it flowed onward along an
    /// unconditional edge and no later visit of the node succeeded.
    /// </summary>
    public int FailedStageLandings { get; init; }

    /// <summary>Simulated time spent in the node per run, over all runs (milliseconds).</summary>
    public double MeanDurationMs { get; init; }

    /// <summary>Simulated cost of the node per run, over all runs (USD).</summary>
    public double MeanCostUsd { get; init; }

    /// <summary>Outcome metrics the node reported (by name).</summary>
    public IReadOnlyDictionary<string, SimulationMetricStats> Metrics { get; init; } = new Dictionary<string, SimulationMetricStats>(StringComparer.Ordinal);

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["visits"] = Visits,
            ["runs"] = Runs,
            ["mean_visits"] = Math.Round(MeanVisits, 3),
            ["max_visits"] = MaxVisits,
            ["outcomes"] = new JsonObject(Outcomes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["retries"] = Retries,
            ["failures_continued"] = FailuresContinued,
            ["failed_stage_landings"] = FailedStageLandings,
            ["mean_duration_ms"] = Math.Round(MeanDurationMs, 1),
            ["mean_cost_usd"] = Math.Round(MeanCostUsd, 4),
        };
        if (Metrics.Count > 0)
        {
            json["metrics"] = new JsonObject(Metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.ToJson())));
        }

        return json;
    }
}

/// <summary>A traversed transition.</summary>
/// <param name="From">Source node id.</param>
/// <param name="To">Target node id.</param>
/// <param name="Kind">
/// <c>edge</c> (along a graph edge, including parallel fan-out and branch steps), <c>jump</c> (not along an edge, e.g. a
/// parallel node continuing at its join), <c>retry_target</c> (a failure routed to a retry target), <c>max_visits</c> (a
/// stage at its visit limit rerouted to its <c>on_max_visits</c> target), or <c>goal_gate</c> (an unsatisfied goal gate
/// sending the run back to its retry target).
/// </param>
/// <param name="Label">The traversed edge's label (empty when it has none).</param>
/// <param name="Count">Traversals over all runs.</param>
/// <param name="Runs">Runs that traversed it at least once.</param>
public sealed record SimulationEdgeStats(string From, string To, string Kind, string Label, int Count, int Runs)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["from"] = From, ["to"] = To, ["count"] = Count, ["runs"] = Runs, ["kind"] = Kind };
        if (Label.Length > 0)
        {
            json["label"] = Label;
        }

        return json;
    }
}

/// <summary>
/// Failed runs grouped by kind, last node, and (normalized) reason, with a sample to replay (the lowest-indexed run that
/// ended at <see cref="LastNode"/>). Runs that hit the step limit are grouped across nodes (one loop, wherever the limit
/// struck it); <see cref="Nodes"/> counts the runs per last node, most frequent first, and <see cref="LastNode"/> is the
/// most frequent.
/// </summary>
public sealed record SimulationFailureGroup(string Kind, string Reason, int Count, SimulationSampleRef Sample, string? LastNode)
{
    /// <summary>Runs per last node (most frequent first).</summary>
    public IReadOnlyDictionary<string, int> Nodes { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["reason"] = Reason,
        ["count"] = Count,
        ["sample"] = Sample.ToJson(),
        ["last_node"] = LastNode,
        ["nodes"] = new JsonObject(Nodes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
    };
}

/// <summary>A notable run: its identity, how it ended, and its path (node visits; parallel branches in branch order).</summary>
public sealed record SimulationSampleSummary(long Seed, int RunIndex, string Status, string Kind, string FailureReason, int Steps, IReadOnlyList<string> Path)
{
    /// <summary>Nodes whose failure flowed onward along an unconditional edge and that no later visit made good (in order).</summary>
    public IReadOnlyList<string> FailedStages { get; init; } = [];

    /// <summary>The run's simulated time (milliseconds).</summary>
    public long SimulatedDurationMs { get; init; }

    /// <summary>The run's simulated cost (USD).</summary>
    public double SimulatedCostUsd { get; init; }

    public JsonObject ToJson() => new()
    {
        ["seed"] = Seed,
        ["run_index"] = RunIndex,
        ["status"] = Status,
        ["kind"] = Kind,
        ["failure_reason"] = FailureReason,
        ["steps"] = Steps,
        ["failed_stages"] = new JsonArray(FailedStages.Select(n => (JsonNode?)n).ToArray()),
        ["simulated_duration_ms"] = SimulatedDurationMs,
        ["simulated_cost_usd"] = Math.Round(SimulatedCostUsd, 4),
        ["path"] = new JsonArray(Path.Select(p => (JsonNode?)p).ToArray()),
    };
}

/// <summary>The result of a Monte Carlo simulation (<see cref="MonteCarloSimulator"/>).</summary>
public sealed record SimulationReport
{
    public required int Runs { get; init; }

    public required long Seed { get; init; }

    /// <summary>Wall-clock time of the simulation.</summary>
    public required long DurationMs { get; init; }

    public required int MaxSteps { get; init; }

    /// <summary>Runs per outcome kind (every key of <see cref="SimulationOutcomeKinds.All"/>).</summary>
    public required IReadOnlyDictionary<string, int> Outcomes { get; init; }

    /// <summary>
    /// Share of runs that reached the exit (outcome <c>success</c>), including runs that landed with failed stages behind
    /// them (<see cref="FailedStageLandings"/>). Use <see cref="CleanSuccessRate"/> to judge readiness.
    /// </summary>
    public required double SuccessRate { get; init; }

    /// <summary>Wilson score 95% interval for the success rate.</summary>
    public required (double Low, double High) SuccessInterval { get; init; }

    /// <summary>
    /// Share of runs that landed clean: reached the exit with no stage failure flowing onward along an unconditional edge
    /// (no <c>FailedStageContinued</c> event), or whose failed stages were all made good by a later successful visit (a goal
    /// gate sending the run back, a loop). The readiness number.
    /// </summary>
    public required double CleanSuccessRate { get; init; }

    /// <summary>Wilson score 95% interval for the clean success rate.</summary>
    public required (double Low, double High) CleanSuccessInterval { get; init; }

    /// <summary>
    /// Runs that landed with failed stages: they reached the exit after a stage failure flowed onward along an unconditional
    /// edge and no later visit of that stage succeeded.
    /// </summary>
    public required int FailedStageLandings { get; init; }

    public required SimulationStepStats Steps { get; init; }

    public required SimulationDurationStats SimulatedDuration { get; init; }

    /// <summary>Simulated cost per run (zero unless the scenario sets <c>cost</c>).</summary>
    public required SimulationCostStats SimulatedCost { get; init; }

    /// <summary>Statistics for every node of the pipeline (declaration order).</summary>
    public required IReadOnlyDictionary<string, SimulationNodeStats> Nodes { get; init; }

    /// <summary>Traversed transitions, most frequent first.</summary>
    public required IReadOnlyList<SimulationEdgeStats> Edges { get; init; }

    /// <summary>Failure groups, most frequent first.</summary>
    public required IReadOnlyList<SimulationFailureGroup> Failures { get; init; }

    /// <summary>Final outcome metrics of the runs that landed, by metric name.</summary>
    public IReadOnlyDictionary<string, SimulationLandedMetric> LandedMetrics { get; init; } = new Dictionary<string, SimulationLandedMetric>(StringComparer.Ordinal);

    /// <summary>The lowest-indexed run that did not succeed (null when every run succeeded).</summary>
    public SimulationSampleSummary? FirstFailure { get; init; }

    /// <summary>The lowest-indexed run that landed with failed stages (null when none did).</summary>
    public SimulationSampleSummary? FirstFailedStageLanding { get; init; }

    /// <summary>The run with the most stage executions.</summary>
    public required SimulationSampleSummary Longest { get; init; }

    /// <summary>The run whose stage executions are closest to the median.</summary>
    public required SimulationSampleSummary Typical { get; init; }

    /// <summary>Scenario warnings (see <see cref="SimulationScenario.Check"/>).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public JsonObject ToJson() => new()
    {
        ["runs"] = Runs,
        ["seed"] = Seed,
        ["duration_ms"] = DurationMs,
        ["max_steps"] = MaxSteps,
        ["outcomes"] = new JsonObject(SimulationOutcomeKinds.All.Select(k => KeyValuePair.Create(k, (JsonNode?)Outcomes.GetValueOrDefault(k)))),
        ["success_rate"] = Math.Round(SuccessRate, 4),
        ["success_ci"] = new JsonArray(Math.Round(SuccessInterval.Low, 4), Math.Round(SuccessInterval.High, 4)),
        ["clean_success_rate"] = Math.Round(CleanSuccessRate, 4),
        ["clean_success_ci"] = new JsonArray(Math.Round(CleanSuccessInterval.Low, 4), Math.Round(CleanSuccessInterval.High, 4)),
        ["failed_stage_landings"] = FailedStageLandings,
        ["steps"] = Steps.ToJson(),
        ["simulated_duration_ms"] = SimulatedDuration.ToJson(),
        ["simulated_cost_usd"] = SimulatedCost.ToJson(),
        ["nodes"] = new JsonObject(Nodes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.ToJson()))),
        ["edges"] = new JsonArray(Edges.Select(e => (JsonNode?)e.ToJson()).ToArray()),
        ["failures"] = new JsonArray(Failures.Select(f => (JsonNode?)f.ToJson()).ToArray()),
        ["landed_metrics"] = new JsonObject(LandedMetrics.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.ToJson()))),
        ["samples"] = new JsonObject
        {
            ["first_failure"] = FirstFailure?.ToJson(),
            ["first_failed_stage_landing"] = FirstFailedStageLanding?.ToJson(),
            ["longest"] = Longest.ToJson(),
            ["typical"] = Typical.ToJson(),
        },
        ["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode?)w).ToArray()),
    };

    /// <summary>Wilson score interval for <paramref name="successes"/> out of <paramref name="runs"/> at 95% confidence.</summary>
    public static (double Low, double High) WilsonInterval(int successes, int runs)
    {
        if (runs <= 0)
        {
            return (0, 0);
        }

        const double z = 1.959963984540054;
        var n = (double)runs;
        var p = successes / n;
        var denominator = 1 + (z * z / n);
        var center = (p + (z * z / (2 * n))) / denominator;
        var half = z * Math.Sqrt((p * (1 - p) / n) + (z * z / (4 * n * n))) / denominator;

        // The bounds are exactly 0 and 1 at the extremes; floating point would otherwise land just inside them.
        return (successes <= 0 ? 0 : Math.Max(0, center - half), successes >= runs ? 1 : Math.Min(1, center + half));
    }
}

using System.Text.Json.Nodes;
using Reactor.Execution;

namespace Reactor.Simulation;

/// <summary>How a simulated run ended (the keys of <see cref="SimulationReport.Outcomes"/>).</summary>
public static class SimulationOutcomeKinds
{
    /// <summary>The run reached the exit with every goal gate satisfied.</summary>
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

/// <summary>Distribution of simulated time per run (the sum of <c>duration_ms</c>/<c>step_delay_ms</c> over its executions).</summary>
public sealed record SimulationDurationStats(double Mean, long P50, long P90, long Max)
{
    public JsonObject ToJson() => new() { ["mean"] = Math.Round(Mean, 1), ["p50"] = P50, ["p90"] = P90, ["max"] = Max };
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
    public JsonObject ToJson() => new()
    {
        ["visits"] = Visits,
        ["runs"] = Runs,
        ["mean_visits"] = Math.Round(MeanVisits, 3),
        ["max_visits"] = MaxVisits,
        ["outcomes"] = new JsonObject(Outcomes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["retries"] = Retries,
    };
}

/// <summary>A traversed transition.</summary>
/// <param name="From">Source node id.</param>
/// <param name="To">Target node id.</param>
/// <param name="Kind">
/// <c>edge</c> (along a graph edge, including parallel fan-out and branch steps), <c>jump</c> (not along an edge, e.g. a
/// parallel node continuing at its join), <c>retry_target</c> (a failure routed to a retry target), or <c>goal_gate</c>
/// (an unsatisfied goal gate sending the run back to its retry target).
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

/// <summary>Failed runs grouped by kind and (normalized) reason, with the first sample that failed this way.</summary>
public sealed record SimulationFailureGroup(string Kind, string Reason, int Count, SimulationSampleRef Sample, string? LastNode)
{
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["reason"] = Reason,
        ["count"] = Count,
        ["sample"] = Sample.ToJson(),
        ["last_node"] = LastNode,
    };
}

/// <summary>A notable run: its identity, how it ended, and its path (node visits; parallel branches in branch order).</summary>
public sealed record SimulationSampleSummary(long Seed, int RunIndex, string Status, string Kind, string FailureReason, int Steps, IReadOnlyList<string> Path)
{
    public JsonObject ToJson() => new()
    {
        ["seed"] = Seed,
        ["run_index"] = RunIndex,
        ["status"] = Status,
        ["kind"] = Kind,
        ["failure_reason"] = FailureReason,
        ["steps"] = Steps,
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

    public required double SuccessRate { get; init; }

    /// <summary>Wilson score 95% interval for the success rate.</summary>
    public required (double Low, double High) SuccessInterval { get; init; }

    public required SimulationStepStats Steps { get; init; }

    public required SimulationDurationStats SimulatedDuration { get; init; }

    /// <summary>Statistics for every node of the pipeline (declaration order).</summary>
    public required IReadOnlyDictionary<string, SimulationNodeStats> Nodes { get; init; }

    /// <summary>Traversed transitions, most frequent first.</summary>
    public required IReadOnlyList<SimulationEdgeStats> Edges { get; init; }

    /// <summary>Failure groups, most frequent first.</summary>
    public required IReadOnlyList<SimulationFailureGroup> Failures { get; init; }

    /// <summary>The lowest-indexed run that did not succeed (null when every run succeeded).</summary>
    public SimulationSampleSummary? FirstFailure { get; init; }

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
        ["steps"] = Steps.ToJson(),
        ["simulated_duration_ms"] = SimulatedDuration.ToJson(),
        ["nodes"] = new JsonObject(Nodes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.ToJson()))),
        ["edges"] = new JsonArray(Edges.Select(e => (JsonNode?)e.ToJson()).ToArray()),
        ["failures"] = new JsonArray(Failures.Select(f => (JsonNode?)f.ToJson()).ToArray()),
        ["samples"] = new JsonObject
        {
            ["first_failure"] = FirstFailure?.ToJson(),
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

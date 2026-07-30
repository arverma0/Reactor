using Reactor.Events;
using Reactor.Simulation;

namespace Reactor.Tests.Simulation;

/// <summary>Outcome metrics, simulated cost, durations (and their priors) in the wind tunnel.</summary>
[Collection(Sim.Collection)]
public sealed class MetricsAndCostTests
{
    /// <summary>A review that ships only at score 5; otherwise a polish round and another review.</summary>
    private const string ScoreGate = """
        digraph Score {
            start [shape=Mdiamond] exit [shape=Msquare]
            review [prompt="review"]
            polish [prompt="polish"]
            start -> review
            review -> exit [condition="metrics.score=5"]
            review -> polish [condition="metrics.score!=5"]
            polish -> review
        }
        """;

    [Fact]
    public async Task Metrics_are_drawn_per_execution_recorded_and_route_the_flight()
    {
        const string scenario = """
            {"nodes": {"review": {"metrics": {"score": {"values": {"3": 1, "4": 1, "5": 2}},
                                              "findings": {"min": 0, "max": 3},
                                              "coverage": {"min": 0.5, "max": 1.0}}}}}
            """;
        var graph = Sim.Prepare(ScoreGate);
        var report = await Sim.ReportAsync(graph, scenario, runs: 300, seed: 5);

        Assert.Equal(300, report.Outcomes["success"]);
        var review = report.Nodes["review"];
        Assert.Equal(review.Visits - 300, Sim.Visits(report, "polish"));
        Assert.InRange(Sim.Visits(report, "polish") / (double)review.Visits, 0.4, 0.6);
        var score = review.Metrics["score"];
        Assert.Equal(review.Visits, score.Count);
        Assert.Equal((3.0, 5.0), (score.Min, score.Max));
        Assert.Equal((0.0, 3.0), (review.Metrics["findings"].Min, review.Metrics["findings"].Max));
        Assert.InRange(review.Metrics["coverage"].Min, 0.5, 1.0);
        Assert.InRange(review.Metrics["coverage"].Max, 0.5, 1.0);
        Assert.Empty(report.Nodes["polish"].Metrics);

        // Only flights scoring 5 land: the distribution at landing says so.
        var landed = report.LandedMetrics["score"];
        Assert.Equal(300, landed.Flights);
        Assert.Equal([KeyValuePair.Create(5.0, 300)], landed.Values);
        Assert.Equal(5.0, landed.P50);
        Assert.Equal(300, report.LandedMetrics["findings"].Flights);

        var trace = await SimulationTrace.RunAsync(graph, Sim.Scenario(scenario), report.Longest.Seed, report.Longest.RunIndex, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(report.Longest.Path, trace.Path);
        var recorded = trace.Events.OfType<MetricsRecorded>().ToList();
        Assert.Equal(trace.Path.Count(n => n == "review"), recorded.Count);
        Assert.All(recorded, m =>
        {
            Assert.Equal("review", m.NodeId);
            Assert.Equal(Math.Floor(m.Metrics["findings"]), m.Metrics["findings"]);
            Assert.InRange(m.Metrics["coverage"], 0.5, 1.0);
        });
        Assert.Equal(5.0, recorded[^1].Metrics["score"]);

        var json = report.ToJson();
        Assert.Equal(review.Visits, json["nodes"]!["review"]!["metrics"]!["score"]!["count"]!.GetValue<int>());
        Assert.Equal(300, json["landed_metrics"]!["score"]!["values"]!["5"]!.GetValue<int>());
        Assert.Null(json["nodes"]!["polish"]!["metrics"]);
    }

    [Fact]
    public async Task Per_status_metrics_follow_the_drawn_status()
    {
        var report = await Sim.ReportAsync(Sim.TestFixLoop, """
            {"nodes": {"test": {"outcomes": {"success": 0.5, "fail": 0.5},
                                "metrics": {"qa_failed": {"success": 0, "fail": {"min": 1, "max": 5}}}}}}
            """, runs: 200);

        var qa = report.Nodes["test"].Metrics["qa_failed"];
        Assert.Equal(report.Nodes["test"].Visits, qa.Count);
        Assert.Equal(0, qa.Min);
        Assert.InRange(qa.Max, 1, 5);
        Assert.Equal([KeyValuePair.Create(0.0, 200)], report.LandedMetrics["qa_failed"].Values);
    }

    [Fact]
    public async Task Costs_add_up_per_execution_retries_included()
    {
        const string dot = """
            digraph Cost {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a", max_retries=1]
                b [prompt="b"]
                start -> a -> b -> exit
            }
            """;
        var graph = Sim.Prepare(dot);
        const string scenario = """{"nodes": {"a": {"cost": 0.25, "script": ["retry", "success"]}, "b": {"cost": "$1.5"}}}""";

        var report = await Sim.ReportAsync(graph, scenario, runs: 20);
        var trace = await SimulationTrace.RunAsync(graph, Sim.Scenario(scenario), 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new SimulationCostStats(2, 2, 2, 2), report.SimulatedCost);
        Assert.Equal(0.5, report.Nodes["a"].MeanCostUsd, 6);
        Assert.Equal(1.5, report.Nodes["b"].MeanCostUsd, 6);
        Assert.Equal(0, report.Nodes["start"].MeanCostUsd);
        Assert.Equal(2, trace.SimulatedCostUsd, 6);
        Assert.Equal(2.0, report.ToJson()["simulated_cost_usd"]!["mean"]!.GetValue<double>());
        Assert.Equal(2.0, report.Longest.SimulatedCostUsd, 6);
    }

    [Fact]
    public async Task Duration_priors_fill_in_typical_durations_without_timing_stages_out()
    {
        const string dot = """
            digraph Priors {
                start [shape=Mdiamond] exit [shape=Msquare]
                plan [prompt="plan"]
                quick [prompt="quick", timeout="5m"]
                test [shape=parallelogram, tool_command="pytest", timeout="30s"]
                start -> plan -> quick
                quick -> test [condition="outcome=success"]
                test -> exit [condition="outcome=success"]
            }
            """;
        var graph = Sim.Prepare(dot);

        var priors = await Sim.ReportAsync(graph, """{"duration_priors": {"codergen": "10m", "tool": "1m"}}""", runs: 10);
        var stepDelay = await Sim.ReportAsync(graph, """{"step_delay_ms": 1000, "duration_priors": {"codergen": "10m"}}""", runs: 10);
        var explicitDuration = await Sim.ReportAsync(graph, """{"duration_priors": {"codergen": "10m"}, "nodes": {"quick": {"duration_ms": "6m"}}}""", runs: 10);

        // plan 10m + quick capped at its 5m timeout + test capped at its 30s timeout; nothing times out.
        Assert.Equal(10, priors.Outcomes["success"]);
        Assert.Equal(930_000, priors.SimulatedDuration.Max);
        Assert.Equal(930_000, priors.SimulatedDuration.P50);
        Assert.Equal(600_000, priors.Nodes["plan"].MeanDurationMs);
        Assert.Equal(300_000, priors.Nodes["quick"].MeanDurationMs);
        Assert.Equal(3_000, stepDelay.SimulatedDuration.Max);

        // An explicit duration beyond the timeout still times the stage out, as in a real run.
        Assert.Equal(0, explicitDuration.Outcomes["success"]);
        Assert.Contains(explicitDuration.Failures, f => f.Reason.Contains("timed out", StringComparison.Ordinal) && f.LastNode == "quick");
    }

    [Fact]
    public async Task Stage_durations_up_to_a_day_are_accepted()
    {
        const string dot = """
            digraph Long {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build", timeout="4h"]
                start -> build -> exit
            }
            """;

        var report = await Sim.ReportAsync(dot, """{"nodes": {"build": {"duration_ms": "150m"}}}""", runs: 5);
        var tooLong = SimulationScenario.TryFromJson(System.Text.Json.Nodes.JsonNode.Parse("""{"nodes": {"build": {"duration_ms": "25h"}}}""")!.AsObject(), out _, out var errors);

        Assert.Equal(5, report.Outcomes["success"]);
        Assert.Equal(9_000_000, report.SimulatedDuration.Max);
        Assert.Equal(86_400_000, SimulationScenario.MaxDurationMs);
        Assert.False(tooLong);
        Assert.Contains("24h", Assert.Single(errors).Message, StringComparison.Ordinal);
    }
}

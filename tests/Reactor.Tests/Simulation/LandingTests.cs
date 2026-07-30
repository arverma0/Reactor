using Reactor.Events;
using Reactor.Simulation;

namespace Reactor.Tests.Simulation;

/// <summary>Clean landings, landings with failed stages, and failure groups that point at the stage.</summary>
[Collection(Sim.Collection)]
public sealed class LandingTests
{
    /// <summary>a and b flow on unconditionally: nothing routes their failures.</summary>
    private const string Chain = """
        digraph Chain {
            start [shape=Mdiamond] exit [shape=Msquare]
            a [prompt="a"]
            b [prompt="b"]
            start -> a -> b -> exit
        }
        """;

    /// <summary>Every stage routes on success only: a failure anywhere ends the flight at that stage.</summary>
    private const string Strict = """
        digraph OnSuccess {
            start [shape=Mdiamond] exit [shape=Msquare]
            a [prompt="a"]
            b [prompt="b"]
            c [shape=parallelogram, tool_command="make test"]
            start -> a
            a -> b [condition="outcome=success"]
            b -> c [condition="outcome=success"]
            c -> exit [condition="outcome=success"]
        }
        """;

    /// <summary>A gate picks a direct or a longer route into a test/fix loop, so the step limit strikes at either node.</summary>
    private const string TwoRoutesIntoALoop = """
        digraph Loop {
            start [shape=Mdiamond] exit [shape=Msquare]
            gate [shape=hexagon, label="Route?"]
            detour [prompt="detour"]
            test [shape=parallelogram, tool_command="pytest"]
            fix [prompt="fix"]
            start -> gate
            gate -> test [label="[D] Direct"]
            gate -> detour [label="[L] Long way"]
            detour -> test
            test -> exit [condition="outcome=success"]
            test -> fix [condition="outcome=fail"]
            fix -> test
        }
        """;

    [Fact]
    public async Task Flights_that_land_after_a_failure_flowed_onward_are_not_clean()
    {
        const string scenario = """{"nodes": {"a": {"outcomes": {"success": 0.7, "fail": 0.3}}}}""";
        var graph = Sim.Prepare(Chain);
        var report = await Sim.ReportAsync(graph, scenario, runs: 400, seed: 3);

        Assert.Equal(400, report.Outcomes["success"]);
        Assert.Equal(1.0, report.SuccessRate);
        var failedA = report.Nodes["a"].Outcomes["fail"];
        Assert.InRange(failedA, 80, 160);
        Assert.Equal(failedA, report.FailedStageLandings);
        Assert.Equal(failedA, report.Nodes["a"].FailedStageLandings);
        Assert.Equal(failedA, report.Nodes["a"].FailuresContinued);
        Assert.Equal(0, report.Nodes["b"].FailedStageLandings);
        Assert.Equal((400 - failedA) / 400.0, report.CleanSuccessRate, 6);
        Assert.Equal(SimulationReport.WilsonInterval(400 - failedA, 400), report.CleanSuccessInterval);

        var sample = Assert.IsType<SimulationSampleSummary>(report.FirstFailedStageLanding);
        Assert.Equal(["a"], sample.FailedStages);
        var trace = await SimulationTrace.RunAsync(graph, Sim.Scenario(scenario), sample.Seed, sample.RunIndex, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("success", trace.Kind);
        Assert.Equal(["a"], trace.FailedStages);
        Assert.Equal("b", Assert.Single(trace.Events.OfType<FailedStageContinued>()).To);

        var json = report.ToJson();
        Assert.Equal(failedA, json["failed_stage_landings"]!.GetValue<int>());
        Assert.Equal(Math.Round(report.CleanSuccessRate, 4), json["clean_success_rate"]!.GetValue<double>());
        Assert.Equal(2, json["clean_success_ci"]!.AsArray().Count);
        Assert.Equal(failedA, json["nodes"]!["a"]!["failed_stage_landings"]!.GetValue<int>());
        Assert.Equal(failedA, json["nodes"]!["a"]!["failures_continued"]!.GetValue<int>());
        Assert.Equal(["a"], json["samples"]!["first_failed_stage_landing"]!["failed_stages"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["a"], trace.ToJson()["failed_stages"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Clean_flights_have_no_failed_stage_landing_sample()
    {
        var report = await Sim.ReportAsync(Chain, runs: 20);

        Assert.Equal(1.0, report.CleanSuccessRate);
        Assert.Equal(0, report.FailedStageLandings);
        Assert.Null(report.FirstFailedStageLanding);
        Assert.Null(report.ToJson()["samples"]!["first_failed_stage_landing"]);
    }

    [Fact]
    public async Task Failures_made_good_by_a_later_visit_and_routed_failures_land_clean()
    {
        // implement (a goal gate, retried once) fails and flows on to test, but the gate sends the flight back until it succeeds; test's
        // failures are routed explicitly to fix.
        var report = await Sim.ReportAsync(Sim.Prepare("""
            digraph test_fix_loop {
              graph [default_max_retries=1, retry_target="implement"]
              start [shape=Mdiamond] exit [shape=Msquare]
              implement [prompt="i", goal_gate=true]
              test [shape=parallelogram, tool_command="t"]
              fix [prompt="f"]
              start -> implement -> test
              test -> exit [condition="outcome=success"]
              test -> fix  [condition="outcome=fail"]
              fix -> test
            }
            """), """
            {"nodes": {"implement": {"script": ["fail", "fail", "success"]}, "test": {"outcomes": {"success": 0.5, "fail": 0.5}}}}
            """, runs: 100);

        Assert.Equal(100, report.Outcomes["success"]);
        Assert.Equal(100, report.Nodes["implement"].FailuresContinued);
        Assert.Equal(0, report.FailedStageLandings);
        Assert.Equal(1.0, report.CleanSuccessRate);
        Assert.Equal(0, report.Nodes["test"].FailuresContinued);
    }

    [Fact]
    public async Task Failures_flowing_on_inside_parallel_branches_count()
    {
        // A failure that flows on to the next stage of its branch is silent; one that ends the branch at the join is the
        // fan-in's to judge (the parallel node reports partial_success), so it is not counted.
        var graph = Sim.Prepare("""
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              reviews [shape=component] gather [shape=tripleoctagon]
              perf [prompt="p"] perf_notes [prompt="n"] style [prompt="s"]
              start -> reviews
              reviews -> perf -> perf_notes -> gather
              reviews -> style -> gather
              gather -> exit
            }
            """);
        var report = await Sim.ReportAsync(graph, """{"nodes": {"perf": {"outcomes": "fail"}, "style": {"outcomes": "fail"}}}""", runs: 30);

        Assert.Equal(30, report.Outcomes["success"]);
        Assert.Equal(30, report.FailedStageLandings);
        Assert.Equal(30, report.Nodes["perf"].FailedStageLandings);
        Assert.Equal(0, report.Nodes["style"].FailedStageLandings);
        Assert.Equal(0.0, report.CleanSuccessRate);
        Assert.Equal(["perf"], report.FirstFailedStageLanding!.FailedStages);
    }

    [Fact]
    public async Task Failure_groups_point_at_the_stage_where_flights_go_down()
    {
        const string scenario = """{"defaults": {"codergen": {"outcomes": {"success": 0.8, "fail": 0.2}}, "tool": {"outcomes": {"success": 0.8, "fail": 0.2}}}}""";
        var graph = Sim.Prepare(Strict);
        var report = await Sim.ReportAsync(graph, scenario, runs: 400, seed: 11);

        Assert.Equal(["a", "b", "c"], report.Failures.Select(f => f.LastNode).Order(StringComparer.Ordinal));
        Assert.Equal(report.Outcomes["fail"], report.Failures.Sum(f => f.Count));
        foreach (var group in report.Failures)
        {
            Assert.Equal("fail", group.Kind);
            Assert.Equal($"[Simulated] failure at {group.LastNode}", group.Reason);
            Assert.Equal(group.Count, Assert.Single(group.Nodes, kv => kv.Key == group.LastNode).Value);
            Assert.Equal(report.Nodes[group.LastNode!].Outcomes["fail"], group.Count);
            var replay = await SimulationTrace.RunAsync(graph, Sim.Scenario(scenario), group.Sample.Seed, group.Sample.RunIndex, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(group.LastNode, replay.Path[^1]);
        }

        var json = report.Failures[0].ToJson();
        Assert.Equal(report.Failures[0].Count, json["nodes"]![report.Failures[0].LastNode!]!.GetValue<int>());
    }

    [Fact]
    public async Task Step_limit_groups_span_the_loop_and_count_their_last_nodes()
    {
        const string scenario = """{"nodes": {"test": {"outcomes": "fail"}}}""";
        var graph = Sim.Prepare(TwoRoutesIntoALoop);
        var report = await Sim.ReportAsync(graph, scenario, runs: 200, maxSteps: 21);

        var group = Assert.Single(report.Failures);
        Assert.Equal("step_limit", group.Kind);
        Assert.Equal(200, group.Count);
        Assert.Equal(["fix", "test"], group.Nodes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(200, group.Nodes.Values.Sum());
        Assert.Equal(group.Nodes.MaxBy(kv => kv.Value).Key, group.LastNode);
        Assert.True(group.Nodes.Values.First() >= group.Nodes.Values.Last(), "per-node counts are ordered most frequent first");
        var replay = await SimulationTrace.RunAsync(graph, Sim.Scenario(scenario), group.Sample.Seed, group.Sample.RunIndex,
            new MonteCarloOptions { MaxSteps = 21 }, TestContext.Current.CancellationToken);
        Assert.Equal(group.LastNode, replay.Path[^1]);
    }
}

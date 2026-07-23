using System.Diagnostics;
using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Graphs;
using Reactor.Simulation;
using Reactor.Tests.Support;
using Reactor.Validation;

namespace Reactor.Tests.Simulation;

public sealed class MonteCarloTests
{
    private const string FactoryScenario = """{"defaults": {"tool": {"outcomes": {"success": 0.6, "fail": 0.4}}}}""";

    public static TheoryData<string> Examples => ExampleTests.Examples;

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Every_example_simulates_without_errors(string name)
    {
        var report = await Sim.ReportAsync(Sim.PrepareExample(name), """
            {"defaults": {"codergen": {"outcomes": {"success": 0.8, "fail": 0.15, "retry": 0.05}},
                          "tool": {"outcomes": {"success": 0.6, "fail": 0.4}},
                          "stack.manager_loop": {"outcomes": {"success": 0.9, "fail": 0.1}}}}
            """, runs: 60);

        Assert.Equal(0, report.Outcomes["error"]);
        Assert.Equal(60, report.Outcomes.Values.Sum());
        Assert.Empty(report.Warnings);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Empty_scenario_succeeds_on_every_example(string name)
    {
        var report = await Sim.ReportAsync(Sim.PrepareExample(name), runs: 40);

        Assert.Equal(40, report.Outcomes["success"]);
        Assert.Equal(1.0, report.SuccessRate);
        Assert.Null(report.FirstFailure);
        Assert.Empty(report.Failures);
    }

    [Theory]
    [InlineData("smoke.dot", new[] { "start", "plan", "implement", "review", "done" })]
    [InlineData("test-fix-loop.dot", new[] { "start", "implement", "test", "exit" })]
    [InlineData("stylesheet.dot", new[] { "start", "architecture", "implement", "write_tests", "final_review", "exit" })]
    [InlineData("parallel-review.dot", new[] { "start", "reviews", "security", "perf", "style", "gather", "summarize", "exit" })]
    [InlineData("supervisor.dot", new[] { "start", "manage", "exit" })]
    public async Task Empty_scenario_follows_the_happy_path(string name, string[] path)
    {
        var graph = Sim.PrepareExample(name);
        var trace = await SimulationTrace.RunAsync(graph, SimulationScenario.Empty, 1, 0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(path, trace.Path);
        Assert.Equal("success", trace.Kind);

        // Nothing outside the pipeline ran (supervisor.dot must not launch child-worker.dot).
        Assert.All(trace.Events.OfType<StageStarted>(), e => Assert.True(graph.ContainsNode(e.NodeId), e.NodeId));
    }

    [Fact]
    public async Task Runs_are_classified_by_how_they_end()
    {
        var stepLimit = await Sim.ReportAsync(Sim.TestFixLoop, """{"nodes": {"test": {"outcomes": {"fail": 1}}}}""", runs: 20, maxSteps: 40);
        var goalGate = await Sim.ReportAsync("""
            digraph G {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build", goal_gate=true]
                start -> build -> exit
            }
            """, """{"nodes": {"build": {"outcomes": "fail"}}}""", runs: 20);
        var deadEnd = await Sim.ReportAsync("""
            digraph D {
                start [shape=Mdiamond] exit [shape=Msquare]
                work [prompt="work"]
                start -> work
                work -> exit [condition="outcome=fail"]
            }
            """, runs: 20);
        var fail = await Sim.ReportAsync("""
            digraph F {
                start [shape=Mdiamond] exit [shape=Msquare]
                work [prompt="work"]
                start -> work
                work -> exit [condition="outcome=success"]
            }
            """, """{"nodes": {"work": {"outcomes": "fail"}}}""", runs: 20);

        Assert.Equal(20, stepLimit.Outcomes["step_limit"]);
        var limit = Assert.Single(stepLimit.Failures);
        Assert.Equal("step_limit", limit.Kind);
        Assert.Equal("Maximum step count (40) exceeded.", limit.Reason);
        Assert.Equal(20, limit.Count);
        Assert.Equal(new SimulationSampleRef(1, 0), limit.Sample);
        Assert.Equal("fix", limit.LastNode);
        Assert.Equal(20, goalGate.Outcomes["goal_gate"]);
        Assert.Equal("Goal gate 'build' unsatisfied (fail) and no retry target.", Assert.Single(goalGate.Failures).Reason);
        Assert.Equal(20, deadEnd.Outcomes["dead_end"]);
        Assert.StartsWith("No eligible outgoing edge from 'work'", Assert.Single(deadEnd.Failures).Reason, StringComparison.Ordinal);
        Assert.Equal(20, fail.Outcomes["fail"]);
        Assert.Equal("[Simulated] failure", Assert.Single(fail.Failures).Reason);
        Assert.Equal("work", fail.Failures[0].LastNode);
        Assert.Equal(0.0, fail.SuccessRate);
    }

    [Fact]
    public async Task Same_seed_gives_an_identical_report_and_traces_replay_samples()
    {
        var graph = Sim.PrepareExample("factory.dot");
        var first = (await Sim.ReportAsync(graph, FactoryScenario, runs: 150, seed: 123)).ToJson();
        var second = (await Sim.ReportAsync(graph, FactoryScenario, runs: 150, seed: 123)).ToJson();
        var other = (await Sim.ReportAsync(graph, FactoryScenario, runs: 150, seed: 124)).ToJson();
        first.Remove("duration_ms");
        second.Remove("duration_ms");
        other.Remove("duration_ms");

        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.NotEqual(first.ToJsonString(), other.ToJsonString());

        foreach (var name in new[] { "longest", "typical" })
        {
            var sample = first["samples"]![name]!;
            var trace = await SimulationTrace.RunAsync(graph, Sim.Scenario(FactoryScenario), sample["seed"]!.GetValue<long>(), sample["run_index"]!.GetValue<int>(),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(sample["path"]!.AsArray().Select(p => p!.GetValue<string>()), trace.Path);
            Assert.Equal(sample["steps"]!.GetValue<int>(), trace.Steps);
            Assert.Equal(sample["kind"]!.GetValue<string>(), trace.Kind);
        }
    }

    [Fact]
    public async Task Failure_samples_replay_through_traces()
    {
        const string scenario = """{"defaults": {"tool": {"outcomes": {"success": 0.2, "fail": 0.8}}}}""";
        var graph = Sim.PrepareExample("factory.dot");
        var report = await Sim.ReportAsync(graph, scenario, runs: 60, seed: 9, maxSteps: 30);
        var failure = Assert.IsType<SimulationSampleSummary>(report.FirstFailure);

        var replay = await SimulationTrace.RunAsync(graph, Sim.Scenario(scenario), failure.Seed, failure.RunIndex,
            new MonteCarloOptions { MaxSteps = 30 }, TestContext.Current.CancellationToken);

        Assert.Equal(failure.Path, replay.Path);
        Assert.Equal(failure.Kind, replay.Kind);
        Assert.Equal(failure.FailureReason, replay.FailureReason);
        Assert.Equal(failure.Steps, replay.Steps);
        var group = report.Failures.Single(f => f.Sample.RunIndex == failure.RunIndex);
        Assert.Equal(failure.Path[^1], group.LastNode);
    }

    [Fact]
    public async Task A_missing_seed_is_chosen_and_reported()
    {
        var graph = Sim.PrepareExample("factory.dot");
        var report = await Sim.ReportAsync(graph, FactoryScenario, runs: 30, seed: null);
        var again = await Sim.ReportAsync(graph, FactoryScenario, runs: 30, seed: report.Seed);
        var fromScenario = await MonteCarloSimulator.RunAsync(graph, Sim.Scenario(FactoryScenario) with { Seed = report.Seed }, new MonteCarloOptions { Runs = 30 },
            TestContext.Current.CancellationToken);

        Assert.InRange(report.Seed, 1, int.MaxValue);
        Assert.Equal(report.Longest.Path, again.Longest.Path);
        Assert.Equal(report.Steps.ToJson().ToJsonString(), again.Steps.ToJson().ToJsonString());
        Assert.Equal(report.Seed, fromScenario.Seed);
        Assert.Equal(report.Typical.Path, fromScenario.Typical.Path);
    }

    [Fact]
    public async Task Preferred_label_weights_route_review_outcomes()
    {
        var report = await Sim.ReportAsync(Sim.LabeledReview, """{"nodes": {"review": {"labels": {"Approve": 0.7, "Fix": 0.3}}}}""", runs: 300);

        Assert.Equal(300, report.Outcomes["success"]);
        Assert.Equal(300, Sim.EdgeCount(report, "review", "exit"));
        Assert.Equal(Sim.Visits(report, "fix"), Sim.EdgeCount(report, "review", "fix"));
        Assert.Equal(Sim.Visits(report, "fix"), Sim.EdgeCount(report, "fix", "review"));
        Assert.Equal(300 + Sim.Visits(report, "fix"), Sim.Visits(report, "review"));
        var fixShare = (double)Sim.Visits(report, "fix") / Sim.Visits(report, "review");
        Assert.InRange(fixShare, 0.22, 0.38);
        var edge = report.Edges.Single(e => e.From == "review" && e.To == "fix");
        Assert.Equal("edge", edge.Kind);
        Assert.Equal("Fix", edge.Label);

        var unlabeled = await Sim.ReportAsync(Sim.LabeledReview, runs: 20);
        Assert.Equal(0, Sim.Visits(unlabeled, "fix"));
    }

    [Fact]
    public async Task Choice_weights_route_human_gates()
    {
        var uniform = await Sim.ReportAsync(Sim.ShipGate, runs: 400);
        var byTarget = await Sim.ReportAsync(Sim.ShipGate, """{"nodes": {"gate": {"choices": {"rework": 1}}}}""", runs: 50);
        var byKey = await Sim.ReportAsync(Sim.ShipGate, """{"nodes": {"gate": {"choices": {"a": 3, "[R] Rework": 1}}}}""", runs: 400);

        Assert.InRange(Sim.Visits(uniform, "ship"), 160, 240);
        Assert.Equal(400, Sim.Visits(uniform, "ship") + Sim.Visits(uniform, "rework"));
        Assert.Equal(0, Sim.Visits(byTarget, "ship"));
        Assert.Equal(50, Sim.Visits(byTarget, "rework"));
        Assert.InRange(Sim.Visits(byKey, "ship"), 260, 340);
        Assert.Equal("[A] Approve", byKey.Edges.Single(e => e.From == "gate" && e.To == "ship").Label);
    }

    [Fact]
    public async Task Context_updates_drive_context_conditions()
    {
        const string dot = """
            digraph Ctx {
                start [shape=Mdiamond] exit [shape=Msquare]
                test [shape=parallelogram, tool_command="run tests"]
                celebrate [prompt="celebrate"]
                investigate [prompt="investigate"]
                start -> test
                test -> celebrate [condition="context.tests_passed=true"]
                test -> investigate [condition="context.tests_passed=false"]
                celebrate -> exit
                investigate -> exit
            }
            """;

        var report = await Sim.ReportAsync(dot, """
            {"nodes": {"test": {"outcomes": {"success": 0.5, "fail": 0.5},
                                "context_updates": {"success": {"tests_passed": true}, "fail": {"tests_passed": false}}}}}
            """, runs: 200);

        Assert.Equal(200, report.Outcomes["success"]);
        var test = report.Nodes["test"];
        Assert.Equal(test.Outcomes["success"], Sim.Visits(report, "celebrate"));
        Assert.Equal(test.Outcomes["fail"], Sim.Visits(report, "investigate"));
        Assert.InRange(test.Outcomes["fail"], 70, 130);
        Assert.All(report.Edges.Where(e => e.From == "test"), e => Assert.Equal("edge", e.Kind));
    }

    [Fact]
    public async Task Parallel_fan_out_and_fan_in_report_branch_paths_and_edges()
    {
        var report = await Sim.ReportAsync(Sim.PrepareExample("parallel-review.dot"), """{"nodes": {"perf": {"outcomes": {"success": 0.5, "fail": 0.5}}}}""", runs: 100);

        Assert.Equal(100, report.Outcomes["success"]);
        foreach (var branch in new[] { "security", "perf", "style" })
        {
            Assert.Equal(100, Sim.Visits(report, branch));
            Assert.Equal(100, report.Nodes[branch].Runs);
            Assert.Equal(100, Sim.EdgeCount(report, "reviews", branch));
            Assert.Equal(100, Sim.EdgeCount(report, branch, "gather"));
        }

        Assert.Equal("jump", report.Edges.Single(e => e.From == "reviews" && e.To == "gather").Kind);
        Assert.Equal(["start", "reviews", "security", "perf", "style", "gather", "summarize", "exit"], report.Longest.Path);
        Assert.InRange(report.Nodes["perf"].Outcomes["fail"], 30, 70);
    }

    [Fact]
    public async Task Retries_are_counted_per_node()
    {
        const string dot = """
            digraph R {
                start [shape=Mdiamond] exit [shape=Msquare]
                flaky [prompt="flaky", max_retries=2]
                lenient [prompt="lenient", max_retries=1, allow_partial=true]
                start -> flaky -> lenient -> exit
            }
            """;

        var report = await Sim.ReportAsync(dot, """{"nodes": {"flaky": {"script": ["retry", "retry", "success"]}, "lenient": {"outcomes": "retry"}}}""", runs: 10);
        var exhausted = await Sim.ReportAsync(dot, """{"nodes": {"flaky": {"outcomes": "retry"}}}""", runs: 10);

        Assert.Equal(10, report.Outcomes["success"]);
        Assert.Equal(20, report.Nodes["flaky"].Retries);
        Assert.Equal(10, report.Nodes["flaky"].Visits);
        Assert.Equal(10, report.Nodes["flaky"].Outcomes["success"]);
        Assert.Equal(10, report.Nodes["lenient"].Retries);
        Assert.Equal(10, report.Nodes["lenient"].Outcomes["partial_success"]);
        Assert.Equal(7, report.Steps.Max);
        Assert.Equal(10, exhausted.Outcomes["success"]);
        Assert.Equal(10, exhausted.Nodes["flaky"].Outcomes["fail"]);
        Assert.Equal(20, exhausted.Nodes["flaky"].Retries);
    }

    [Fact]
    public async Task Goal_gates_with_retry_targets_bounce_back()
    {
        const string dot = """
            digraph G {
                graph [retry_target="build"]
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build", goal_gate=true]
                start -> build -> exit
            }
            """;

        var trace = await Sim.TraceAsync(dot, """{"nodes": {"build": {"script": ["fail", "success"]}}}""");
        var report = await Sim.ReportAsync(dot, """{"nodes": {"build": {"script": ["fail", "success"]}}}""", runs: 5);

        Assert.Equal(["start", "build", "build", "exit"], trace.Path);
        Assert.Single(trace.Events.OfType<GoalGateUnsatisfied>());
        var bounce = report.Edges.Single(e => e.Kind == "goal_gate");
        Assert.Equal(("exit", "build", 5, 5), (bounce.From, bounce.To, bounce.Count, bounce.Runs));
    }

    [Fact]
    public async Task Report_statistics_are_consistent()
    {
        var report = await Sim.ReportAsync(Sim.PrepareExample("factory.dot"), FactoryScenario, runs: 300, seed: 5);
        var json = report.ToJson();

        Assert.Equal(300, report.Runs);
        Assert.Equal(300, report.Outcomes.Values.Sum());
        Assert.Equal(SimulationOutcomeKinds.All, json["outcomes"]!.AsObject().Select(kv => kv.Key));
        Assert.InRange(report.SuccessRate, report.SuccessInterval.Low, report.SuccessInterval.High);
        Assert.True(report.Steps.Min <= report.Steps.P50 && report.Steps.P50 <= report.Steps.P90 && report.Steps.P90 <= report.Steps.P95 && report.Steps.P95 <= report.Steps.Max);
        Assert.Equal(300, report.Steps.Histogram.Sum(b => b.Count));
        Assert.InRange(report.Steps.Histogram.Count, 1, 30);
        Assert.All(report.Steps.Histogram, b => Assert.Equal(0, b.Steps % report.Steps.BucketSize));
        Assert.Equal(new[] { "start", "exit" }, report.Nodes.Keys.Take(2));
        Assert.Equal(300, report.Nodes["start"].Visits);
        Assert.Equal(1.0, report.Nodes["start"].MeanVisits);
        Assert.True(report.Nodes["test"].MaxVisits >= 1);
        Assert.Equal(report.Edges.OrderByDescending(e => e.Count).Select(e => e.Count), report.Edges.Select(e => e.Count));
        Assert.Equal(300, report.Edges.Where(e => e.From == "start").Sum(e => e.Count));
        Assert.Equal(report.Longest.Steps, report.Steps.Max);
        Assert.True(report.Longest.Path.Count <= report.Longest.Steps);
        var samples = json["samples"]!.AsObject();
        Assert.Equal(["first_failure", "longest", "typical"], samples.Select(kv => kv.Key));
        Assert.Equal(["min", "mean", "p50", "p90", "p95", "max", "bucket_size", "histogram"], json["steps"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(2, json["success_ci"]!.AsArray().Count);
        Assert.Equal(0.0, Math.Round(report.SimulatedDuration.Mean, 3));
    }

    [Fact]
    public void Wilson_interval_matches_known_values()
    {
        var (low, high) = SimulationReport.WilsonInterval(470, 500);
        Assert.Equal(0.9156, Math.Round(low, 4));
        Assert.Equal(0.9577, Math.Round(high, 4));
        Assert.Equal((0.0, 0.0), SimulationReport.WilsonInterval(0, 0));
        Assert.Equal(1.0, SimulationReport.WilsonInterval(10, 10).High);
        Assert.Equal(0.0, SimulationReport.WilsonInterval(0, 10).Low);
        Assert.Equal(0.7225, Math.Round(SimulationReport.WilsonInterval(10, 10).Low, 4));
    }

    [Fact]
    public async Task Simulated_durations_are_accounted_without_waiting()
    {
        var stopwatch = Stopwatch.StartNew();
        var report = await Sim.ReportAsync(Sim.TestFixLoop, """{"step_delay_ms": 1000, "nodes": {"test": {"duration_ms": "1m"}}}""", runs: 50);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(61_000, report.SimulatedDuration.P50);
        Assert.Equal(61_000, report.SimulatedDuration.Max);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_up_front()
    {
        var graph = DotParser.Parse("digraph G { start [shape=Mdiamond]; a [prompt=\"a\"]; start -> a }");
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ValidationException>(() => MonteCarloSimulator.RunAsync(graph, SimulationScenario.Empty, cancellationToken: ct));
        await Assert.ThrowsAsync<ValidationException>(() => SimulationTrace.RunAsync(graph, SimulationScenario.Empty, 1, cancellationToken: ct));
        var valid = Sim.Prepare(Sim.TestFixLoop);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MonteCarloSimulator.RunAsync(valid, SimulationScenario.Empty, new MonteCarloOptions { Runs = 0 }, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MonteCarloSimulator.RunAsync(valid, SimulationScenario.Empty, new MonteCarloOptions { Runs = MonteCarloOptions.MaxRuns + 1 }, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MonteCarloSimulator.RunAsync(valid, SimulationScenario.Empty, new MonteCarloOptions { MaxSteps = MonteCarloOptions.MaxStepsCap + 1 }, ct));
    }

    [Fact]
    public async Task Cancellation_stops_the_simulation_and_scratch_directories_are_removed()
    {
        using var dir = new TempDir();
        var graph = Sim.PrepareExample("factory.dot");
        var scenario = Sim.Scenario(FactoryScenario);

        var report = await MonteCarloSimulator.RunAsync(graph, scenario, new MonteCarloOptions { Runs = 40, TempDirectory = dir.Path, Seed = 3 }, TestContext.Current.CancellationToken);
        Assert.Equal(40, report.Runs);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MonteCarloSimulator.RunAsync(graph, scenario, new MonteCarloOptions { Runs = 2000, TempDirectory = dir.Path }, cts.Token));
        using var late = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MonteCarloSimulator.RunAsync(graph, scenario, new MonteCarloOptions { Runs = 5000, MaxSteps = 5000, TempDirectory = dir.Path }, late.Token));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Two_hundred_factory_samples_finish_quickly()
    {
        var graph = Sim.PrepareExample("factory.dot");
        var stopwatch = Stopwatch.StartNew();

        var report = await Sim.ReportAsync(graph, FactoryScenario, runs: 200, seed: 42);

        stopwatch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"200 factory samples: {stopwatch.ElapsedMilliseconds} ms (report says {report.DurationMs} ms), mean steps {report.Steps.Mean:0.0}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"200 samples took {stopwatch.Elapsed}");
        Assert.Equal(200, report.Runs);
        Assert.True(report.Outcomes["success"] > 150, report.ToJson()["outcomes"]!.ToJsonString());
    }
}

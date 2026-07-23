using Reactor.Events;
using Reactor.Simulation;

namespace Reactor.Tests.Simulation;

/// <summary>Simulation results must not depend on how parallel branches are scheduled.</summary>
public sealed class ParallelDeterminismTests
{
    private const string FirstSuccess = """
        digraph FirstSuccess {
            start [shape=Mdiamond] exit [shape=Msquare]
            fork [shape=component, join_policy="first_success"]
            a [prompt="a"]
            a2 [prompt="a2"]
            b [prompt="b"]
            b2 [prompt="b2"]
            join [shape=tripleoctagon]
            start -> fork
            fork -> a -> a2 -> join
            fork -> b -> b2 -> join
            join -> exit
        }
        """;

    private const string Converging = """
        digraph Converge {
            start [shape=Mdiamond] exit [shape=Msquare]
            fork [shape=component]
            a [prompt="a"]
            b [prompt="b"]
            test [shape=parallelogram, tool_command="run tests"]
            fixit [prompt="fixit"]
            join [shape=tripleoctagon]
            start -> fork
            fork -> a -> test
            fork -> b -> test
            test -> fixit [condition="outcome=fail"]
            test -> join [condition="outcome=success"]
            fixit -> join
            join -> exit
        }
        """;

    private const string Nested = """
        digraph Nested {
            start [shape=Mdiamond] exit [shape=Msquare]
            outer [shape=component, join_policy="first_success"]
            quick [prompt="quick"]
            inner [shape=component]
            i1 [prompt="i1"]
            i2 [prompt="i2"]
            ijoin [shape=tripleoctagon]
            ojoin [shape=tripleoctagon]
            after [prompt="after"]
            start -> outer
            outer -> quick -> ojoin
            outer -> inner
            inner -> i1 -> ijoin
            inner -> i2 -> ijoin
            ijoin -> ojoin
            ojoin -> after -> exit
        }
        """;

    [Fact]
    public async Task First_success_picks_the_first_successful_branch_in_edge_order()
    {
        const string scenario = """{"nodes": {"a2": {"outcomes": {"success": 0.5, "fail": 0.5}}}}""";
        var graph = Sim.Prepare(FirstSuccess);
        var reports = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var json = (await Sim.ReportAsync(graph, scenario, runs: 200, seed: 7)).ToJson();
            json.Remove("duration_ms");
            reports.Add(json.ToJsonString());
        }

        Assert.Single(reports.Distinct(StringComparer.Ordinal));
        var report = await Sim.ReportAsync(graph, scenario, runs: 200, seed: 7);
        Assert.Equal(200, Sim.Visits(report, "a"));
        Assert.Equal(report.Nodes["a2"].Outcomes["fail"], Sim.Visits(report, "b"));
        Assert.InRange(Sim.Visits(report, "b"), 70, 130);

        var empty = await Sim.TraceAsync(FirstSuccess);
        Assert.Equal(["start", "fork", "a", "a2", "join", "exit"], empty.Path);
        foreach (var sample in new[] { report.Longest, report.Typical })
        {
            var trace = await SimulationTrace.RunAsync(graph, Sim.Scenario(scenario), sample.Seed, sample.RunIndex, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(sample.Path, trace.Path);
            Assert.Equal(sample.Steps, trace.Steps);
        }
    }

    [Fact]
    public async Task Nodes_shared_by_branches_get_their_draws_in_branch_order()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 10; i++)
        {
            var trace = await Sim.TraceAsync(Converging, """{"nodes": {"test": {"script": ["fail", "success"]}}}""");
            paths.Add(string.Join(",", trace.Path));
        }

        Assert.Equal("start,fork,a,test,fixit,b,test,join,exit", Assert.Single(paths));
    }

    [Fact]
    public async Task Nested_parallel_sections_expand_in_branch_order()
    {
        var trace = await Sim.TraceAsync(Nested, """{"nodes": {"quick": {"outcomes": "fail"}}}""");
        var report = await Sim.ReportAsync(Nested, """{"nodes": {"quick": {"outcomes": "fail"}}}""", runs: 30);
        var quickWins = await Sim.ReportAsync(Nested, runs: 30);

        Assert.Equal(["start", "outer", "quick", "inner", "i1", "i2", "ijoin", "ojoin", "after", "exit"], trace.Path);
        Assert.Equal(30, Sim.EdgeCount(report, "inner", "i1"));
        Assert.Equal(30, Sim.EdgeCount(report, "i2", "ijoin"));
        Assert.Equal(30, Sim.EdgeCount(report, "ojoin", "after"));
        Assert.Equal(30, Sim.EdgeCount(report, "after", "exit"));
        Assert.Equal(0, Sim.Visits(quickWins, "inner"));
        Assert.Equal(30, Sim.EdgeCount(quickWins, "ojoin", "after"));
        Assert.Equal(["start", "outer", "quick", "ojoin", "after", "exit"], quickWins.Typical.Path);
    }

    [Fact]
    public void Analysis_survives_a_nested_parallel_that_never_reports_completion()
    {
        var graph = Sim.Prepare(Nested);
        var sample = new SimulationSample(SimulationScenario.Empty, 1);
        sample.RecordParallel(new ParallelRecord("outer",
        [
            new ParallelBranchRecord("quick", "success", "ojoin", ["quick"]),
            new ParallelBranchRecord("inner", "skipped", null, []),
        ]));
        PipelineEvent[] events =
        [
            new StageStarted("start", "start", 1, "start", 1),
            new EdgeSelected("start", "outer", "weight", ""),
            new StageStarted("outer", "outer", 2, "parallel", 1),
            new ParallelStarted("outer", 2),
            new StageStarted("quick", "quick", 3, "codergen", 1),
            new StageStarted("inner", "inner", 4, "parallel", 1),
            new ParallelStarted("inner", 2),
            new StageCompleted("quick", 3, TimeSpan.Zero, "success", ""),
            new ParallelCompleted("outer", TimeSpan.Zero, 1, 0),
            new EdgeSelected("outer", "ojoin", "jump", ""),
            new StageStarted("ojoin", "ojoin", 5, "parallel.fan_in", 1),
            new EdgeSelected("ojoin", "after", "weight", ""),
            new StageStarted("after", "after", 6, "codergen", 1),
            new EdgeSelected("after", "exit", "weight", ""),
            new StageStarted("exit", "exit", 7, "exit", 1),
        ];

        var result = SampleRunner.Analyze(graph, sample, events, null, null, SampleDetail.Path);

        Assert.Equal(["start", "outer", "quick", "ojoin", "after", "exit"], result.Path);
        Assert.Equal("exit", result.LastNode);
        Assert.Contains(new EdgeKey("ojoin", "after", "edge", ""), result.Edges.Keys);
        Assert.Contains(new EdgeKey("quick", "ojoin", "edge", ""), result.Edges.Keys);
        Assert.Equal(7, result.Steps);
    }
}

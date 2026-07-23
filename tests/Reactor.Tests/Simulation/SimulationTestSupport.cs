using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Simulation;
using Reactor.Tests.Support;

namespace Reactor.Tests.Simulation;

/// <summary>Shared pipelines and helpers for simulation tests.</summary>
internal static class Sim
{
    /// <summary>implement → test (tool) → exit on success, → fix on failure; fix → test.</summary>
    public const string TestFixLoop = """
        digraph Loop {
            start [shape=Mdiamond] exit [shape=Msquare]
            implement [prompt="implement"]
            test [shape=parallelogram, tool_command="python -m pytest -q"]
            fix [prompt="fix"]
            start -> implement -> test
            test -> exit [condition="outcome=success"]
            test -> fix [condition="outcome=fail"]
            fix -> test
        }
        """;

    /// <summary>A review stage routing by preferred label: Approve → exit, Fix → fix → review.</summary>
    public const string LabeledReview = """
        digraph Review {
            start [shape=Mdiamond] exit [shape=Msquare]
            write [prompt="write"]
            review [prompt="review"]
            fix [prompt="fix"]
            start -> write -> review
            review -> exit [label="Approve"]
            review -> fix [label="Fix"]
            fix -> review
        }
        """;

    /// <summary>A human gate choosing between two stages that both lead to the exit.</summary>
    public const string ShipGate = """
        digraph Ship {
            start [shape=Mdiamond] exit [shape=Msquare]
            gate [shape=hexagon, label="Ship it?"]
            ship [prompt="ship"]
            rework [prompt="rework"]
            start -> gate
            gate -> ship [label="[A] Approve"]
            gate -> rework [label="[R] Rework"]
            ship -> exit
            rework -> exit
        }
        """;

    public static Graph Prepare(string dot)
    {
        var prepared = TestPipelines.Runner().Prepare(DotParser.Parse(dot));
        Assert.False(prepared.HasErrors, string.Join("\n", prepared.Errors));
        return prepared.Graph;
    }

    public static Graph PrepareExample(string name)
    {
        var prepared = TestPipelines.Runner().Prepare(DotParser.ParseFile(Path.Combine(ExampleTests.ExamplesDirectory(), name)));
        Assert.False(prepared.HasErrors, string.Join("\n", prepared.Errors));
        return prepared.Graph;
    }

    public static SimulationScenario Scenario(string json) => SimulationScenario.FromJson(JsonNode.Parse(json)!.AsObject());

    public static Task<SimulationTrace> TraceAsync(string dot, string scenario = "{}", long seed = 1, int runIndex = 0, int maxSteps = 200) =>
        SimulationTrace.RunAsync(Prepare(dot), Scenario(scenario), seed, runIndex, new MonteCarloOptions { MaxSteps = maxSteps }, TestContext.Current.CancellationToken);

    public static Task<SimulationReport> ReportAsync(string dot, string scenario = "{}", int runs = 100, long? seed = 1, int maxSteps = 200) =>
        ReportAsync(Prepare(dot), scenario, runs, seed, maxSteps);

    public static Task<SimulationReport> ReportAsync(Graph graph, string scenario = "{}", int runs = 100, long? seed = 1, int maxSteps = 200) =>
        MonteCarloSimulator.RunAsync(graph, Scenario(scenario), new MonteCarloOptions { Runs = runs, MaxSteps = maxSteps, Seed = seed }, TestContext.Current.CancellationToken);

    /// <summary>Visits of a node over all runs (0 when never visited).</summary>
    public static int Visits(SimulationReport report, string node) => report.Nodes.TryGetValue(node, out var stats) ? stats.Visits : 0;

    public static int EdgeCount(SimulationReport report, string from, string to) =>
        report.Edges.Where(e => e.From == from && e.To == to).Sum(e => e.Count);
}

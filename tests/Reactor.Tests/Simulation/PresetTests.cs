using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Simulation;

namespace Reactor.Tests.Simulation;

public sealed class PresetTests
{
    [Fact]
    public void The_studio_bundles_exactly_these_presets()
    {
        // The Studio's wind tunnel imports this file; regenerate it from SimulationPreset.CatalogJson() when presets change.
        var path = Path.Combine(Path.GetDirectoryName(ExampleTests.ExamplesDirectory())!, "src", "Reactor.Studio", "src", "modes", "tunnel", "presets.json");
        var bundled = JsonNode.Parse(File.ReadAllText(path));

        Assert.True(JsonNode.DeepEquals(SimulationPreset.CatalogJson(), bundled),
            $"{path} is out of date. Expected:\n{SimulationPreset.CatalogJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}");
    }

    [Fact]
    public void Presets_build_the_documented_scenarios()
    {
        var realistic = SimulationPreset.Find(" Realistic ")!.ToScenario();

        Assert.Equal(["happy", "realistic", "stress", "chaos"], SimulationPreset.All.Select(p => p.Name));
        Assert.Null(SimulationPreset.Find("calm"));
        Assert.Equal(0.9, realistic.Defaults["codergen"].Outcomes![StageStatus.Success]);
        Assert.Equal(0.03, realistic.Defaults["codergen"].Outcomes![StageStatus.Retry]);
        Assert.Equal(0.07, realistic.Defaults["codergen"].Outcomes![StageStatus.Fail]);
        Assert.Equal(0.25, realistic.Defaults["tool"].Outcomes![StageStatus.Fail]);
        Assert.Equal(0.1, realistic.Defaults["stack.manager_loop"].Outcomes![StageStatus.Fail]);
        Assert.Equal(600_000, realistic.DurationPriors["codergen"]);
        Assert.Equal(60_000, realistic.DurationPriors["tool"]);
        Assert.Equal(0.3, SimulationPreset.Find("chaos")!.ToScenario().Defaults["codergen"].Outcomes![StageStatus.Fail]);
        Assert.Equal(1, SimulationPreset.Find("happy")!.ToScenario().Defaults["tool"].Outcomes![StageStatus.Success]);
        Assert.Empty(SimulationPreset.Find("happy")!.ToScenario().Check(Sim.Prepare(Sim.TestFixLoop)));
    }

    [Fact]
    public void Presets_choose_labeled_routes_evenly()
    {
        var graph = Sim.Prepare(Sim.LabeledReview);

        var scenario = SimulationPreset.Find("realistic")!.ToScenario(graph);

        Assert.Equal(new Dictionary<string, double> { ["Approve"] = 0.5, ["Fix"] = 0.5 }, scenario.Nodes["review"].Labels);
        Assert.Equal(["review"], scenario.Nodes.Keys);
        Assert.Empty(scenario.Check(graph));
    }

    [Fact]
    public void Scenarios_can_start_from_a_preset()
    {
        var graph = Sim.Prepare(Sim.LabeledReview);

        Assert.True(SimulationScenario.TryResolve(JsonNode.Parse("""
            {"preset": "stress", "seed": 9, "duration_priors": {"tool": "5m"},
             "defaults": {"codergen": {"cost": 0.5}},
             "nodes": {"write": {"duration_ms": "2h"}, "fix": {"labels": {"x": 1}}}}
            """), graph, out var scenario, out var errors), string.Join("; ", errors));

        Assert.Equal(9, scenario.Seed);
        Assert.Equal(0.75, scenario.Defaults["codergen"].Outcomes![StageStatus.Success]);
        Assert.Equal(0.5, scenario.Defaults["codergen"].CostUsd);
        Assert.Equal(0.5, scenario.Defaults["tool"].Outcomes![StageStatus.Fail]);
        Assert.Equal(300_000, scenario.DurationPriors["tool"]);
        Assert.Equal(600_000, scenario.DurationPriors["codergen"]);
        Assert.Equal(7_200_000, scenario.Nodes["write"].DurationMs);
        Assert.Equal(0.5, scenario.Nodes["review"].Labels!["Fix"]);
        Assert.Equal(1, scenario.Nodes["fix"].Labels!["x"]);

        // Without a graph (FromJson), a preset still applies; even route labels need the graph.
        var parsed = Sim.Scenario("""{"preset": "happy", "nodes": {"review": {"outcomes": "fail"}}}""");
        Assert.Equal(1, parsed.Defaults["codergen"].Outcomes![StageStatus.Success]);
        Assert.Equal(1, parsed.Nodes["review"].Outcomes![StageStatus.Fail]);
        Assert.Null(parsed.Nodes["review"].Labels);
    }

    [Fact]
    public void Resolving_a_scenario_accepts_preset_names_and_reports_unknown_ones()
    {
        var graph = Sim.Prepare(Sim.LabeledReview);

        Assert.True(SimulationScenario.TryResolve(JsonValue.Create("Chaos"), graph, out var chaos, out _));
        Assert.Equal(0.55, chaos.Defaults["codergen"].Outcomes![StageStatus.Success]);
        Assert.Equal(0.5, chaos.Nodes["review"].Labels!["Approve"]);

        Assert.True(SimulationScenario.TryResolve(null, graph, out var empty, out _));
        Assert.Same(SimulationScenario.Empty, empty);
        Assert.True(SimulationScenario.TryResolve(JsonNode.Parse("""{"nodes": {"review": {"outcomes": "fail"}}}"""), graph, out var plain, out _));
        Assert.Null(plain.Nodes["review"].Labels);

        Assert.False(SimulationScenario.TryResolve(JsonValue.Create("realistc"), graph, out _, out var unknown));
        Assert.StartsWith("unknown preset 'realistc' (expected happy, realistic, stress, chaos)", Assert.Single(unknown).Message, StringComparison.Ordinal);
        Assert.False(SimulationScenario.TryResolve(JsonNode.Parse("""{"preset": "calm"}"""), graph, out _, out var badPreset));
        Assert.Equal("preset", Assert.Single(badPreset).Path);
        Assert.False(SimulationScenario.TryResolve(JsonNode.Parse("[1]"), graph, out _, out _));
        Assert.False(SimulationScenario.TryResolve(JsonValue.Create(3), graph, out _, out _));
    }
}

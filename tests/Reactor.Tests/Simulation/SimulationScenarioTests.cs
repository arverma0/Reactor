using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Simulation;

namespace Reactor.Tests.Simulation;

public sealed class SimulationScenarioTests
{
    [Fact]
    public void Parses_the_documented_scenario_format()
    {
        var scenario = Sim.Scenario("""
            {
              "seed": 123,
              "step_delay_ms": 0,
              "defaults": {
                "codergen": { "outcomes": { "success": 0.9, "fail": 0.1 } },
                "tool":     { "outcomes": { "success": 0.7, "fail": 0.3 } },
                "stack.manager_loop": { "outcomes": { "success": 1 } }
              },
              "nodes": {
                "test": {
                  "outcomes": { "success": 0.6, "fail": 0.3, "retry": 0.1, "partial_success": 0 },
                  "script": ["fail", "fail", "success"],
                  "labels": { "Approve": 0.8, "Fix": 0.2 },
                  "choices": { "[A] Approve": 0.7, "[R] Revise": 0.3 },
                  "context_updates": { "success": { "tests_passed": true }, "fail": { "tests_passed": false } },
                  "duration_ms": 1200
                }
              }
            }
            """);

        Assert.Equal(123, scenario.Seed);
        Assert.Equal(0, scenario.StepDelayMs);
        Assert.Equal(0.3, scenario.Defaults["tool"].Outcomes![StageStatus.Fail]);
        var test = scenario.Nodes["test"];
        Assert.Equal(0.1, test.Outcomes![StageStatus.Retry]);
        Assert.Equal(0, test.Outcomes[StageStatus.PartialSuccess]);
        Assert.Equal([StageStatus.Fail, StageStatus.Fail, StageStatus.Success], test.Script!.Select(s => s.Status));
        Assert.Equal(0.8, test.Labels!["Approve"]);
        Assert.Equal(0.3, test.Choices!["[R] Revise"]);
        Assert.Equal(true, test.ContextUpdates!["success"]["tests_passed"]);
        Assert.Equal(1200, test.DurationMs);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        const string source = """
            {"seed": 7, "step_delay_ms": 250,
             "defaults": {"tool": {"outcomes": {"success": 0.6, "fail": 0.4}, "duration_ms": 30}},
             "nodes": {"review": {"script": ["fail", "success:Approve"], "labels": {"Approve": 1},
                                  "context_updates": {"*": {"reviewed": true}, "fail": {"notes": ["a", 1]}}},
                       "gate": {"choices": {"A": 2, "rework": 1}}}}
            """;
        var scenario = Sim.Scenario(source);
        var json = scenario.ToJson();
        var reparsed = SimulationScenario.FromJson(json);

        Assert.Equal(json.ToJsonString(), reparsed.ToJson().ToJsonString());
        Assert.Equal("success:Approve", json["nodes"]!["review"]!["script"]![1]!.GetValue<string>());
        Assert.True(json["nodes"]!["review"]!["context_updates"]!["*"]!["reviewed"]!.GetValue<bool>());
        Assert.Equal(1, json["nodes"]!["review"]!["context_updates"]!["fail"]!["notes"]![1]!.GetValue<long>());
        Assert.Equal(30, json["defaults"]!["tool"]!["duration_ms"]!.GetValue<int>());
    }

    [Fact]
    public void Parsing_is_lenient_about_spelling_numbers_and_shorthands()
    {
        var scenario = Sim.Scenario("""
            {"seed": "42", "stepDelayMs": "1.5s", "name": "ignored metadata",
             "nodes": {"a": {"outcomes": "fail", "script": "partial:Approve", "durationMs": "250ms"},
                       "b": {"Outcomes": {"PASSED": "3", "Failed": 1}, "script": [{"outcome": "retry"}, {"status": "success", "label": "Ship"}]}}}
            """);

        Assert.Equal(42, scenario.Seed);
        Assert.Equal(1500, scenario.StepDelayMs);
        Assert.Equal(1, scenario.Nodes["a"].Outcomes![StageStatus.Fail]);
        Assert.Equal(new SimulationScriptStep(StageStatus.PartialSuccess, "Approve"), scenario.Nodes["a"].Script!.Single());
        Assert.Equal(250, scenario.Nodes["a"].DurationMs);
        Assert.Equal(3, scenario.Nodes["b"].Outcomes![StageStatus.Success]);
        Assert.Equal([new SimulationScriptStep(StageStatus.Retry), new SimulationScriptStep(StageStatus.Success, "Ship")], scenario.Nodes["b"].Script!);
        Assert.Empty(SimulationScenario.Empty.Nodes);
        Assert.Empty(SimulationScenario.FromJson(null).Nodes);
    }

    [Fact]
    public void Invalid_scenarios_report_every_error_with_its_path()
    {
        var ok = SimulationScenario.TryFromJson(JsonNode.Parse("""
            {"seed": 1.5, "step_delay_ms": -1,
             "defaults": [],
             "nodes": {"test": {"outcomes": {"sucess": 1, "fail": -2}, "script": ["fail", "maybe"], "labels": {"A": 0},
                                "context_updates": {"done": {}}, "duration": 5, "duration_ms": "soon"},
                       "other": "fail"}}
            """)!.AsObject(), out _, out var errors);

        Assert.False(ok);
        var messages = errors.Select(e => e.ToString()).ToList();
        Assert.Contains(messages, m => m.StartsWith("seed:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("step_delay_ms:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("defaults:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.test.outcomes.sucess: unknown status 'sucess'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.test.outcomes.fail: weight must be a non-negative number", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.test.script[1]: invalid script entry", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.test.labels: weights must not all be zero", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.test.context_updates.done: unknown status", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.test.duration: unknown field", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.test.duration_ms:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.other: must be an object", StringComparison.Ordinal));
        Assert.All(errors.Where(e => e.Path.StartsWith("nodes.test", StringComparison.Ordinal)), e => Assert.Equal("test", e.NodeId));

        var exception = Assert.Throws<SimulationScenarioException>(() => SimulationScenario.FromJson(JsonNode.Parse("""{"nodes": {"a": {"outcomes": {"x": 1}}}}""")!.AsObject()));
        Assert.Single(exception.Errors);
        Assert.Contains("unknown status 'x'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_warns_about_entries_that_cannot_apply_to_the_pipeline()
    {
        var graph = Sim.Prepare(Sim.ShipGate);
        var scenario = Sim.Scenario("""
            {"defaults": {"codegen": {"outcomes": {"fail": 1}}, "parallel": {"outcomes": {"fail": 1}}},
             "nodes": {"missing": {}, "start": {"outcomes": {"fail": 1}},
                       "gate": {"choices": {"Approve": 1, "Ship it": 1}, "labels": {"x": 1}},
                       "ship": {"labels": {"Nope": 1}, "choices": {"A": 1}}}}
            """);

        var warnings = scenario.Check(graph);

        Assert.Contains(warnings, w => w.StartsWith("defaults.codegen: unknown handler type", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("defaults.parallel:", StringComparison.Ordinal) && w.Contains("no effect", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("nodes.missing: the pipeline has no node", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("nodes.start:", StringComparison.Ordinal) && w.Contains("no effect", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("nodes.gate.choices: 'Ship it' matches no option", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("'Approve' matches no option", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("nodes.gate.labels:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("nodes.ship.labels: 'Nope'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("nodes.ship.choices:", StringComparison.Ordinal));
        Assert.Empty(Sim.Scenario("""{"defaults": {"custom.review": {}}}""").Check(graph, ["custom.review"]));
    }

    [Fact]
    public void Draws_are_a_pure_function_of_their_inputs_and_roughly_uniform()
    {
        var first = SimulationSample.Draw(123, 4, "test", 2, "outcome");
        Assert.Equal(first, SimulationSample.Draw(123, 4, "test", 2, "outcome"));
        Assert.NotEqual(first, SimulationSample.Draw(123, 4, "test", 2, "label"));
        Assert.NotEqual(first, SimulationSample.Draw(123, 5, "test", 2, "outcome"));
        Assert.NotEqual(first, SimulationSample.Draw(124, 4, "test", 2, "outcome"));
        Assert.NotEqual(first, SimulationSample.Draw(123, 4, "test", 3, "outcome"));

        var values = Enumerable.Range(0, 20_000).Select(i => SimulationSample.Draw(9, i, "node", 1, "outcome")).ToList();
        Assert.All(values, v => Assert.InRange(v, 0.0, 0.9999999999));
        Assert.InRange(values.Average(), 0.49, 0.51);
        var buckets = values.GroupBy(v => (int)(v * 10)).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(10, buckets.Count);
        Assert.All(buckets.Values, count => Assert.InRange(count, 1800, 2200));
    }
}

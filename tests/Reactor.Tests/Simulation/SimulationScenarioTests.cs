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
    public void Parses_metrics_cost_and_duration_priors_and_round_trips_them()
    {
        var scenario = Sim.Scenario("""
            {"duration_priors": {"codergen": "10m", "tool": 60000},
             "nodes": {"review": {"cost": "$0.25", "metrics": {
                "score": {"values": {"5": 0.3, "3": 0.2, "4": 0.5}},
                "findings": {"min": 0, "max": 3},
                "coverage": {"min": 0.5, "max": 1},
                "ratio": {"min": 0, "max": 1, "integer": false},
                "bugs": 2,
                "passed": true,
                "levels": {"values": [1, 2, 2]},
                "qa_failed": {"success": 0, "*": {"min": 1, "max": 5}}}}}}
            """);

        Assert.Equal(600_000, scenario.DurationPriors["codergen"]);
        Assert.Equal(60_000, scenario.DurationPriors["tool"]);
        var review = scenario.Nodes["review"];
        Assert.Equal(0.25, review.CostUsd);
        var metrics = review.Metrics!;
        var score = Assert.IsType<SimulationMetric.Weighted>(metrics["score"]);
        Assert.Equal([3.0, 4.0, 5.0], score.Values.Select(v => v.Key));
        Assert.Equal(new SimulationMetric.Uniform(0, 3, Integer: true), metrics["findings"]);
        Assert.Equal(new SimulationMetric.Uniform(0.5, 1, Integer: false), metrics["coverage"]);
        Assert.Equal(new SimulationMetric.Uniform(0, 1, Integer: false), metrics["ratio"]);
        Assert.Equal(new SimulationMetric.Constant(2), metrics["bugs"]);
        Assert.Equal(new SimulationMetric.Constant(1), metrics["passed"]);
        Assert.Equal([KeyValuePair.Create(1.0, 1.0), KeyValuePair.Create(2.0, 2.0)], Assert.IsType<SimulationMetric.Weighted>(metrics["levels"]).Values);
        var qa = Assert.IsType<SimulationMetric.PerStatus>(metrics["qa_failed"]);
        Assert.Equal(new SimulationMetric.Constant(0), qa.ByStatus["success"]);
        Assert.Equal(new SimulationMetric.Uniform(1, 5, Integer: true), qa.ByStatus["*"]);

        var json = scenario.ToJson();
        Assert.Equal(json.ToJsonString(), SimulationScenario.FromJson(json).ToJson().ToJsonString());
        Assert.False(json["nodes"]!["review"]!["metrics"]!["ratio"]!["integer"]!.GetValue<bool>());
        Assert.Null(json["nodes"]!["review"]!["metrics"]!["coverage"]!["integer"]);
        Assert.Equal(0.25, json["nodes"]!["review"]!["cost"]!.GetValue<double>());
        Assert.Equal(600_000, json["duration_priors"]!["codergen"]!.GetValue<int>());
    }

    [Fact]
    public void Metric_draws_stay_within_their_distribution()
    {
        var dice = new SimulationMetric.Uniform(1, 6, Integer: true);
        var range = new SimulationMetric.Uniform(0.5, 1.5, Integer: false);
        var weighted = new SimulationMetric.Weighted([KeyValuePair.Create(3.0, 1.0), KeyValuePair.Create(5.0, 3.0)]);
        var perStatus = new SimulationMetric.PerStatus(new Dictionary<string, SimulationMetric> { ["fail"] = new SimulationMetric.Constant(7) });

        Assert.Equal(1, dice.Draw(StageStatus.Success, 0));
        Assert.Equal(6, dice.Draw(StageStatus.Success, 0.9999999));
        Assert.Equal(4, dice.Draw(StageStatus.Success, 0.5));
        Assert.Equal(1.0, range.Draw(StageStatus.Success, 0.5));
        Assert.Equal(3, weighted.Draw(StageStatus.Success, 0.2));
        Assert.Equal(5, weighted.Draw(StageStatus.Success, 0.3));
        Assert.Equal(7, perStatus.Draw(StageStatus.Fail, 0.1));
        Assert.Null(perStatus.Draw(StageStatus.Success, 0.1));
    }

    [Fact]
    public void Invalid_metrics_costs_and_priors_report_errors_with_their_paths()
    {
        var ok = SimulationScenario.TryFromJson(JsonNode.Parse("""
            {"duration_priors": {"codergen": "25h", "tool": "soon"},
             "nodes": {"a": {"cost": -1,
                             "metrics": {"1bad": 1, "x": {"min": 5, "max": 1}, "y": {"values": {"high": 1}}, "z": {"min": 0.5, "max": 2, "integer": true},
                                         "w": {"success": {"fail": 1}}, "v": "lots", "u": {"min": 1, "values": [1]}, "t": {"sucess": 1}}},
                       "b": {"cost": 20000, "metrics": []}}}
            """)!.AsObject(), out _, out var errors);

        Assert.False(ok);
        var messages = errors.Select(e => e.ToString()).ToList();
        Assert.Contains(messages, m => m.StartsWith("duration_priors.codergen: must be a duration", StringComparison.Ordinal) && m.Contains("24h", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("duration_priors.tool: must be a duration", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.cost: must be a cost in USD", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.b.cost: must be a cost in USD", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.1bad: is not a metric name", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.x: \"min\" must not be greater than \"max\"", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.y.values.high: the key must be a number", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.z: a whole-number range needs whole-number bounds", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.w.success: per-status entries must be", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.v: must be a number,", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.u: \"values\" cannot be combined", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.a.metrics.t.sucess: unknown status 'sucess'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("nodes.b.metrics: must be an object of metric names", StringComparison.Ordinal));
        Assert.All(errors.Where(e => e.Path.StartsWith("nodes.a", StringComparison.Ordinal)), e => Assert.Equal("a", e.NodeId));
    }

    [Fact]
    public void Check_warns_about_durations_beyond_timeouts_metrics_on_gates_and_unknown_priors()
    {
        var graph = Sim.Prepare("""
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build", timeout="1h"]
                plan [prompt="plan", timeout="2h"]
                gate [shape=hexagon, label="Ship?"]
                start -> plan -> build -> gate
                gate -> exit [label="[S] Ship"]
            }
            """);
        var scenario = Sim.Scenario("""
            {"duration_priors": {"codergen": "3h", "codegen": "1m", "exit": "1m"},
             "defaults": {"codergen": {"duration_ms": "3h"}, "wait.human": {"metrics": {"x": 1}}},
             "nodes": {"build": {"duration_ms": "2h"}, "gate": {"metrics": {"y": 1}}}}
            """);

        var warnings = scenario.Check(graph);

        Assert.Contains("nodes.build.duration_ms: 2h exceeds the timeout of 'build' (1h), so every execution of 'build' times out.", warnings);
        Assert.Contains("defaults.codergen.duration_ms: 3h exceeds the timeout of 'plan' (2h), so every execution of 'plan' times out.", warnings);
        Assert.Contains(warnings, w => w.StartsWith("duration_priors.codegen: unknown handler type", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("duration_priors.exit:", StringComparison.Ordinal) && w.Contains("no effect", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("defaults.wait.human.metrics: human gates report no metrics", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("nodes.gate.metrics: human gates report no metrics", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.StartsWith("duration_priors.codergen", StringComparison.Ordinal));
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

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Server.Tests;

public sealed class SimulationApiTests
{
    private const string Loop = """
        digraph Loop {
            graph [goal="Ship a feature"]
            start [shape=Mdiamond] exit [shape=Msquare]
            implement [prompt="Implement $goal"]
            test [shape=parallelogram, tool_command="python -m pytest -q"]
            fix [prompt="Fix"]
            start -> implement -> test
            test -> exit [condition="outcome=success"]
            test -> fix [condition="outcome=fail"]
            fix -> test
        }
        """;

    private const string Walkthrough = """
        digraph Walk {
            start [shape=Mdiamond] exit [shape=Msquare]
            implement [prompt="Implement"]
            fix [prompt="Fix"]
            review [shape=hexagon, label="Ship it?"]
            start -> implement
            implement -> review [condition="outcome=success"]
            implement -> fix [condition="outcome=fail"]
            fix -> implement
            review -> exit [label="[A] Approve"]
            review -> fix [label="[R] Revise"]
        }
        """;

    [Fact]
    public async Task Simulate_returns_a_monte_carlo_report()
    {
        await using var server = await ServerHarness.StartAsync();
        var response = await server.Client.PostAsJsonAsync("/studio/api/simulate", new
        {
            dot = Loop,
            runs = 300,
            max_steps = 60,
            scenario = JsonNode.Parse("""{"seed": 123, "defaults": {"tool": {"outcomes": {"success": 0.6, "fail": 0.4}}}}"""),
        });
        var report = await ServerHarness.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode, report.ToJsonString());
        Assert.Equal(300, report["runs"]!.GetValue<int>());
        Assert.Equal(123, report["seed"]!.GetValue<long>());
        Assert.Equal(60, report["max_steps"]!.GetValue<int>());
        Assert.True(report["duration_ms"]!.GetValue<long>() >= 0);
        Assert.Equal(["success", "fail", "step_limit", "goal_gate", "dead_end", "error"], report["outcomes"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(300, report["outcomes"]!.AsObject().Sum(kv => kv.Value!.GetValue<int>()));
        Assert.InRange(report["success_rate"]!.GetValue<double>(), 0.9, 1.0);
        Assert.Equal(2, report["success_ci"]!.AsArray().Count);
        var steps = report["steps"]!;
        Assert.Equal(300, steps["histogram"]!.AsArray().Sum(b => b!["count"]!.GetValue<int>()));
        Assert.True(steps["p50"]!.GetValue<int>() <= steps["p95"]!.GetValue<int>());
        var test = report["nodes"]!["test"]!;
        Assert.Equal(300, test["runs"]!.GetValue<int>());
        Assert.True(test["visits"]!.GetValue<int>() > 300);
        Assert.True(test["outcomes"]!["fail"]!.GetValue<int>() > 0);
        var edge = report["edges"]!.AsArray().Single(e => e!["from"]!.GetValue<string>() == "test" && e["to"]!.GetValue<string>() == "fix")!;
        Assert.Equal("edge", edge["kind"]!.GetValue<string>());
        Assert.True(edge["count"]!.GetValue<int>() >= edge["runs"]!.GetValue<int>());
        Assert.Equal(["first_failure", "longest", "typical"], report["samples"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("start", report["samples"]!["typical"]!["path"]![0]!.GetValue<string>());
        Assert.NotNull(report["failures"]!.AsArray());
        Assert.Empty(report["warnings"]!.AsArray());
        Assert.NotNull(report["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task Simulate_reports_step_limits_and_raw_dot_bodies_use_the_empty_scenario()
    {
        await using var server = await ServerHarness.StartAsync();
        var looping = await server.Client.PostAsJsonAsync("/studio/api/simulate", new
        {
            dot = Loop,
            runs = 20,
            max_steps = 30,
            scenario = JsonNode.Parse("""{"nodes": {"test": {"outcomes": {"fail": 1}}}}"""),
        });
        var report = await ServerHarness.ReadJsonAsync(looping);
        Assert.Equal(20, report["outcomes"]!["step_limit"]!.GetValue<int>());
        var failure = report["failures"]!.AsArray().Single()!;
        Assert.Equal("step_limit", failure["kind"]!.GetValue<string>());
        Assert.Equal("Maximum step count (30) exceeded.", failure["reason"]!.GetValue<string>());
        Assert.Equal(20, failure["count"]!.GetValue<int>());
        Assert.Equal(0, failure["sample"]!["run_index"]!.GetValue<int>());
        Assert.Equal("fix", failure["last_node"]!.GetValue<string>());
        Assert.Equal("step_limit", report["samples"]!["first_failure"]!["kind"]!.GetValue<string>());

        var raw = await server.Client.PostAsync("/studio/api/simulate", new StringContent(Loop, Encoding.UTF8, "text/vnd.graphviz"));
        var rawReport = await ServerHarness.ReadJsonAsync(raw);
        Assert.True(raw.IsSuccessStatusCode, rawReport.ToJsonString());
        Assert.Equal(500, rawReport["runs"]!.GetValue<int>());
        Assert.Equal(500, rawReport["outcomes"]!["success"]!.GetValue<int>());
        Assert.Equal(200, rawReport["max_steps"]!.GetValue<int>());
    }

    [Fact]
    public async Task Simulate_rejects_invalid_input_with_diagnostics()
    {
        await using var server = await ServerHarness.StartAsync();

        var parse = await PostAsync(server, "/studio/api/simulate", new { dot = "digraph { a -> }" });
        Assert.Equal(HttpStatusCode.BadRequest, parse.Status);
        Assert.Contains("line", parse.Body["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(parse.Body["diagnostics"]!.AsArray());

        var invalid = await PostAsync(server, "/studio/api/simulate", new { dot = "digraph G { a -> b }" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        Assert.Contains(invalid.Body["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "start_node" && d["severity"]!.GetValue<string>() == "error");

        var scenario = await PostAsync(server, "/studio/api/simulate", new
        {
            dot = Loop,
            scenario = JsonNode.Parse("""{"nodes": {"test": {"outcomes": {"sucess": 1}, "script": ["maybe"]}}}"""),
        });
        Assert.Equal(HttpStatusCode.BadRequest, scenario.Status);
        Assert.Equal("The simulation scenario is invalid.", scenario.Body["error"]!.GetValue<string>());
        var diagnostics = scenario.Body["diagnostics"]!.AsArray();
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("simulation_scenario", d!["rule"]!.GetValue<string>());
            Assert.Equal("error", d["severity"]!.GetValue<string>());
            Assert.Equal("test", d["node_id"]!.GetValue<string>());
        });
        Assert.StartsWith("nodes.test.outcomes.sucess: unknown status", diagnostics[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        foreach (var body in new object[]
        {
            new { dot = Loop, runs = 0 },
            new { dot = Loop, runs = 5001 },
            new { dot = Loop, max_steps = "lots" },
            new { dot = Loop, seed = 1.5 },
            new { dot = Loop, scenario = "fail" },
            new { scenario = new { } },
        })
        {
            var response = await PostAsync(server, "/studio/api/simulate", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.Status);
            Assert.False(string.IsNullOrEmpty(response.Body["error"]?.GetValue<string>()), response.Body.ToJsonString());
        }
    }

    [Fact]
    public async Task Trace_replays_a_report_sample_with_its_events()
    {
        await using var server = await ServerHarness.StartAsync();
        var scenario = JsonNode.Parse("""{"seed": 77, "defaults": {"tool": {"outcomes": {"success": 0.5, "fail": 0.5}}}}""");
        var report = await ServerHarness.ReadJsonAsync(await server.Client.PostAsJsonAsync("/studio/api/simulate", new { dot = Loop, runs = 50, scenario }));
        var longest = report["samples"]!["longest"]!;

        var response = await server.Client.PostAsJsonAsync("/studio/api/simulate/trace", new
        {
            dot = Loop,
            scenario = JsonNode.Parse(scenario!.ToJsonString()),
            seed = longest["seed"]!.GetValue<long>(),
            run_index = longest["run_index"]!.GetValue<int>(),
        });
        var trace = await ServerHarness.ReadJsonAsync(response);

        Assert.True(response.IsSuccessStatusCode, trace.ToJsonString());
        Assert.Equal(77, trace["seed"]!.GetValue<long>());
        Assert.Equal(longest["run_index"]!.GetValue<int>(), trace["run_index"]!.GetValue<int>());
        Assert.Equal("success", trace["status"]!.GetValue<string>());
        Assert.Equal("success", trace["kind"]!.GetValue<string>());
        Assert.Equal("", trace["failure_reason"]!.GetValue<string>());
        Assert.Equal(longest["steps"]!.GetValue<int>(), trace["steps"]!.GetValue<int>());
        Assert.Equal(longest["path"]!.AsArray().Select(p => p!.GetValue<string>()), trace["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        var events = trace["events"]!.AsArray();
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e!["seq"]!.GetValue<int>()));
        Assert.Equal("PipelineStarted", events[0]!["type"]!.GetValue<string>());
        Assert.Equal("PipelineCompleted", events[^1]!["type"]!.GetValue<string>());
        Assert.Contains(events, e => e!["type"]!.GetValue<string>() == "StageLog" && e["message"]!.GetValue<string>().StartsWith("[Simulated]", StringComparison.Ordinal));
        Assert.Equal(trace["steps"]!.GetValue<int>(), events.Count(e => e!["type"]!.GetValue<string>() == "StageStarted"));

        var defaults = await ServerHarness.ReadJsonAsync(await server.Client.PostAsJsonAsync("/studio/api/simulate/trace", new { dot = Loop }));
        Assert.Equal(0, defaults["run_index"]!.GetValue<int>());
        Assert.Equal(["start", "implement", "test", "exit"], defaults["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(defaults["seed"]!.GetValue<long>() > 0);

        var bad = await PostAsync(server, "/studio/api/simulate/trace", new { dot = Loop, run_index = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
    }

    [Fact]
    public async Task Info_advertises_the_simulation_feature()
    {
        await using var server = await ServerHarness.StartAsync();
        var info = await server.GetJsonAsync("/studio/api/info");
        Assert.Contains("simulation", info["features"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public async Task Stochastic_simulated_runs_stream_events_and_never_run_real_handlers()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions
        {
            WorkingDirectory = dir,
            ConfigureRunner = runner => runner.RegisterHandler("custom.audit", new ThrowingHandler()),
        });
        var marker = Path.Combine(server.Dir.Path, "marker.txt").Replace('\\', '/');
        var dot = Loop
            .Replace("python -m pytest -q", $"echo hacked> \\\"{marker}\\\"", StringComparison.Ordinal)
            .Replace("start -> implement -> test", "audit [type=\"custom.audit\", prompt=\"audit\"]\n start -> audit -> implement -> test", StringComparison.Ordinal);

        var created = await StartSimulationAsync(server, dot, new { mode = "stochastic", scenario = new { step_delay_ms = 0, nodes = new { test = new { script = new[] { "fail", "success" } } } } });
        var id = created["id"]!.GetValue<string>();
        Assert.True(created["simulated"]!.GetValue<bool>());
        Assert.Equal("stochastic", created["simulation_mode"]!.GetValue<string>());

        var frames = await ReadEventStreamAsync(server, id);
        Assert.Equal("PipelineStarted", frames[0].Event);
        Assert.Equal("end", frames[^1].Event);
        Assert.Contains(frames, f => f.Event == "StageStarted" && f.Data["node_id"]?.GetValue<string>() == "audit" && f.Data["handler_type"]?.GetValue<string>() == "custom.audit");
        Assert.Equal(2, frames.Count(f => f.Event == "StageStarted" && f.Data["node_id"]?.GetValue<string>() == "test"));

        var detail = await server.WaitForStatusAsync(id, "success", "fail", "error");
        Assert.Equal("success", detail["status"]!.GetValue<string>());
        Assert.True(detail["simulated"]!.GetValue<bool>());
        Assert.Equal("stochastic", detail["simulation_mode"]!.GetValue<string>());
        Assert.True(detail["simulation_seed"]!.GetValue<long>() > 0);
        Assert.Equal(["start", "audit", "implement", "test", "fix", "test", "exit"], detail["completed_nodes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(File.Exists(marker), "simulated runs must never execute tool commands");

        var simulations = Path.Combine(server.Dir.Path, ".reactor", "simulations");
        var logsRoot = detail["logs_root"]!.GetValue<string>();
        Assert.StartsWith(simulations, logsRoot, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(logsRoot, "checkpoint.json")));
        Assert.False(Directory.Exists(Path.Combine(server.Dir.Path, ".reactor", "runs")), "simulated runs must not appear in the runs directory");

        var summary = JsonNode.Parse(await server.Client.GetStringAsync("/pipelines"))!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id)!;
        Assert.True(summary["simulated"]!.GetValue<bool>());
        Assert.Equal("stochastic", summary["simulation_mode"]!.GetValue<string>());

        var stage = await server.GetJsonAsync($"/pipelines/{id}/stages/test");
        Assert.Equal("[Simulated] success", stage["outcome"]!["notes"]!.GetValue<string>());
    }

    [Fact]
    public async Task Real_runs_are_not_flagged_and_stay_in_the_runs_directory()
    {
        await using var server = await ServerHarness.StartAsync();
        var created = await server.StartRunAsync("""
            digraph Real {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a"]
                start -> a -> exit
            }
            """);
        var id = created["id"]!.GetValue<string>();
        Assert.Null(created["simulated"]);

        var detail = await server.WaitForStatusAsync(id, "success");
        Assert.Null(detail["simulated"]);
        Assert.Null(detail["simulation_mode"]);
        Assert.StartsWith(Path.Combine(server.Dir.Path, ".reactor", "runs"), detail["logs_root"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        var summary = JsonNode.Parse(await server.Client.GetStringAsync("/pipelines"))!.AsArray().Single()!;
        Assert.Null(summary["simulated"]);

        // The prompt was written by the real codergen handler (simulation backend), unlike simulated runs.
        Assert.Contains("prompt.md", (await server.GetJsonAsync($"/pipelines/{id}/stages/a"))["files"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Stochastic_runs_animate_with_a_default_step_delay()
    {
        await using var server = await ServerHarness.StartAsync();
        var stopwatch = Stopwatch.StartNew();
        var created = await StartSimulationAsync(server, """
            digraph Slow {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a"]
                start -> a -> exit
            }
            """, new { mode = "stochastic" });

        await server.WaitForStatusAsync(created["id"]!.GetValue<string>(), "success");

        Assert.True(stopwatch.ElapsedMilliseconds >= 550, $"one simulated stage should take ~600 ms, took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Interactive_simulated_runs_are_driven_through_the_questions_api()
    {
        await using var server = await ServerHarness.StartAsync();
        var created = await StartSimulationAsync(server, Walkthrough, new { mode = "interactive" });
        var id = created["id"]!.GetValue<string>();
        Assert.Equal("interactive", created["simulation_mode"]!.GetValue<string>());

        var answered = new List<string>();
        string? previous = null;
        foreach (var (stage, value) in new[] { ("implement", "F"), ("fix", "S"), ("implement", "s"), ("review", "A") })
        {
            var question = await WaitForQuestionAsync(server, id, previous);
            Assert.Equal(stage, question["stage"]!.GetValue<string>());
            if (stage == "review")
            {
                Assert.Equal("Ship it?", question["text"]!.GetValue<string>());
                Assert.Equal(["A", "R"], question["options"]!.AsArray().Select(o => o!["key"]!.GetValue<string>()));
            }
            else
            {
                Assert.Equal($"Simulate the outcome of '{stage}'", question["text"]!.GetValue<string>());
                Assert.Equal(["S", "F", "R", "P"], question["options"]!.AsArray().Select(o => o!["key"]!.GetValue<string>()));
            }

            previous = question["id"]!.GetValue<string>();
            var answer = await server.Client.PostAsJsonAsync($"/pipelines/{id}/questions/{previous}/answer", new { value });
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            answered.Add(stage);
        }

        var detail = await server.WaitForStatusAsync(id, "success", "fail", "error");
        Assert.Equal("success", detail["status"]!.GetValue<string>());
        Assert.Equal(["start", "implement", "fix", "implement", "review", "exit"], detail["completed_nodes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("interactive", detail["simulation_mode"]!.GetValue<string>());
        var frames = await ReadEventStreamAsync(server, id);
        Assert.Equal(4, frames.Count(f => f.Event == "InterviewStarted"));
        Assert.Equal(4, frames.Count(f => f.Event == "InterviewCompleted"));
    }

    [Fact]
    public async Task Simulated_runs_resume_with_their_simulation_settings()
    {
        await using var server = await ServerHarness.StartAsync();
        var created = await StartSimulationAsync(server, """
            digraph Resumable {
                start [shape=Mdiamond] exit [shape=Msquare]
                plan [prompt="plan"]
                implement [prompt="implement"]
                start -> plan -> implement
                implement -> exit [condition="outcome=success"]
            }
            """, new { mode = "stochastic", scenario = new { step_delay_ms = 0, nodes = new { implement = new { script = new[] { "fail", "success" } } } } });
        var id = created["id"]!.GetValue<string>();

        var failed = await server.WaitForStatusAsync(id, "fail", "error", "success");
        Assert.Equal("fail", failed["status"]!.GetValue<string>());
        Assert.Equal("[Simulated] failure", failed["result"]!["failure_reason"]!.GetValue<string>());

        var resume = await server.Client.PostAsync($"/pipelines/{id}/resume", null);
        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode);
        var detail = await server.WaitForStatusAsync(id, "success", "fail", "error");
        Assert.Equal("success", detail["status"]!.GetValue<string>());
        Assert.True(detail["simulated"]!.GetValue<bool>());
        Assert.Equal("success", detail["node_statuses"]!["implement"]!.GetValue<string>());
        Assert.StartsWith(Path.Combine(server.Dir.Path, ".reactor", "simulations"), detail["logs_root"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Simulated_runs_can_be_cancelled()
    {
        await using var server = await ServerHarness.StartAsync();
        var created = await StartSimulationAsync(server, Loop, new { mode = "stochastic", scenario = new { step_delay_ms = 30_000 } });
        var id = created["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(id, "running");

        Assert.Equal(HttpStatusCode.Accepted, (await server.Client.PostAsync($"/pipelines/{id}/cancel", null)).StatusCode);
        await server.WaitForStatusAsync(id, "cancelled");
    }

    [Fact]
    public async Task Invalid_simulation_settings_are_rejected()
    {
        await using var server = await ServerHarness.StartAsync();

        var mode = await PostAsync(server, "/pipelines", new { dot = Loop, simulation = new { mode = "turbo" } });
        Assert.Equal(HttpStatusCode.BadRequest, mode.Status);
        Assert.Equal("The simulation settings are invalid.", mode.Body["error"]!.GetValue<string>());
        Assert.Contains("simulation.mode", mode.Body["diagnostics"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        var scenario = await PostAsync(server, "/pipelines", new { dot = Loop, simulation = new { scenario = new { nodes = new { fix = new { outcomes = new { maybe = 1 } } } } } });
        Assert.Equal(HttpStatusCode.BadRequest, scenario.Status);
        var diagnostic = scenario.Body["diagnostics"]!.AsArray().Single()!;
        Assert.Equal("simulation_scenario", diagnostic["rule"]!.GetValue<string>());
        Assert.Equal("fix", diagnostic["node_id"]!.GetValue<string>());

        var malformed = await PostAsync(server, "/pipelines", new { dot = Loop, simulation = 42 });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.Status);
        Assert.Contains("'simulation' must be an object", malformed.Body["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(server.Manager.Runs);

        var shorthand = await PostAsync(server, "/pipelines", new { dot = Walkthrough, simulation = "interactive" });
        Assert.Equal(HttpStatusCode.Created, shorthand.Status);
        Assert.Equal("interactive", shorthand.Body["simulation_mode"]!.GetValue<string>());
        var flag = await PostAsync(server, "/pipelines", new { dot = Loop, simulation = true });
        Assert.Equal("stochastic", flag.Body["simulation_mode"]!.GetValue<string>());
        foreach (var run in server.Manager.Runs)
        {
            server.Manager.Cancel(run.Id);
        }
    }

    private static async Task<JsonObject> StartSimulationAsync(ServerHarness server, string dot, object simulation)
    {
        var response = await server.Client.PostAsJsonAsync("/pipelines", new { dot, simulation });
        var body = await ServerHarness.ReadJsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body.ToJsonString());
        return body;
    }

    private static async Task<(HttpStatusCode Status, JsonObject Body)> PostAsync(ServerHarness server, string path, object body)
    {
        var response = await server.Client.PostAsJsonAsync(path, body);
        return (response.StatusCode, await ServerHarness.ReadJsonAsync(response));
    }

    private static async Task<JsonNode> WaitForQuestionAsync(ServerHarness server, string id, string? previous)
    {
        for (var i = 0; i < 400; i++)
        {
            var questions = JsonNode.Parse(await server.Client.GetStringAsync($"/pipelines/{id}/questions"))!.AsArray();
            if (questions.FirstOrDefault(q => q!["id"]!.GetValue<string>() != previous) is { } fresh)
            {
                return fresh;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("No new question was asked.");
    }

    /// <summary>Reads the run's whole event stream (it ends with an <c>end</c> event when the run finishes).</summary>
    private static async Task<List<(string Event, JsonObject Data)>> ReadEventStreamAsync(ServerHarness server, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var text = await server.Client.GetStringAsync($"/pipelines/{id}/events", timeout.Token);
        var frames = new List<(string, JsonObject)>();
        foreach (var block in text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var type = block.Split('\n').FirstOrDefault(l => l.StartsWith("event: ", StringComparison.Ordinal))?[7..];
            var data = block.Split('\n').FirstOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal))?[6..];
            if (type is not null && data is not null)
            {
                frames.Add((type, (JsonObject)JsonNode.Parse(data)!));
            }
        }

        return frames;
    }

    /// <summary>A custom handler that must never run in a simulation.</summary>
    private sealed class ThrowingHandler : INodeHandler
    {
        public Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The real custom handler ran during a simulation.");
    }
}

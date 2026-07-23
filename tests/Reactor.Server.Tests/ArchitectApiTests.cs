using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Handlers;
using Reactor.Server.Studio;

namespace Reactor.Server.Tests;

public sealed class ArchitectApiTests
{
    private const string Valid = """
        digraph feature {
            graph [goal="Add a feature"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            implement [label="Implement", prompt="Implement $goal"]
            test [label="Run tests", shape=parallelogram, tool_command="python -m pytest -q", goal_gate=true]
            fix [label="Fix", prompt="Fix the failing tests"]
            start -> implement -> test
            test -> exit [condition="outcome=success"]
            test -> fix [condition="outcome=fail"]
            fix -> test
        }
        """;

    private const string Broken = """
        digraph feature {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            start -> implement -> 
        }
        """;

    /// <summary>Valid DOT, but a succeeding tool has nowhere to go: no simulated flight can land.</summary>
    private const string DeadEnd = """
        digraph feature {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            test [shape=parallelogram, tool_command="python -m pytest -q"]
            start -> test
            test -> exit [condition="outcome=fail"]
        }
        """;

    private static CodergenResult Answer(string dot) => CodergenResult.FromText($"- Implement, then test; failures loop through Fix.\n\n```dot\n{dot}\n```\n");

    private static async Task<JsonObject> WaitForArchitectAsync(ServerHarness server, string id)
    {
        for (var i = 0; i < 400; i++)
        {
            var state = await server.GetJsonAsync($"/studio/api/architect/{id}");
            if (state["status"]!.GetValue<string>() is "success" or "fail" or "cancelled" or "error")
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Architect run {id} did not finish.");
    }

    private static async Task<string> StartAsync(ServerHarness server, object body)
    {
        var response = await server.Client.PostAsJsonAsync("/studio/api/architect", body);
        var json = await ServerHarness.ReadJsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToJsonString());
        return json["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_draft_that_fails_its_checks_is_repaired_with_the_findings()
    {
        var prompts = new List<string>();
        var backend = new ControllableBackend
        {
            Respond = node =>
            {
                lock (prompts)
                {
                    prompts.Add(node.Prompt);
                    return Answer(prompts.Count == 1 ? Broken : Valid);
                }
            },
        };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });

        var id = await StartAsync(server, new { mode = "draft", brief = "Add a feature with tests", models = new[] { "claude-sonnet-5", "gpt-5.6-luna" }, model = "claude-sonnet-5" });
        var state = await WaitForArchitectAsync(server, id);

        Assert.True(state["status"]!.GetValue<string>() == "success", state.ToJsonString());
        Assert.Equal(2, state["attempt"]!.GetValue<int>());
        var proposal = state["proposal"]!;
        Assert.True(proposal["valid"]!.GetValue<bool>(), state.ToJsonString());
        Assert.Equal(Valid.Trim(), proposal["dot"]!.GetValue<string>());
        Assert.Equal(1.0, proposal["forecast"]!["happy_path"]!.GetValue<double>());
        Assert.True(proposal["forecast"]!["realistic"]!.GetValue<double>() > 0.5);
        Assert.Equal("- Implement, then test; failures loop through Fix.", state["rationale"]!.GetValue<string>());

        Assert.Equal(2, prompts.Count);
        Assert.Contains("You are the Architect in Reactor Studio", prompts[0], StringComparison.Ordinal);
        Assert.Contains("Use `$goal` for the goal.", prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Reporting the stage outcome", prompts[0], StringComparison.Ordinal);
        Assert.Contains("## Brief\nAdd a feature with tests", prompts[0], StringComparison.Ordinal);
        Assert.Contains("## Models available\nclaude-sonnet-5, gpt-5.6-luna", prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("## What the checker found", prompts[0], StringComparison.Ordinal);
        Assert.Contains("## Your previous draft", prompts[1], StringComparison.Ordinal);
        Assert.Contains("## What the checker found\n- DOT syntax error", prompts[1], StringComparison.Ordinal);

        // The Architect is an ordinary run: its stages, model, and a scratch working directory.
        var run = await server.GetJsonAsync($"/pipelines/{id}");
        Assert.Equal("architect", run["name"]!.GetValue<string>());
        Assert.Contains($"{Path.DirectorySeparatorChar}.reactor{Path.DirectorySeparatorChar}studio{Path.DirectorySeparatorChar}architect{Path.DirectorySeparatorChar}", run["working_directory"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("claude-sonnet-5", server.Manager.Get(id)!.Graph.Nodes["design"].LlmModel);
        var check = File.ReadAllText(Path.Combine(run["logs_root"]!.GetValue<string>(), "check", "check.md"));
        Assert.StartsWith("# Draft 2: passed", check, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backend_failure_ends_the_run_with_the_backends_reason_without_checking()
    {
        var backend = new ControllableBackend { Respond = _ => CodergenResult.FromOutcome(Outcome.Fail("429: rate limited by the provider")) };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });

        var id = await StartAsync(server, new { mode = "draft", brief = "Anything" });
        var state = await WaitForArchitectAsync(server, id);

        Assert.True(state["status"]!.GetValue<string>() == "fail", state.ToJsonString());
        Assert.Equal(1, backend.Calls);
        Assert.Equal(0, state["attempt"]!.GetValue<int>());
        Assert.Null(state["proposal"]);
        Assert.Contains("429: rate limited by the provider", state["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wind_tunnel_rejects_a_draft_whose_flights_cannot_land_and_the_architect_gives_up_after_three_drafts()
    {
        var backend = new ControllableBackend { Respond = _ => Answer(DeadEnd) };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });

        var id = await StartAsync(server, new { mode = "revise", brief = "Make it shorter", dot = Valid });
        var state = await WaitForArchitectAsync(server, id);

        Assert.True(state["status"]!.GetValue<string>() == "fail", state.ToJsonString());
        Assert.Equal(ArchitectApi.MaxDrafts, state["attempt"]!.GetValue<int>());
        Assert.Equal(ArchitectApi.MaxDrafts, backend.Calls);
        var proposal = state["proposal"]!;
        Assert.False(proposal["valid"]!.GetValue<bool>());
        var problem = proposal["problems"]![0]!.GetValue<string>();
        Assert.StartsWith("Wind tunnel: even with every stage succeeding, 100% of simulated flights do not land", problem, StringComparison.Ordinal);
        Assert.Contains("giving up after 3 drafts", state["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_are_validated_and_a_simulating_server_explains_what_it_needs()
    {
        await using (var simulating = await ServerHarness.StartAsync())
        {
            var response = await simulating.Client.PostAsJsonAsync("/studio/api/architect", new { mode = "draft", brief = "x" });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("--backend", (await ServerHarness.ReadJsonAsync(response))["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains("architect", Strings((await simulating.GetJsonAsync("/studio/api/info"))["features"]));
        }

        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = new ControllableBackend() });
        foreach (var (body, message) in new (object, string)[]
                 {
                     (new { mode = "redesign", brief = "x" }, "'mode'"),
                     (new { mode = "draft" }, "brief"),
                     (new { mode = "revise", brief = "x" }, "current pipeline"),
                     (new { mode = "draft", brief = "x", model = "rm -rf /" }, "model id"),
                 })
        {
            var response = await server.Client.PostAsJsonAsync("/studio/api/architect", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(message, (await ServerHarness.ReadJsonAsync(response))["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/studio/api/architect/nope")).StatusCode);
        var plain = await server.StartRunAsync("digraph plain { start [shape=Mdiamond] exit [shape=Msquare] start -> exit }");
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/architect/{plain["id"]!.GetValue<string>()}")).StatusCode);
    }

    [Fact]
    public void Answers_split_into_the_last_pipeline_block_and_the_rationale()
    {
        var (dot, rationale) = ArchitectApi.SplitAnswer("## Findings\n- slow\n\n```json\n{}\n```\n\n```dot\ndigraph a { x -> y }\n```\n");
        Assert.Equal("digraph a { x -> y }", dot);
        Assert.Equal("## Findings\n- slow\n\n```json\n{}\n```", rationale.ReplaceLineEndings("\n"));

        var (bare, rest) = ArchitectApi.SplitAnswer("Here it is: digraph b { a -> b [label=\"}\"] } Done.");
        Assert.NotNull(bare);
        Assert.StartsWith("digraph b {", bare, StringComparison.Ordinal);

        var (none, text) = ArchitectApi.SplitAnswer("I cannot help with that.");
        Assert.Null(none);
        Assert.Equal("I cannot help with that.", text);
        _ = rest;
    }

    [Fact]
    public void The_model_is_set_on_the_design_stages_of_the_template()
    {
        var template = ArchitectApi.LoadTemplate(Path.GetTempPath());
        var graph = Reactor.Graphs.DotParser.Parse(ArchitectApi.WithModel(template, "gpt-5.6-luna", "high"));
        Assert.Equal("gpt-5.6-luna", graph.Nodes["design"].LlmModel);
        Assert.Equal("high", graph.Nodes["design"].ReasoningEffort);
        Assert.Equal(ArchitectApi.DesignType, graph.Nodes["design"].Type);
        Assert.Null(graph.Nodes["check"].LlmModel);
    }

    [Fact]
    public void The_prompt_carries_the_request_and_on_a_repair_the_previous_draft_and_findings()
    {
        var context = new PipelineContext();
        context.Set(ArchitectApi.Mode, "critique");
        context.Set(ArchitectApi.Current, Valid);
        context.Set(ArchitectApi.Evidence, "Run tests fails 69% of visits.");
        var first = ArchitectApi.ComposePrompt("Design the pipeline described below.", "GUIDE", context);
        Assert.StartsWith("GUIDE\n\n---\n\n# Request\n\nDesign the pipeline described below.\n\nCritique the current pipeline", first, StringComparison.Ordinal);
        Assert.Contains("## Brief\n(none: focus on what the evidence shows)", first, StringComparison.Ordinal);
        Assert.Contains("## Current pipeline\n```dot\ndigraph feature {", first, StringComparison.Ordinal);
        Assert.Contains("## Evidence\nRun tests fails 69% of visits.", first, StringComparison.Ordinal);
        Assert.Contains("## Models available\n(not listed: leave models out)", first, StringComparison.Ordinal);

        context.Set(ArchitectApi.Attempt, 1L);
        context.Set(ArchitectApi.Proposal, "digraph feature { start -> exit }");
        context.Set(ArchitectApi.Feedback, "- [reachability] stage 'x': unreachable");
        var repair = ArchitectApi.ComposePrompt("", "GUIDE", context);
        Assert.Contains("## Your previous draft\n```dot\ndigraph feature { start -> exit }\n```", repair, StringComparison.Ordinal);
        Assert.EndsWith("## What the checker found\n- [reachability] stage 'x': unreachable\n\nFix every finding. Answer again in the same format, with the complete corrected pipeline.", repair, StringComparison.Ordinal);
    }

    private static List<string> Strings(JsonNode? node) => node is JsonArray array ? array.Select(n => n!.GetValue<string>()).ToList() : [];
}

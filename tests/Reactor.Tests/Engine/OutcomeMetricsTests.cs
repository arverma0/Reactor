using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Handlers;
using Reactor.Tests.Support;

namespace Reactor.Tests.Engine;

public class OutcomeMetricsTests
{
    [Fact]
    public void Status_file_metrics_parse_numbers_booleans_and_numeric_strings_only()
    {
        var outcome = Outcome.FromStatusJson(JsonNode.Parse("""
            {"outcome": "success", "metrics": {
              "qa_passed": 30, "score": 4.5, "strict": true, "loose": false, "bundle_kb": " 915 ",
              "label": "high", "nested": {"a": 1}, "nan": "NaN", "9bad": 1, "has space": 2, "dotted.name": 7}}
            """));

        Assert.Equal(new Dictionary<string, double>
        {
            ["qa_passed"] = 30, ["score"] = 4.5, ["strict"] = 1, ["loose"] = 0, ["bundle_kb"] = 915, ["dotted.name"] = 7,
        }, outcome.Metrics);
        Assert.Equal(outcome.Metrics, Outcome.FromStatusJson(outcome.ToStatusJson()).Metrics);
        Assert.Empty(Outcome.FromStatusJson(JsonNode.Parse("""{"outcome": "success"}""")).Metrics);
    }

    [Fact]
    public async Task Metrics_reach_the_context_events_status_file_and_checkpoint_history()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("review", CodergenResult.FromOutcome(new Outcome
        {
            Status = StageStatus.Success,
            Metrics = new Dictionary<string, double> { ["visual_min_score"] = 4, ["qa_pending"] = 0 },
        }));
        var events = new List<PipelineEvent>();
        var options = TestPipelines.Options(dir) with { OnEvent = e => { lock (events) { events.Add(e); } } };

        var result = await TestPipelines.Runner(backend).RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              review [prompt="review"]
              start -> review -> exit
            }
            """, options);

        Assert.True(result.Succeeded);
        Assert.Equal(4.0, Convert.ToDouble(result.Context["metrics.visual_min_score"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("0", ContextValues.ToDisplayString(result.Context["metrics.qa_pending"]));
        var recorded = Assert.Single(events.OfType<MetricsRecorded>());
        Assert.Equal("review", recorded.NodeId);
        Assert.Equal(4, recorded.Metrics["visual_min_score"]);
        Assert.Equal(4, recorded.ToJson()["metrics"]!["visual_min_score"]!.GetValue<double>());

        var status = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(result.LogsRoot, "review", "status.json")))!;
        Assert.Equal(4, status["metrics"]!["visual_min_score"]!.GetValue<double>());
        var checkpoint = Checkpoint.TryLoadFromRunDirectory(result.LogsRoot)!;
        Assert.Equal(4, checkpoint.History.Single(h => h.NodeId == "review").Metrics!["visual_min_score"]);
    }

    [Fact]
    public async Task Metrics_reported_inside_parallel_branches_are_merged_back_at_the_join()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend()
            .On("visual", CodergenResult.FromOutcome(new Outcome { Metrics = new Dictionary<string, double> { ["visual_score"] = 3 } }))
            .On("math", CodergenResult.FromOutcome(new Outcome { Metrics = new Dictionary<string, double> { ["math_score"] = 5 } }));

        var result = await TestPipelines.Runner(backend).RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              fork [shape=component] join [shape=tripleoctagon]
              visual [prompt="v"] math [prompt="m"]
              start -> fork
              fork -> visual -> join
              fork -> math -> join
              join -> exit
            }
            """, TestPipelines.Options(dir));

        Assert.True(result.Succeeded);
        Assert.Equal("3", ContextValues.ToDisplayString(result.Context["metrics.visual_score"]));
        Assert.Equal("5", ContextValues.ToDisplayString(result.Context["metrics.math_score"]));
    }
}

public class FailureFlowEventTests
{
    private const string Pipeline = """
        digraph G {
          start [shape=Mdiamond] exit [shape=Msquare]
          build [prompt="b"] readme [prompt="r"]
          start -> build -> readme -> exit
        }
        """;

    private static async Task<List<PipelineEvent>> RunAsync(string dot, ScriptedBackend backend)
    {
        using var dir = new TempDir();
        var events = new List<PipelineEvent>();
        await TestPipelines.Runner(backend).RunAsync(dot, TestPipelines.Options(dir) with { OnEvent = e => { lock (events) { events.Add(e); } } });
        return events;
    }

    [Fact]
    public async Task A_failure_followed_along_an_unconditional_edge_is_reported()
    {
        var events = await RunAsync(Pipeline, new ScriptedBackend().On("build", Outcome.Fail("compile error")));

        var continued = Assert.Single(events.OfType<FailedStageContinued>());
        Assert.Equal(("build", "readme", "compile error"), (continued.NodeId, continued.To, continued.FailureReason));
        Assert.True(events.IndexOf(continued) < events.FindIndex(e => e is EdgeSelected { From: "build" }));
    }

    [Fact]
    public async Task Explicitly_routed_failures_and_failures_routed_to_a_diamond_are_not_reported()
    {
        var failEdge = await RunAsync(
            Pipeline.Replace("start -> build -> readme -> exit", "start -> build build -> readme [condition=\"outcome=success\"] build -> exit [condition=\"outcome=fail\"] readme -> exit"),
            new ScriptedBackend().On("build", Outcome.Fail("x")));
        Assert.Empty(failEdge.OfType<FailedStageContinued>());

        var diamond = await RunAsync(
            Pipeline.Replace("start -> build -> readme -> exit", "gate [shape=diamond] start -> build -> gate gate -> readme [condition=\"outcome=success\"] gate -> exit [condition=\"outcome=fail\"] readme -> exit"),
            new ScriptedBackend().On("build", Outcome.Fail("x")));
        Assert.Empty(diamond.OfType<FailedStageContinued>());
    }

    [Fact]
    public async Task Failures_inside_parallel_branches_are_reported_too()
    {
        var events = await RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              fork [shape=component] join [shape=tripleoctagon]
              a1 [prompt="a1"] a2 [prompt="a2"] b [prompt="b"]
              start -> fork
              fork -> a1 -> a2 -> join
              fork -> b -> join
              join -> exit
            }
            """,
            new ScriptedBackend().On("a1", Outcome.Fail("broken")));

        var continued = Assert.Single(events.OfType<FailedStageContinued>());
        Assert.Equal(("a1", "a2"), (continued.NodeId, continued.To));
    }
}

public class BackendMetadataTests
{
    private sealed class ThrowingBackend(Exception error) : ICodergenBackend
    {
        public Task<CodergenResult> RunAsync(Reactor.Graphs.Node node, string prompt, PipelineContext context, CancellationToken cancellationToken) =>
            throw error;
    }

    [Fact]
    public void Attached_metadata_is_found_on_the_exception_or_an_inner_exception()
    {
        var inner = BackendMetadata.Attach(new InvalidOperationException("inner"), new Dictionary<string, object?> { ["model"] = "m" });
        Assert.True(BackendMetadata.TryGet(new Exception("outer", inner), out var metadata));
        Assert.Equal("m", metadata["model"]);
        Assert.False(BackendMetadata.TryGet(new Exception("plain"), out _));
    }

    [Fact]
    public async Task A_stage_whose_backend_throws_still_records_backend_json_and_the_last_assistant_text()
    {
        using var dir = new TempDir();
        var error = BackendMetadata.Attach(new IOException("session lost"), new Dictionary<string, object?>
        {
            ["model"] = "claude-test",
            ["input_tokens"] = 1234L,
            [BackendMetadata.LastAssistantText] = "Half way: wrote src/a.ts, next is tests.",
        });

        var result = await TestPipelines.Runner(new ThrowingBackend(error)).RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              build [prompt="b"]
              start -> build -> exit
            }
            """, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["build"].Status);
        var backendJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(result.LogsRoot, "build", "backend.json")))!;
        Assert.Equal("claude-test", backendJson["model"]!.GetValue<string>());
        Assert.Equal(1234, backendJson["input_tokens"]!.GetValue<long>());
        Assert.Equal("error", backendJson["outcome"]!.GetValue<string>());
        Assert.Equal("session lost", backendJson["error"]!.GetValue<string>());
        Assert.Null(backendJson[BackendMetadata.LastAssistantText]);
        Assert.Contains("Half way", await File.ReadAllTextAsync(Path.Combine(result.LogsRoot, "build", CodergenHandler.PartialResponseFileName)));
    }

    [Fact]
    public async Task An_llm_fan_in_judge_records_its_backend_metadata_and_decision()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend()
            .On("judge", CodergenResult.FromText("BEST: b\nb plays better.").WithMetadata(new Dictionary<string, object?> { ["model"] = "judge-model", ["premium_requests"] = 1.0 }));

        var result = await TestPipelines.Runner(backend).RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              fork [shape=component]
              judge [shape=tripleoctagon, prompt="Pick the best prototype."]
              a [prompt="a"] b [prompt="b"]
              start -> fork
              fork -> a -> judge
              fork -> b -> judge
              judge -> exit
            }
            """, TestPipelines.Options(dir));

        Assert.True(result.Succeeded);
        var backendJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(result.LogsRoot, "judge", "backend.json")))!;
        Assert.Equal("judge-model", backendJson["model"]!.GetValue<string>());
        Assert.Equal(1, backendJson["premium_requests"]!.GetValue<double>());
        Assert.Equal("llm", backendJson["fan_in_method"]!.GetValue<string>());
        Assert.Equal("b", backendJson["fan_in_best_id"]!.GetValue<string>());
    }
}

public class FanInArtifactTests
{
    private const string BakeOff = """
        digraph G {
          start [shape=Mdiamond] exit [shape=Msquare]
          fork [shape=component]
          judge [shape=tripleoctagon, prompt="Pick the best prototype and explain why.", inputs="brief", outputs="verdict, report:JUDGEMENT.md"]
          brief_stage [prompt="Write the brief", outputs="brief"]
          a [prompt="a"] b [prompt="b"]
          start -> brief_stage -> fork
          fork -> a -> judge
          fork -> b -> judge
          judge -> exit
        }
        """;

    [Fact]
    public async Task An_llm_judge_reads_its_inputs_and_records_its_outputs()
    {
        using var dir = new TempDir();
        var prompts = new List<string>();
        var backend = new ScriptedBackend()
            .On("brief_stage", CodergenResult.FromText("Build a tiny game."))
            .On("judge", (_, prompt, context) =>
            {
                lock (prompts)
                {
                    prompts.Add(prompt);
                }

                File.WriteAllText(Path.Combine(context.WorkingDirectory, "JUDGEMENT.md"), "# b wins");
                return "BEST: b\nb is more fun.";
            });

        var result = await TestPipelines.Runner(backend).RunAsync(BakeOff, TestPipelines.Options(dir));

        Assert.True(result.Succeeded, result.FailureReason);
        var prompt = Assert.Single(prompts);
        Assert.Contains("## Inputs", prompt);
        Assert.Contains("Build a tiny game.", prompt);
        Assert.Contains("`report` → write the file `JUDGEMENT.md`", prompt);
        Assert.EndsWith("BEST: <id>", prompt.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal("b", result.Context["parallel.fan_in.best_id"]);
        Assert.Contains("verdict", ((Dictionary<string, object?>)result.Context["artifacts.verdict"]!)["name"]!.ToString());
        Assert.Equal("JUDGEMENT.md", ((Dictionary<string, object?>)result.Context["artifacts.report"]!)["path"]);
    }

    [Fact]
    public async Task A_judge_that_does_not_write_its_declared_file_fails_and_lint_flags_unjudged_fan_ins()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("judge", CodergenResult.FromText("BEST: a"));
        var result = await TestPipelines.Runner(backend).RunAsync(BakeOff.Replace("judge -> exit", "judge -> exit [condition=\"outcome=success\"]"), TestPipelines.Options(dir));
        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["judge"].Status);
        Assert.Contains("JUDGEMENT.md", result.NodeOutcomes["judge"].FailureReason);

        var diagnostics = PipelineValidatorTestAccess.Lint(BakeOff.Replace("prompt=\"Pick the best prototype and explain why.\", ", ""));
        Assert.Contains(diagnostics, d => d.Rule == "artifact_handlers" && d.NodeId == "judge" && d.Message.Contains("no prompt", StringComparison.Ordinal));
        Assert.DoesNotContain(PipelineValidatorTestAccess.Lint(BakeOff), d => d.Rule.StartsWith("artifact_", StringComparison.Ordinal));
    }
}

internal static class PipelineValidatorTestAccess
{
    public static IReadOnlyList<Reactor.Validation.Diagnostic> Lint(string dot) => Reactor.Validation.PipelineValidator.Validate(Reactor.Graphs.DotParser.Parse(dot));
}

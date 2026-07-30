using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Reactor.Conditions;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Tests.Support;
using Reactor.Validation;

namespace Reactor.Tests.Engine;

public class NumericConditionTests
{
    private static PipelineContext Context(params (string Key, object? Value)[] values)
    {
        var context = new PipelineContext();
        foreach (var (key, value) in values)
        {
            context.Set(key, value);
        }

        return context;
    }

    [Theory]
    [InlineData("metrics.score>=4", true)]
    [InlineData("metrics.score>4", false)]
    [InlineData("metrics.score<4.5", true)]
    [InlineData("metrics.score<=3.99", false)]
    [InlineData("context.metrics.score>=4", true)]
    [InlineData("metrics.missing>=0", false)]
    [InlineData("context.label>1", false)]
    [InlineData("context.offset>-2.5", true)]
    [InlineData("outcome=success && metrics.score>=4 && metrics.pending<=0", true)]
    [InlineData("visits.polish<3", true)]
    [InlineData("visits.polish>=2", true)]
    [InlineData("context.visits.polish=2", true)]
    [InlineData("visits.never<1", true)]
    public void Numeric_comparisons_and_visit_counts_evaluate(string condition, bool expected)
    {
        var context = Context(("metrics.score", 4L), ("metrics.pending", 0L), ("label", "high"), ("offset", -1.5), ("internal.visits.polish", 2L));
        Assert.Equal(expected, ConditionEvaluator.Evaluate(condition, Outcome.Success(), context));
    }

    [Fact]
    public void Two_character_operators_win_and_round_trip()
    {
        var expression = ConditionParser.Parse("visits.fix<=3 && metrics.qa_failed>0 && context.x!=y && context.n>=1.5 && context.m<2");
        Assert.Equal(
            [ConditionOperator.LessOrEqual, ConditionOperator.GreaterThan, ConditionOperator.NotEquals, ConditionOperator.GreaterOrEqual, ConditionOperator.LessThan],
            expression.Clauses.Select(c => c.Operator));
        Assert.Equal("visits.fix<=3", expression.Clauses[0].ToString());
        Assert.True(expression.Clauses[3].IsNumeric);
        Assert.Equal("1.5", expression.Clauses[3].Value);
    }
}

public class VisitLimitTests
{
    private const string Loop = """
        digraph G {
          start [shape=Mdiamond] exit [shape=Msquare]
          work [prompt="w" ATTRS] check [prompt="c"] escalate [prompt="e"]
          start -> work -> check
          check -> exit [condition="outcome=success"]
          check -> work [condition="outcome=fail"]
          check -> escalate [condition="context.never=1"]
          escalate -> exit
        }
        """;

    private static async Task<(PipelineResult Result, List<PipelineEvent> Events, ScriptedBackend Backend)> RunAsync(string dot, ScriptedBackend? backend = null, int maxSteps = 500)
    {
        using var dir = new TempDir();
        backend ??= new ScriptedBackend().On("check", Outcome.Fail("still broken"));
        var events = new List<PipelineEvent>();
        var result = await TestPipelines.Runner(backend).RunAsync(dot, TestPipelines.Options(dir) with { MaxSteps = maxSteps, OnEvent = e => { lock (events) { events.Add(e); } } });
        return (result, events, backend);
    }

    [Fact]
    public async Task Max_visits_reroutes_to_on_max_visits_once_the_limit_is_used_up()
    {
        var (result, events, backend) = await RunAsync(Loop.Replace("ATTRS", ", max_visits=3, on_max_visits=escalate"));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(3, backend.CallCount("work"));
        Assert.Equal(1, backend.CallCount("escalate"));
        var limit = Assert.Single(events.OfType<VisitLimitReached>());
        Assert.Equal(("work", 3, "escalate"), (limit.NodeId, limit.MaxVisits, limit.Target));
        Assert.Contains(events.OfType<EdgeSelected>(), e => e is { From: "work", To: "escalate", Reason: "max_visits" });
        Assert.Equal(3L, result.Context["internal.visits.work"]);
    }

    [Fact]
    public async Task Max_visits_without_a_target_fails_the_run()
    {
        var (result, _, backend) = await RunAsync(Loop.Replace("ATTRS", ", max_visits=2"));

        Assert.False(result.Succeeded);
        Assert.Contains("visit limit (max_visits=2)", result.FailureReason);
        Assert.Equal(2, backend.CallCount("work"));
    }

    [Fact]
    public async Task Visit_count_conditions_bound_a_loop()
    {
        var dot = Loop
            .Replace("ATTRS", "")
            .Replace("check -> work [condition=\"outcome=fail\"]", "check -> work [condition=\"outcome=fail && visits.work<2\"] check -> escalate [condition=\"outcome=fail && visits.work>=2\"]");
        var (result, _, backend) = await RunAsync(dot);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, backend.CallCount("work"));
        Assert.Equal(1, backend.CallCount("escalate"));
    }

    [Fact]
    public async Task Exhausted_targets_that_point_at_each_other_fail_instead_of_spinning()
    {
        var (result, _, _) = await RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              a [prompt="a", max_visits=1, on_max_visits=b] b [prompt="b", max_visits=1, on_max_visits=a]
              start -> a -> b -> a
              b -> exit [condition="context.never=1"]
            }
            """, maxSteps: 0);

        Assert.False(result.Succeeded);
        Assert.Contains("redirect in a cycle", result.FailureReason);
    }

    [Fact]
    public async Task Visit_limits_apply_inside_parallel_branches()
    {
        var backend = new ScriptedBackend().On("check", Outcome.Fail("x"));
        var (result, events, _) = await RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              fork [shape=component] join [shape=tripleoctagon]
              work [prompt="w", max_visits=2, on_max_visits=give_up] check [prompt="c"] give_up [prompt="g"] other [prompt="o"]
              start -> fork
              fork -> work -> check
              check -> work [condition="outcome=fail"]
              check -> join [condition="outcome=success"]
              give_up -> join
              fork -> other -> join
              join -> exit
            }
            """, backend);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, backend.CallCount("work"));
        Assert.Equal(1, backend.CallCount("give_up"));
        Assert.Single(events.OfType<VisitLimitReached>());
    }
}

public class OnFailTests
{
    private const string Chain = """
        digraph G {
          graph [GRAPH]
          start [shape=Mdiamond] exit [shape=Msquare]
          build [prompt="b" NODE] readme [prompt="r"] repair [prompt="fix"]
          start -> build -> readme -> exit
          build -> repair [condition="context.never=1"]
          repair -> exit
        }
        """;

    private static async Task<(PipelineResult Result, List<PipelineEvent> Events, ScriptedBackend Backend)> RunAsync(string graphAttrs, string nodeAttrs)
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("build", Outcome.Fail("compile error"));
        var events = new List<PipelineEvent>();
        var dot = Chain.Replace("GRAPH", graphAttrs).Replace("NODE", nodeAttrs);
        var result = await TestPipelines.Runner(backend).RunAsync(dot, TestPipelines.Options(dir) with { OnEvent = e => { lock (events) { events.Add(e); } } });
        return (result, events, backend);
    }

    [Fact]
    public async Task By_default_an_unrouted_failure_flows_on_as_the_spec_says()
    {
        var (result, events, backend) = await RunAsync("goal=\"g\"", "");
        Assert.True(result.Succeeded);
        Assert.Equal(1, backend.CallCount("readme"));
        Assert.Single(events.OfType<FailedStageContinued>());
    }

    [Fact]
    public async Task Default_on_fail_stop_ends_the_run_at_an_unrouted_failure()
    {
        var (result, events, backend) = await RunAsync("default_on_fail=stop", "");
        Assert.False(result.Succeeded);
        Assert.Equal("compile error", result.FailureReason);
        Assert.Equal(0, backend.CallCount("readme"));
        Assert.Empty(events.OfType<FailedStageContinued>());
    }

    [Fact]
    public async Task Node_on_fail_overrides_the_graph_default_and_retry_targets_still_route()
    {
        var (stopped, _, _) = await RunAsync("goal=\"g\"", ", on_fail=stop");
        Assert.False(stopped.Succeeded);

        var (continued, _, readme) = await RunAsync("default_on_fail=stop", ", on_fail=continue");
        Assert.True(continued.Succeeded);
        Assert.Equal(1, readme.CallCount("readme"));

        var (retried, _, repair) = await RunAsync("default_on_fail=stop", ", retry_target=repair");
        Assert.True(retried.Succeeded);
        Assert.Equal(1, repair.CallCount("repair"));
        Assert.Equal(0, repair.CallCount("readme"));
    }

    [Fact]
    public async Task Under_stop_a_failure_still_flows_into_a_diamond_that_inspects_it()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("build", Outcome.Fail("compile error"));
        var result = await TestPipelines.Runner(backend).RunAsync(
            """
            digraph G {
              graph [default_on_fail=stop]
              start [shape=Mdiamond] exit [shape=Msquare]
              build [prompt="b"] check [shape=diamond] readme [prompt="r"] repair [prompt="fix"]
              start -> build -> check
              check -> readme [condition="outcome=success"]
              check -> repair [condition="outcome=fail"]
              readme -> exit
              repair -> exit
            }
            """, TestPipelines.Options(dir));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(1, backend.CallCount("repair"));
        Assert.Equal(0, backend.CallCount("readme"));
    }
}

public class TimeoutHandlingTests
{
    /// <summary>A steerable backend whose first attempt of <c>build</c> hangs until cancelled (reporting usage), later attempts succeed.</summary>
    private sealed class HangingBackend : ICodergenBackend
    {
        public ConcurrentQueue<string> Prompts { get; } = new();

        public bool Steerable { get; init; }

        public Func<string, CancellationToken, Task<CodergenResult>>? Behavior { get; init; }

        public bool SupportsSteering => Steerable;

        public async Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            var stageDir = context.Run!.StageDirectory(node.Id);
            if (Behavior is not null)
            {
                return await Behavior(stageDir, cancellationToken);
            }

            if (Prompts.Count == 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException ex)
                {
                    throw BackendMetadata.Attach(ex, new Dictionary<string, object?>
                    {
                        ["model"] = "m", ["input_tokens"] = 1000L, ["cost_usd"] = 0.25,
                        [BackendMetadata.LastAssistantText] = "Wrote src/core.ts; the renderer is next.",
                    });
                }
            }

            return CodergenResult.FromText("done").WithMetadata(new Dictionary<string, object?> { ["cost_usd"] = 0.5 });
        }
    }

    /// <summary>The pending steering text, or null while there is none or the engine is still writing it.</summary>
    private static async Task<string?> ReadSteeringAsync(string stageDir, CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(stageDir, SteeringMessages.FileName);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<(PipelineResult Result, List<PipelineEvent> Events)> RunAsync(ICodergenBackend backend, string attrs, TempDir dir)
    {
        var events = new List<PipelineEvent>();
        var result = await TestPipelines.Runner(backend).RunAsync(
            $$"""
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              build [prompt="Build it" {{attrs}}]
              start -> build -> exit
            }
            """, TestPipelines.Options(dir) with { OnEvent = e => { lock (events) { events.Add(e); } } });
        return (result, events);
    }

    [Fact]
    public async Task A_timed_out_attempt_is_archived_costed_and_handed_off_to_the_retry()
    {
        using var dir = new TempDir();
        var backend = new HangingBackend();
        var (result, events) = await RunAsync(backend, ", timeout=\"300ms\", max_retries=1", dir);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains(events.OfType<StageFailed>(), f => f is { NodeId: "build", WillRetry: true } && f.Error.Contains("timed out after 300ms"));

        var archive = StageAttemptArchive.PathFor(Path.Combine(result.LogsRoot, "build"), 1);
        var dead = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(archive, "backend.json")))!;
        Assert.Equal("timeout", dead["outcome"]!.GetValue<string>());
        Assert.Equal(300, dead["timeout_ms"]!.GetValue<long>());
        Assert.Equal(1000, dead["input_tokens"]!.GetValue<long>());
        Assert.True(File.Exists(Path.Combine(archive, "prompt.md")));
        Assert.Contains("renderer is next", await File.ReadAllTextAsync(Path.Combine(archive, CodergenHandler.PartialResponseFileName)));

        var retry = backend.Prompts.Last();
        Assert.Contains("## Previous attempt (this is attempt 2 of 2)", retry);
        Assert.Contains("ran out of time after", retry);
        Assert.Contains("> Wrote src/core.ts; the renderer is next.", retry);
        Assert.Contains(archive, retry);
        Assert.Contains(StageTiming.PromptLine(TimeSpan.FromMilliseconds(300)), retry);
        Assert.DoesNotContain("## Previous attempt", backend.Prompts.First());

        Assert.Equal(0.75, result.Usage["cost_usd"], 6);
        Assert.Equal(1000, result.Usage["input_tokens"]);
        Assert.False(result.Context.ContainsKey(PreviousAttempt.ContextKey("build")));
    }

    [Fact]
    public async Task On_timeout_fail_does_not_retry_a_timeout()
    {
        using var dir = new TempDir();
        var backend = new HangingBackend();
        var (result, events) = await RunAsync(backend, ", timeout=\"200ms\", max_retries=2, on_timeout=fail", dir);

        Assert.Single(backend.Prompts);
        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["build"].Status);
        Assert.Contains(events.OfType<StageFailed>(), f => f is { NodeId: "build", WillRetry: false });
    }

    [Fact]
    public async Task A_soft_deadline_reminds_a_steerable_agent_before_the_time_limit()
    {
        using var dir = new TempDir();
        var backend = new HangingBackend
        {
            Steerable = true,
            Behavior = async (stageDir, ct) =>
            {
                string? text;
                while ((text = await ReadSteeringAsync(stageDir, ct)) is null)
                {
                    await Task.Delay(20, ct);
                }

                return CodergenResult.FromText("wrapped up: " + text);
            },
        };
        var (result, events) = await RunAsync(backend, ", timeout=\"20s\", soft_timeout=\"100ms\"", dir);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains("Time check from the pipeline", await File.ReadAllTextAsync(Path.Combine(result.LogsRoot, "build", "response.md")));
        Assert.Contains(events.OfType<StageLog>(), l => l.NodeId == "build" && l.Message.StartsWith("soft deadline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrap_up_tells_the_agent_time_is_up_and_lets_it_finish_within_the_grace_period()
    {
        using var dir = new TempDir();
        var backend = new HangingBackend
        {
            Steerable = true,
            Behavior = async (stageDir, ct) =>
            {
                while ((await ReadSteeringAsync(stageDir, ct))?.Contains("Time is up", StringComparison.Ordinal) != true)
                {
                    await Task.Delay(20, ct);
                }

                return CodergenResult.FromText("summary of what is done");
            },
        };
        var (result, events) = await RunAsync(backend, ", timeout=\"200ms\", soft_timeout=off, on_timeout=wrap_up, timeout_grace=\"10s\"", dir);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["build"].Status);
        Assert.Empty(events.OfType<StageFailed>());
        Assert.Contains(events.OfType<StageLog>(), l => l.Message.StartsWith("time limit reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_stage_stops_itself_at_its_limit_keeps_its_output_and_honors_on_timeout()
    {
        var command = OperatingSystem.IsWindows() ? "echo started& ping -n 8 127.0.0.1 >nul" : "echo started; sleep 7";
        foreach (var (onTimeout, attempts) in new[] { ("fail", 1), ("retry", 2) })
        {
            using var dir = new TempDir();
            var events = new List<PipelineEvent>();
            var result = await TestPipelines.Runner().RunAsync(
                $$"""
                digraph G {
                  start [shape=Mdiamond] exit [shape=Msquare]
                  qa [shape=parallelogram, tool_command="{{command}}", timeout="1s", max_retries=1, on_timeout={{onTimeout}}]
                  start -> qa -> exit
                }
                """, TestPipelines.Options(dir) with { OnEvent = e => { lock (events) { events.Add(e); } } });

            var qa = result.NodeOutcomes["qa"];
            Assert.Equal(StageStatus.Fail, qa.Status);
            Assert.Contains("timed out after 1s", qa.FailureReason);
            Assert.Equal(true, result.Context[ToolHandler.TimedOutKey]);
            Assert.Contains("started", await File.ReadAllTextAsync(Path.Combine(result.LogsRoot, "qa", "stdout.txt")));
            Assert.Equal(attempts, events.OfType<StageStarted>().Count(e => e.NodeId == "qa"));
        }
    }

    [Fact]
    public void Default_timing_reminds_only_long_steerable_stages()
    {
        var graph = DotParser.Parse("""digraph G { a [timeout="90m"] b [timeout="2m"] c [timeout="90m", soft_timeout=off] d [timeout="60m", on_timeout=wrap_up] }""");
        var steerable = new HangingBackend { Steerable = true };
        var handler = new CodergenHandler();

        Assert.Equal(TimeSpan.FromMinutes(72), StageTiming.For(graph.Nodes["a"], handler, HandlerTypes.Codergen, steerable).SoftDeadline);
        Assert.Null(StageTiming.For(graph.Nodes["a"], handler, HandlerTypes.Codergen, new HangingBackend()).SoftDeadline);
        Assert.Null(StageTiming.For(graph.Nodes["b"], handler, HandlerTypes.Codergen, steerable).SoftDeadline);
        Assert.Null(StageTiming.For(graph.Nodes["c"], handler, HandlerTypes.Codergen, steerable).SoftDeadline);
        Assert.Equal(TimeSpan.FromMinutes(6), StageTiming.For(graph.Nodes["d"], handler, HandlerTypes.Codergen, steerable).WrapUpGrace);
        Assert.Equal(TimeSpan.FromMinutes(90) + StageTiming.ToolBackstop, StageTiming.For(graph.Nodes["a"], handler, HandlerTypes.Tool, steerable).HardTimeout);
        Assert.Null(StageTiming.For(graph.Nodes["a"], handler, HandlerTypes.Tool, steerable).SoftDeadline);
    }
}

public class BudgetTests
{
    private const string Pipeline = """
        digraph G {
          graph [max_cost=2]
          start [shape=Mdiamond] exit [shape=Msquare]
          a [prompt="a"] b [prompt="b"] c [prompt="c"]
          start -> a -> b -> c -> exit
        }
        """;

    private static ScriptedBackend Priced() => new ScriptedBackend()
        .On("a", CodergenResult.FromText("a").WithMetadata(new Dictionary<string, object?> { ["cost_usd"] = 1.5, ["input_tokens"] = 10L }))
        .On("b", CodergenResult.FromText("b").WithMetadata(new Dictionary<string, object?> { ["cost_usd"] = 1.5, ["input_tokens"] = 10L }))
        .On("c", CodergenResult.FromText("c").WithMetadata(new Dictionary<string, object?> { ["cost_usd"] = 1.5, ["input_tokens"] = 10L }));

    [Fact]
    public async Task A_run_stops_resumably_at_its_budget_and_continues_with_a_higher_one()
    {
        using var dir = new TempDir();
        var backend = Priced();
        var events = new List<PipelineEvent>();
        var runner = TestPipelines.Runner(backend);
        var stopped = await runner.RunAsync(Pipeline, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.False(stopped.Succeeded);
        Assert.True(stopped.BudgetExceeded);
        Assert.Contains("Budget exceeded: $3.00 spent, max_cost is $2.00", stopped.FailureReason);
        Assert.Equal(0, backend.CallCount("c"));
        Assert.Equal(3.0, stopped.Usage["cost_usd"], 6);
        var budget = Assert.Single(events.OfType<BudgetExceeded>());
        Assert.Equal("c", budget.NextNodeId);
        var checkpoint = Checkpoint.TryLoadFromRunDirectory(stopped.LogsRoot)!;
        Assert.Null(checkpoint.FinalStatus);
        Assert.Equal(3.0, checkpoint.Usage["cost_usd"], 6);

        var resumed = await runner.ResumeAsync(stopped.LogsRoot, TestPipelines.Options(dir) with { MaxCostUsd = 10 });
        Assert.True(resumed.Succeeded, resumed.FailureReason);
        Assert.Equal(1, backend.CallCount("c"));
        Assert.Equal(4.5, resumed.Usage["cost_usd"], 6);
        Assert.Equal(30, resumed.Usage["input_tokens"]);
    }

    [Fact]
    public async Task Without_a_budget_usage_is_only_totalled()
    {
        using var dir = new TempDir();
        var result = await TestPipelines.Runner(Priced()).RunAsync(Pipeline.Replace("graph [max_cost=2]", ""), TestPipelines.Options(dir));
        Assert.True(result.Succeeded);
        Assert.Equal(4.5, result.Usage["cost_usd"], 6);
        Assert.Equal("$4.50 · 30 in / 0 out tokens", UsageLedger.Describe(result.Usage));
        Assert.Equal(["$0", "$0.0005", "$0.0097", "$0.01", "$12.30"], new[] { 0, 0.0005, 0.009662, 0.01, 12.3 }.Select(UsageLedger.FormatUsd));

        // A checkpoint round-trips in memory (as a fork does), where tokens are whole numbers and costs are not.
        var checkpoint = Checkpoint.TryLoadFromRunDirectory(result.LogsRoot)!;
        var copy = Checkpoint.FromJson(checkpoint.ToJson());
        Assert.Equal(4.5, copy.Usage["cost_usd"], 6);
        Assert.Equal(30, copy.Usage["input_tokens"]);
        var metrics = Outcome.FromStatusJson(new Outcome { Metrics = new Dictionary<string, double> { ["n"] = 3 } }.ToStatusJson()).Metrics;
        Assert.Equal(3, metrics["n"]);
        Assert.Equal(7, StageMetrics.Flatten(new System.Text.Json.Nodes.JsonObject { ["a"] = 7L, ["b"] = 2 })["a"]);
    }
}

public class MetricsFileTests
{
    [Fact]
    public async Task Metrics_from_the_stage_file_and_metrics_file_are_recorded_and_routable()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend()
            .On("qa", (node, _, context) =>
            {
                File.WriteAllText(Path.Combine(context.Run!.StageDirectory(node.Id), StageMetrics.FileName), """{"bundle_kb": 915, "strict": true}""");
                Directory.CreateDirectory(Path.Combine(context.Run.WorkingDirectory, "qa"));
                File.WriteAllText(Path.Combine(context.Run.WorkingDirectory, "qa", "report.json"),
                    """{"summary": {"passed": 28, "failed": 2, "pending": 0}, "results": [1, 2], "title": "QA", "bundle_kb": 1}""");
                return CodergenResult.FromOutcome(new Outcome { Metrics = new Dictionary<string, double> { ["bundle_kb"] = 900 } });
            });

        var result = await TestPipelines.Runner(backend).RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              qa [prompt="q", metrics_file="qa/report.json"] fix [prompt="f"]
              start -> qa
              qa -> fix [condition="metrics.summary.failed>0"]
              qa -> exit [condition="metrics.summary.failed<=0"]
              fix -> exit
            }
            """, TestPipelines.Options(dir));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains("fix", result.CompletedNodes);
        var metrics = result.NodeOutcomes["qa"].Metrics;
        Assert.Equal(900, metrics["bundle_kb"]);
        Assert.Equal(1, metrics["strict"]);
        Assert.Equal(28, metrics["summary.passed"]);
        Assert.False(metrics.ContainsKey("results"));
        Assert.False(metrics.ContainsKey("title"));
    }

    [Fact]
    public async Task A_missing_metrics_file_is_reported_only_for_attempts_that_did_not_fail()
    {
        using var dir = new TempDir();
        var calls = 0;
        var backend = new ScriptedBackend()
            .On("qa", (_, _, _) => ++calls == 1 ? Outcome.Fail("runner crashed before writing its report") : Outcome.Success("ok"));
        var logs = new List<StageLog>();

        var result = await TestPipelines.Runner(backend).RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              qa [prompt="q", metrics_file="qa/report.json", max_retries=1]
              start -> qa -> exit
            }
            """, TestPipelines.Options(dir) with { OnEvent = e => { if (e is StageLog log) logs.Add(log); } });

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Single(logs, l => l.Message.Contains("metrics_file 'qa/report.json' does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_metrics_file_resolves_in_the_branch_workspace_of_an_isolated_branch()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("a", (_, _, context) =>
        {
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "report.json"), """{"score": 5}""");
            return "built";
        });

        var result = await TestPipelines.Runner(backend).RunAsync(
            """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              fork [shape=component, branch_workdir="copy"] join [shape=tripleoctagon]
              a [prompt="a", metrics_file="report.json"] b [prompt="b"]
              start -> fork
              fork -> a -> join
              fork -> b -> join
              join -> exit
            }
            """, TestPipelines.Options(dir));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(5, result.NodeOutcomes["a"].Metrics["score"]);
        Assert.Equal("5", ContextValues.ToDisplayString(result.Context["metrics.score"]));
        Assert.False(File.Exists(dir.Combine("report.json")));
    }

    [Fact]
    public void Flatten_keeps_numbers_and_booleans_to_a_bounded_depth()
    {
        var metrics = StageMetrics.Flatten(JsonNode.Parse("""{"a": {"b": {"c": 1, "d": {"e": 2}}}, "f": false, "g": null, "h": "3"}"""));
        Assert.Equal(new Dictionary<string, double> { ["a.b.c"] = 1, ["f"] = 0 }, metrics);
    }
}

public class FlowLintTests
{
    private static List<Diagnostic> Lint(string dot, string rule) =>
        PipelineValidator.Validate(DotParser.Parse(dot)).Where(d => d.Rule == rule).ToList();

    [Fact]
    public void Fail_flows_onward_is_reported_for_unrouted_work_stages_only()
    {
        const string chain = """
            digraph G {
              GRAPH
              start [shape=Mdiamond] exit [shape=Msquare]
              build [prompt="b" ATTRS] gate [shape=hexagon] check [shape=diamond]
              start -> build -> exit
              gate -> exit
            }
            """;
        var plain = Lint(chain.Replace("GRAPH", "").Replace("ATTRS", ""), "fail_flows_onward");
        var diagnostic = Assert.Single(plain);
        Assert.Equal("build", diagnostic.NodeId);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
        Assert.Contains("continues to 'exit' as if it had succeeded", diagnostic.Message);

        Assert.Contains("retry_target is never used", Assert.Single(Lint(chain.Replace("GRAPH", "").Replace("ATTRS", ", retry_target=gate"), "fail_flows_onward")).Message);
        Assert.Empty(Lint(chain.Replace("GRAPH", "graph [default_on_fail=stop]").Replace("ATTRS", ""), "fail_flows_onward"));
        Assert.Empty(Lint(chain.Replace("GRAPH", "").Replace("ATTRS", ", on_fail=continue"), "fail_flows_onward"));
        Assert.Empty(Lint(chain.Replace("GRAPH", "").Replace("ATTRS", "").Replace("build -> exit", "build -> exit [condition=\"outcome=success\"] build -> gate [condition=\"outcome!=success\"]"), "fail_flows_onward"));
        Assert.Empty(Lint(chain.Replace("GRAPH", "").Replace("ATTRS", "").Replace("build -> exit", "build -> check check -> exit"), "fail_flows_onward"));
    }

    [Fact]
    public void Default_route_flags_tied_labeled_edges_but_not_human_gates_or_weighted_defaults()
    {
        const string dot = """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              verdict [prompt="v"] approve_gate [shape=hexagon] polish [prompt="p"]
              start -> verdict
              verdict -> approve_gate [label="Approve" WEIGHT]
              verdict -> polish [label="Rework"]
              approve_gate -> exit [label="Ship"]
              approve_gate -> polish [label="Fix"]
              polish -> verdict
            }
            """;
        var tied = Assert.Single(Lint(dot.Replace("WEIGHT", ""), "default_route"));
        Assert.Equal("verdict", tied.NodeId);
        Assert.Contains("'Approve' (approve_gate)", tied.Message);
        Assert.DoesNotContain(Lint(dot.Replace("WEIGHT", ""), "default_route"), d => d.NodeId == "approve_gate");
        Assert.Single(Lint(dot.Replace("WEIGHT", ", weight=0"), "default_route"));
        Assert.Empty(Lint(dot.Replace("WEIGHT", ", weight=-1"), "default_route"));
    }

    [Fact]
    public void Unbounded_loops_are_noted_until_the_engine_bounds_them()
    {
        const string dot = """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              fix [prompt="f" ATTRS] test [shape=parallelogram, tool_command="t"]
              start -> fix -> test
              test -> exit [condition="outcome=success"]
              test -> fix [condition="outcome=fail EDGE"]
            }
            """;
        var loop = Assert.Single(Lint(dot.Replace("ATTRS", "").Replace("EDGE", ""), "unbounded_loop"));
        Assert.Equal(Severity.Info, loop.Severity);
        Assert.Contains("fix → test", loop.Message);
        Assert.Empty(Lint(dot.Replace("ATTRS", ", max_visits=3").Replace("EDGE", ""), "unbounded_loop"));
        Assert.Empty(Lint(dot.Replace("ATTRS", "").Replace("EDGE", " && visits.fix<3"), "unbounded_loop"));
    }

    [Fact]
    public void Visit_limit_and_extension_attributes_are_validated()
    {
        var visits = Lint("""digraph G { a [on_max_visits=ghost] b [max_visits=2, on_max_visits=b] }""", "visit_limits");
        Assert.Equal(3, visits.Count);

        var types = Lint(
            """
            digraph G {
              graph [default_on_fail=maybe, max_cost=lots]
              a [max_visits=0, on_fail=abort, on_timeout=later, timeout="5m", soft_timeout=soon, timeout_grace=x, metrics_file="../r.json"]
              b [on_timeout=fail]
            }
            """, "attribute_types");
        Assert.Equal(9, types.Count);
        Assert.Contains(types, d => d.NodeId == "b" && d.Message.Contains("no effect without a timeout"));
    }

    [Fact]
    public void Failure_routes_must_hand_the_fixer_the_failing_report()
    {
        const string dot = """
            digraph G {
              start [shape=Mdiamond] exit [shape=Msquare]
              polish [prompt="p", inputs="review_findings" EXTRA] recheck [shape=parallelogram, tool_command="npm run qa", outputs="recheck_report"]
              review [prompt="r", outputs="review_findings"]
              route [shape=diamond]
              start -> review -> polish -> recheck
              recheck -> exit [condition="outcome=success"]
              ROUTE
            }
            """;
        var direct = Assert.Single(Lint(dot.Replace("EXTRA", "").Replace("ROUTE", "recheck -> polish [condition=\"outcome=fail\"]"), "failure_evidence"));
        Assert.Equal(("recheck", "polish"), direct.Edge);
        Assert.Contains("'recheck_report'", direct.Message);
        Assert.Single(Lint(dot.Replace("EXTRA", "").Replace("recheck -> exit [condition=\"outcome=success\"]", "recheck -> route").Replace("ROUTE", "route -> exit [condition=\"outcome=success\"] route -> polish [condition=\"outcome!=success\"]"), "failure_evidence"));
        Assert.Empty(Lint(dot.Replace("EXTRA", ", inputs=\"review_findings, recheck_report\"").Replace("inputs=\"review_findings\" ,", "").Replace("ROUTE", "recheck -> polish [condition=\"outcome=fail\"]"), "failure_evidence"));
    }

    [Fact]
    public void Condition_values_understand_visits_and_metrics_keys()
    {
        var diagnostics = Lint(
            """
            digraph G {
              a [prompt="a"] b [prompt="b"]
              a -> b [condition="visits.ghost<2 && metrics.score>=4 && outcome>1"]
            }
            """, "condition_values");
        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Message.Contains("visits of 'ghost'"));
        Assert.Contains(diagnostics, d => d.Message.Contains("outcome is never a number"));
    }
}

public class VisitLimitSimulationTests
{
    [Fact]
    public async Task The_wind_tunnel_bounds_loops_with_max_visits_and_reports_the_reroute()
    {
        var graph = Reactor.Tests.Simulation.Sim.Prepare("""
            digraph G {
              graph [default_on_fail=stop]
              start [shape=Mdiamond] exit [shape=Msquare]
              polish [prompt="p", max_visits=3, on_max_visits=approve]
              recheck [shape=parallelogram, tool_command="qa"]
              approve [shape=hexagon, label="Ship?"]
              start -> polish -> recheck
              recheck -> exit [condition="outcome=success"]
              recheck -> polish [condition="outcome=fail"]
              approve -> exit [label="[S] Ship"]
            }
            """);
        var report = await Reactor.Tests.Simulation.Sim.ReportAsync(graph, """{"nodes": {"recheck": {"outcomes": "fail"}}}""", runs: 50);

        Assert.Equal(50, report.Outcomes["success"]);
        Assert.Equal(150, report.Nodes["polish"].Visits);
        Assert.Equal(3, report.Nodes["polish"].MaxVisits);
        var reroute = Assert.Single(report.Edges, e => e.Kind == "max_visits");
        Assert.Equal(("polish", "approve", 50), (reroute.From, reroute.To, reroute.Count));
    }
}

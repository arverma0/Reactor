using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Human;
using Reactor.Tests.Support;
using Reactor.Validation;

namespace Reactor.Tests.Engine;

public class EngineTests
{
    private const string Linear = """
        digraph Linear {
            graph [goal="Create a hello world script"]
            start [shape=Mdiamond]
            exit  [shape=Msquare]
            plan      [prompt="Plan: $goal"]
            implement [prompt="Implement the plan"]
            review    [prompt="Review"]
            start -> plan -> implement -> review -> exit
        }
        """;

    [Fact]
    public async Task Linear_pipeline_runs_end_to_end_in_simulation_mode()
    {
        using var dir = new TempDir();
        var events = new List<PipelineEvent>();
        var runner = TestPipelines.Runner();
        runner.OnEvent += e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };

        var result = await runner.RunAsync(Linear, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "plan", "implement", "review", "exit"], result.CompletedNodes);
        foreach (var stage in new[] { "plan", "implement", "review" })
        {
            Assert.True(File.Exists(Path.Combine(result.LogsRoot, stage, "prompt.md")), $"{stage}/prompt.md");
            Assert.True(File.Exists(Path.Combine(result.LogsRoot, stage, "response.md")), $"{stage}/response.md");
            Assert.True(File.Exists(Path.Combine(result.LogsRoot, stage, "status.json")), $"{stage}/status.json");
        }

        Assert.Equal("[Simulated] Response for stage: plan", File.ReadAllText(Path.Combine(result.LogsRoot, "plan", "response.md")));
        Assert.StartsWith("Plan: Create a hello world script", File.ReadAllText(Path.Combine(result.LogsRoot, "plan", "prompt.md")).Split("---\n")[^1].Trim(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "graph.dot")));
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "result.json")));

        var checkpoint = Checkpoint.Load(Path.Combine(result.LogsRoot, "checkpoint.json"));
        Assert.Equal("exit", checkpoint.CurrentNode);
        Assert.Contains("implement", checkpoint.CompletedNodes);
        Assert.Equal("success", checkpoint.FinalStatus);

        Assert.IsType<PipelineStarted>(events[0]);
        Assert.IsType<PipelineCompleted>(events[^1]);
        Assert.Equal(5, events.OfType<StageStarted>().Count());
        Assert.Equal(5, events.OfType<StageCompleted>().Count());
        Assert.Equal(5, events.OfType<CheckpointSaved>().Count());
        Assert.Equal("Create a hello world script", result.Context["graph.goal"]);
        Assert.Equal("review", result.Context["last_stage"]);
    }

    [Fact]
    public async Task Invalid_pipelines_are_rejected_before_execution()
    {
        using var dir = new TempDir();
        var error = await Assert.ThrowsAsync<ValidationException>(() => TestPipelines.Runner().RunAsync("digraph G { a -> b }", TestPipelines.Options(dir)));
        Assert.Contains(error.Errors, d => d.Rule == "start_node");
    }

    [Fact]
    public async Task Conditional_branching_follows_success_and_fail_paths()
    {
        const string source = """
            digraph Branch {
                start [shape=Mdiamond] exit [shape=Msquare]
                check [prompt="check"]
                good [prompt="good"] bad [prompt="bad"]
                start -> check
                check -> good [condition="outcome=success"]
                check -> bad  [condition="outcome=fail"]
                good -> exit
                bad -> exit
            }
            """;
        using var dir = new TempDir();

        var ok = await TestPipelines.Runner(new ScriptedBackend().On("check", "fine")).RunAsync(source, TestPipelines.Options(dir, "ok"));
        var failed = await TestPipelines.Runner(new ScriptedBackend().On("check", Outcome.Fail("broken"))).RunAsync(source, TestPipelines.Options(dir, "fail"));

        Assert.Contains("good", ok.CompletedNodes);
        Assert.DoesNotContain("bad", ok.CompletedNodes);
        Assert.Contains("bad", failed.CompletedNodes);
        Assert.Equal(StageStatus.Success, failed.Status);
    }

    [Fact]
    public async Task Spec_branch_example_loops_back_through_a_pass_through_diamond()
    {
        const string source = """
            digraph Branch {
                graph [goal="Implement and validate a feature"]
                node [shape=box, timeout="900s"]
                start     [shape=Mdiamond, label="Start"]
                exit      [shape=Msquare, label="Exit"]
                plan      [label="Plan", prompt="Plan the implementation"]
                implement [label="Implement", prompt="Implement the plan"]
                validate  [label="Validate", prompt="Run tests"]
                gate      [shape=diamond, label="Tests passing?"]
                start -> plan -> implement -> validate -> gate
                gate -> exit      [label="Yes", condition="outcome=success"]
                gate -> implement [label="No", condition="outcome!=success"]
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("validate", Outcome.Fail("2 tests failed"), "all tests pass");

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "plan", "implement", "validate", "gate", "implement", "validate", "gate", "exit"], result.CompletedNodes);
        Assert.Equal(2, backend.CallCount("implement"));
        Assert.True(Directory.Exists(Path.Combine(result.LogsRoot, "implement", "history", "visit-1")));
    }

    [Fact]
    public async Task Retries_on_fail_and_retry_outcomes_up_to_max_retries()
    {
        const string source = """
            digraph R {
                start [shape=Mdiamond] exit [shape=Msquare]
                flaky [prompt="x", max_retries=2]
                start -> flaky -> exit
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("flaky", Outcome.Fail("boom"), Outcome.Retry("again"), "finally");
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(3, backend.CallCount("flaky"));
        Assert.Equal(2, events.OfType<StageRetrying>().Count());
        Assert.Equal([2, 3], events.OfType<StageRetrying>().Select(e => e.Attempt));
        Assert.Equal(0L, result.Context["internal.retry_count.flaky"]);
    }

    [Fact]
    public async Task Retry_exhaustion_fails_or_accepts_partial()
    {
        const string template = """
            digraph R {
                start [shape=Mdiamond] exit [shape=Msquare]
                stubborn [prompt="x", max_retries=1 ALLOW]
                start -> stubborn -> exit
            }
            """;
        using var dir = new TempDir();

        var failed = await TestPipelines.Runner(new ScriptedBackend().On("stubborn", Outcome.Retry("not yet")))
            .RunAsync(template.Replace(" ALLOW", "", StringComparison.Ordinal), TestPipelines.Options(dir, "a"));
        var partial = await TestPipelines.Runner(new ScriptedBackend().On("stubborn", Outcome.Retry("not yet")))
            .RunAsync(template.Replace(" ALLOW", ", allow_partial=true", StringComparison.Ordinal), TestPipelines.Options(dir, "b"));

        Assert.Equal(StageStatus.Fail, failed.NodeOutcomes["stubborn"].Status);
        Assert.Contains("max retries exceeded", failed.NodeOutcomes["stubborn"].FailureReason, StringComparison.Ordinal);
        Assert.Equal(StageStatus.Success, partial.Status);
        Assert.Equal(StageStatus.PartialSuccess, partial.NodeOutcomes["stubborn"].Status);
        var status = JsonNode.Parse(File.ReadAllText(Path.Combine(partial.LogsRoot, "stubborn", "status.json")))!;
        Assert.Equal("partial_success", status["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Graph_default_max_retries_applies_and_transient_exceptions_are_retried()
    {
        const string source = """
            digraph R {
                graph [default_max_retries=2]
                start [shape=Mdiamond] exit [shape=Msquare]
                net [prompt="x"]
                bug [prompt="y"]
                start -> net -> bug -> exit
            }
            """;
        using var dir = new TempDir();
        var attempts = 0;
        var backend = new ScriptedBackend()
            .On("net", (_, _, _) => ++attempts < 3 ? throw new IOException("connection reset") : "ok")
            .On("bug", (_, _, _) => throw new InvalidOperationException("logic error"));

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(3, attempts);
        Assert.Equal(1, backend.CallCount("bug"));
        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["bug"].Status);
        Assert.Contains("logic error", result.NodeOutcomes["bug"].FailureReason, StringComparison.Ordinal);

        // Without a goal gate a failed stage still follows its unconditional edge (spec 3.2), so the run completes.
        Assert.Equal(StageStatus.Success, result.Status);
    }

    [Fact]
    public async Task Goal_gate_blocks_exit_until_satisfied()
    {
        const string source = """
            digraph Gate {
                start [shape=Mdiamond] exit [shape=Msquare]
                implement [prompt="x", goal_gate=true, retry_target="implement"]
                review [prompt="y"]
                start -> implement -> review -> exit
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("implement", Outcome.Fail("not done"), "done");
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "implement", "review", "implement", "review", "exit"], result.CompletedNodes);
        Assert.Single(events.OfType<GoalGateUnsatisfied>());
    }

    [Fact]
    public async Task Goal_gate_without_retry_target_fails_the_pipeline_and_graph_target_is_a_fallback()
    {
        const string noTarget = """
            digraph Gate {
                start [shape=Mdiamond] exit [shape=Msquare]
                implement [prompt="x", goal_gate=true]
                start -> implement -> exit
            }
            """;
        using var dir = new TempDir();

        var failed = await TestPipelines.Runner(new ScriptedBackend().On("implement", Outcome.PartialSuccess("ok-ish"), Outcome.Fail("nope")))
            .RunAsync(noTarget.Replace("goal_gate=true]", "goal_gate=true]\n fixer [prompt=\"f\"]\n implement -> fixer [condition=\"outcome=fail\"]\n fixer -> exit", StringComparison.Ordinal), TestPipelines.Options(dir, "a"));
        Assert.Equal(StageStatus.Success, failed.Status);

        var gateFail = await TestPipelines.Runner(new ScriptedBackend().On("implement", Outcome.Fail("nope")))
            .RunAsync(noTarget.Replace("goal_gate=true]", "goal_gate=true]\n fixer [prompt=\"f\"]\n implement -> fixer [condition=\"outcome=fail\"]\n fixer -> exit", StringComparison.Ordinal), TestPipelines.Options(dir, "b"));
        Assert.Equal(StageStatus.Fail, gateFail.Status);
        Assert.Contains("Goal gate 'implement' unsatisfied", gateFail.FailureReason, StringComparison.Ordinal);

        var graphTarget = await TestPipelines.Runner(new ScriptedBackend().On("implement", Outcome.Fail("nope"), "fixed"))
            .RunAsync(noTarget.Replace("digraph Gate {", "digraph Gate {\n graph [retry_target=\"implement\"]", StringComparison.Ordinal)
                .Replace("goal_gate=true]", "goal_gate=true]\n fixer [prompt=\"f\"]\n implement -> fixer [condition=\"outcome=fail\"]\n fixer -> exit", StringComparison.Ordinal), TestPipelines.Options(dir, "c"));
        Assert.Equal(StageStatus.Success, graphTarget.Status);
        Assert.Equal(2, graphTarget.CompletedNodes.Count(n => n == "implement"));
    }

    [Fact]
    public async Task Failure_routing_uses_fail_edges_then_retry_targets_then_terminates()
    {
        const string source = """
            digraph F {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a", retry_target="recover"]
                recover [prompt="r"]
                b [prompt="b"]
                start -> a
                a -> b [condition="outcome=success"]
                a -> recover [condition="outcome=retry"]
                recover -> b
                b -> exit
            }
            """;
        using var dir = new TempDir();

        var viaTarget = await TestPipelines.Runner(new ScriptedBackend().On("a", Outcome.Fail("x"))).RunAsync(source, TestPipelines.Options(dir, "a"));
        Assert.Equal(["start", "a", "recover", "b", "exit"], viaTarget.CompletedNodes);

        var terminated = await TestPipelines.Runner(new ScriptedBackend().On("a", Outcome.Fail("disk full")))
            .RunAsync(source.Replace(", retry_target=\"recover\"", "", StringComparison.Ordinal), TestPipelines.Options(dir, "b"));
        Assert.Equal(StageStatus.Fail, terminated.Status);
        Assert.Equal("disk full", terminated.FailureReason);
    }

    [Fact]
    public async Task No_matching_conditional_edge_fails_with_a_clear_reason()
    {
        const string source = """
            digraph F {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a"]
                start -> a
                a -> exit [condition="context.ready=true"]
            }
            """;
        using var dir = new TempDir();
        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir));
        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.Contains("No eligible outgoing edge from 'a'", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_updates_are_visible_to_the_next_node_and_conditions()
    {
        const string source = """
            digraph C {
                start [shape=Mdiamond] exit [shape=Msquare]
                producer [prompt="p"]
                consumer [prompt="c"]
                skipped [prompt="s"]
                start -> producer -> consumer
                consumer -> exit [condition="context.flavor=vanilla"]
                consumer -> skipped
                skipped -> exit
            }
            """;
        using var dir = new TempDir();
        string? seen = null;
        var backend = new ScriptedBackend()
            .On("producer", Outcome.Success("made", new Dictionary<string, object?> { ["flavor"] = "vanilla", ["count"] = 2 }))
            .On("consumer", (_, _, ctx) =>
            {
                seen = ctx.GetString("flavor");
                return "consumed";
            });

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("vanilla", seen);
        Assert.DoesNotContain("skipped", result.CompletedNodes);
        Assert.Equal(2L, result.Context["count"]);
    }

    [Fact]
    public async Task Agent_written_status_file_drives_routing_by_preferred_label()
    {
        const string source = """
            digraph S {
                start [shape=Mdiamond] exit [shape=Msquare]
                review [prompt="review"]
                fix [prompt="fix"]
                ship [prompt="ship"]
                start -> review
                review -> fix [label="[F] Fix"]
                review -> ship [label="[S] Ship", weight=10]
                fix -> exit
                ship -> exit
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend { SupportsStatusFile = true }.On("review", (node, prompt, ctx) =>
        {
            Assert.Contains("status.json", prompt, StringComparison.Ordinal);
            Assert.Contains("\"[F] Fix\"", prompt, StringComparison.Ordinal);
            var stageDir = ctx.Run!.StageDirectory(node.Id);
            File.WriteAllText(Path.Combine(stageDir, "status.json"),
                """{"outcome":"success","preferred_label":"Fix","context_updates":{"review.issues":3},"notes":"found issues"}""");
            return "I found 3 issues.";
        });

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Contains("fix", result.CompletedNodes);
        Assert.DoesNotContain("ship", result.CompletedNodes);
        Assert.Equal(3L, result.Context["review.issues"]);
        Assert.Equal("review", result.NodeOutcomes["review"].ContextUpdates["last_stage"]);
        Assert.Equal("found issues", result.NodeOutcomes["review"].Notes);
    }

    [Fact]
    public async Task Stylesheet_and_goal_expansion_reach_the_backend()
    {
        const string source = """
            digraph S {
                graph [goal="build a CLI", model_stylesheet="box { llm_model: model-a; } .critical { llm_model: model-b; llm_provider: openai; }"]
                start [shape=Mdiamond] exit [shape=Msquare]
                plan [prompt="Plan $goal"]
                check [prompt="Check $goal", class="critical"]
                start -> plan -> check -> exit
            }
            """;
        using var dir = new TempDir();
        var models = new Dictionary<string, string?>();
        var backend = new ScriptedBackend()
            .On("plan", (node, prompt, _) =>
            {
                models[node.Id] = node.LlmModel;
                Assert.EndsWith("Plan build a CLI", prompt, StringComparison.Ordinal);
                return "planned";
            })
            .On("check", (node, _, _) =>
            {
                models[node.Id] = node.LlmModel + "/" + node.LlmProvider;
                return "checked";
            });

        await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("model-a", models["plan"]);
        Assert.Equal("model-b/openai", models["check"]);
    }

    [Fact]
    public async Task Custom_handlers_can_be_registered_by_type()
    {
        const string source = """
            digraph C {
                start [shape=Mdiamond] exit [shape=Msquare]
                custom [type="my_custom_type", custom_attr="value"]
                start -> custom -> exit
            }
            """;
        using var dir = new TempDir();
        var runner = TestPipelines.Runner();
        var handler = new RecordingHandler();
        runner.RegisterHandler("my_custom_type", handler);

        var result = await runner.RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal("value", handler.SeenAttribute);
        Assert.Equal("yes", result.Context["custom.ran"]);
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "custom", "status.json")));
    }

    [Fact]
    public async Task Auto_status_turns_a_silent_handler_into_success()
    {
        const string source = """
            digraph A {
                start [shape=Mdiamond] exit [shape=Msquare]
                quiet [type="silent", auto_status=true]
                loud [type="silent", goal_gate=true]
                start -> quiet -> loud -> exit
            }
            """;
        using var dir = new TempDir();
        var runner = TestPipelines.Runner();
        runner.RegisterHandler("silent", new SilentHandler());

        var result = await runner.RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.NodeOutcomes["quiet"].Status);
        Assert.Contains("auto-status", result.NodeOutcomes["quiet"].Notes, StringComparison.Ordinal);
        Assert.Contains("without producing an outcome", result.NodeOutcomes["loud"].FailureReason, StringComparison.Ordinal);
        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.Contains("Goal gate 'loud' unsatisfied", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pipeline_with_more_than_ten_nodes_completes()
    {
        var stages = Enumerable.Range(1, 12).Select(i => $"s{i}").ToList();
        var source = "digraph Big { start [shape=Mdiamond] exit [shape=Msquare] " +
                     string.Join(" ", stages.Select(s => $"{s} [prompt=\"{s}\"]")) +
                     " start -> " + string.Join(" -> ", stages) + " -> exit }";
        using var dir = new TempDir();

        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(14, result.CompletedNodes.Count);
    }

    [Fact]
    public async Task Node_timeout_fails_the_stage()
    {
        const string source = """
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                slow [prompt="x", timeout="200ms", goal_gate=true]
                start -> slow -> exit
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend { Delay = TimeSpan.FromSeconds(30) };

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.Contains("timed out after 200ms", result.NodeOutcomes["slow"].FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_marks_it_cancelled()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend { Delay = TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await TestPipelines.Runner(backend).RunAsync(Linear, TestPipelines.Options(dir), cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.Equal(["start"], result.CompletedNodes);
    }

    [Fact]
    public async Task Max_steps_guards_against_infinite_loops()
    {
        const string source = """
            digraph L {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a"] b [prompt="b"]
                start -> a -> b
                b -> a
                b -> exit [condition="context.never=true"]
            }
            """;
        using var dir = new TempDir();
        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir) with { MaxSteps = 20 });
        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.Contains("Maximum step count (20)", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loop_restart_relaunches_with_a_fresh_log_directory_and_context()
    {
        const string source = """
            digraph R {
                start [shape=Mdiamond] exit [shape=Msquare]
                work [prompt="w"]
                decide [shape=diamond]
                start -> work -> decide
                decide -> exit [condition="context.round=2"]
                decide -> work [loop_restart=true]
            }
            """;
        using var dir = new TempDir();
        var round = 0;
        var backend = new ScriptedBackend().On("work", (_, _, ctx) =>
        {
            round++;
            Assert.Equal("", ctx.GetString("round"));
            return Outcome.Success("w", new Dictionary<string, object?> { ["round"] = round });
        });
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(1, result.LoopRestarts);
        Assert.EndsWith("-restart-1", result.LogsRoot, StringComparison.Ordinal);
        Assert.Equal(["work", "decide", "exit"], result.CompletedNodes);
        Assert.Single(events.OfType<LoopRestarted>());
        Assert.True(File.Exists(Path.Combine(dir.Combine("run"), "checkpoint.json")));
    }

    [Fact]
    public async Task Fidelity_controls_preamble_and_thread_resolution()
    {
        const string source = """
            digraph F {
                graph [goal="g"]
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="first"]
                b [prompt="second", fidelity="full", thread_id="main"]
                c [prompt="third", fidelity="truncate"]
                start -> a -> b -> c -> exit
            }
            """;
        using var dir = new TempDir();
        var seen = new Dictionary<string, (string Prompt, string Fidelity, string Thread)>();
        CodergenResult Capture(Node n, string p, PipelineContext ctx)
        {
            seen[n.Id] = (p, ctx.GetString("internal.fidelity"), ctx.GetString("internal.thread_id"));
            return "ok";
        }

        var backend = new ScriptedBackend().On("a", Capture).On("b", Capture).On("c", Capture);
        await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("compact", seen["a"].Fidelity);
        Assert.Contains("## Pipeline context", seen["a"].Prompt, StringComparison.Ordinal);
        Assert.Equal("full", seen["b"].Fidelity);
        Assert.Equal("main", seen["b"].Thread);
        Assert.Equal("second", seen["b"].Prompt);
        Assert.Equal("truncate", seen["c"].Fidelity);
        Assert.DoesNotContain("Completed stages", seen["c"].Prompt, StringComparison.Ordinal);
        Assert.Contains("Goal: g", seen["c"].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edge_fidelity_overrides_node_and_graph_defaults()
    {
        const string source = """
            digraph F {
                graph [default_fidelity="summary:low"]
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a"] b [prompt="b", fidelity="compact"] c [prompt="c"]
                start -> a -> b
                b -> c [fidelity="summary:medium"]
                c -> exit
            }
            """;
        using var dir = new TempDir();
        var fidelities = new Dictionary<string, string>();
        CodergenResult Capture(Node n, string p, PipelineContext ctx)
        {
            fidelities[n.Id] = ctx.GetString("internal.fidelity");
            return "ok";
        }

        await TestPipelines.Runner(new ScriptedBackend().On("a", Capture).On("b", Capture).On("c", Capture)).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("summary:low", fidelities["a"]);
        Assert.Equal("compact", fidelities["b"]);
        Assert.Equal("summary:medium", fidelities["c"]);
    }

    [Fact]
    public async Task Tool_nodes_run_commands_and_fail_on_non_zero_exit()
    {
        var echo = OperatingSystem.IsWindows() ? "echo hello-%REACTOR_NODE_ID%" : "echo hello-$REACTOR_NODE_ID";
        var source = $$"""
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                say [shape=parallelogram, tool_command="{{echo}}"]
                boom [shape=parallelogram, tool_command="exit 3", goal_gate=true]
                start -> say -> boom -> exit
            }
            """;
        using var dir = new TempDir();

        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("hello-say", result.NodeOutcomes["say"].ContextUpdates["tool.output"]);
        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.Contains("exited with code 3", result.NodeOutcomes["boom"].FailureReason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "say", "stdout.txt")));
    }

    private sealed class RecordingHandler : INodeHandler
    {
        public string? SeenAttribute { get; private set; }

        public Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
        {
            SeenAttribute = node["custom_attr"];
            return Task.FromResult<Outcome?>(Outcome.Success("custom done", new Dictionary<string, object?> { ["custom.ran"] = "yes" }));
        }
    }

    private sealed class SilentHandler : INodeHandler
    {
        public Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken) =>
            Task.FromResult<Outcome?>(null);
    }
}

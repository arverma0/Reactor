using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Tests.Support;

namespace Reactor.Tests.Engine;

public class ResumeAndSupervisorTests
{
    private const string Pipeline = """
        digraph Resume {
            graph [goal="resume me"]
            start [shape=Mdiamond] exit [shape=Msquare]
            a [prompt="a"]
            b [prompt="b", fidelity="full", thread_id="t"]
            c [prompt="c"]
            d [prompt="d"]
            start -> a -> b -> c -> d -> exit
        }
        """;

    private static ScriptedBackend Backend(Func<string, bool>? crashOn = null)
    {
        var backend = new ScriptedBackend();
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            backend.On(id, (node, _, ctx) =>
            {
                if (crashOn?.Invoke(node.Id) == true)
                {
                    throw new OperationCanceledException("simulated crash");
                }

                return Outcome.Success($"{node.Id} done", new Dictionary<string, object?> { [$"out.{node.Id}"] = ctx.GetString("out.a") + node.Id });
            });
        }

        return backend;
    }

    [Fact]
    public async Task Checkpoint_resume_produces_the_same_result_as_an_uninterrupted_run()
    {
        using var dir = new TempDir();
        var reference = await TestPipelines.Runner(Backend()).RunAsync(Pipeline, TestPipelines.Options(dir, "reference"));

        using var cts = new CancellationTokenSource();
        var crashing = new ScriptedBackend();
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            crashing.On(id, (node, _, ctx) =>
            {
                if (node.Id == "c")
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return Outcome.Success($"{node.Id} done", new Dictionary<string, object?> { [$"out.{node.Id}"] = ctx.GetString("out.a") + node.Id });
            });
        }

        var interrupted = await TestPipelines.Runner(crashing).RunAsync(Pipeline, TestPipelines.Options(dir, "resumed"), cts.Token);
        Assert.True(interrupted.Cancelled);
        Assert.Equal(["start", "a", "b"], interrupted.CompletedNodes);

        var promptsSeen = new List<(string, string)>();
        var resumeBackend = Backend();
        var resumed = await TestPipelines.Runner(resumeBackend).ResumeAsync(interrupted.LogsRoot, new RunOptions { Backoff = BackoffConfig.Immediate });

        Assert.Equal(StageStatus.Success, resumed.Status);
        Assert.Equal(reference.CompletedNodes, resumed.CompletedNodes);
        Assert.Equal(reference.Context["out.d"], resumed.Context["out.d"]);
        Assert.Equal(reference.RunId.Length, resumed.RunId.Length);
        Assert.Equal(0, resumeBackend.CallCount("a"));
        Assert.Equal(0, resumeBackend.CallCount("b"));
        Assert.Equal(1, resumeBackend.CallCount("c"));
        Assert.Contains(resumed.Context.Keys, k => k == "graph.goal");
        _ = promptsSeen;
    }

    [Fact]
    public async Task Resume_degrades_full_fidelity_for_one_hop()
    {
        const string source = """
            digraph R {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a", fidelity="full"]
                b [prompt="b", fidelity="full"]
                c [prompt="c", fidelity="full"]
                start -> a -> b -> c -> exit
            }
            """;
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var first = new ScriptedBackend().On("b", (_, _, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var interrupted = await TestPipelines.Runner(first).RunAsync(source, TestPipelines.Options(dir), cts.Token);
        Assert.Equal("full", Checkpoint.Load(Path.Combine(interrupted.LogsRoot, "checkpoint.json")).LastFidelity);

        var fidelities = new Dictionary<string, string>();
        CodergenResult Capture(Node n, string p, PipelineContext ctx)
        {
            fidelities[n.Id] = ctx.GetString("internal.fidelity");
            return "ok";
        }

        await TestPipelines.Runner(new ScriptedBackend().On("b", Capture).On("c", Capture)).ResumeAsync(interrupted.LogsRoot);

        Assert.Equal("summary:high", fidelities["b"]);
        Assert.Equal("full", fidelities["c"]);
    }

    [Fact]
    public async Task Resume_after_failure_reexecutes_the_failed_stage_and_completed_runs_are_noops()
    {
        const string source = """
            digraph R {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a", goal_gate=true]
                start -> a -> exit
            }
            """;
        using var dir = new TempDir();
        var failed = await TestPipelines.Runner(new ScriptedBackend().On("a", Outcome.Fail("flaky infra"))).RunAsync(source, TestPipelines.Options(dir));
        Assert.Equal(StageStatus.Fail, failed.Status);

        var fixedBackend = new ScriptedBackend().On("a", "works now");
        var resumed = await TestPipelines.Runner(fixedBackend).ResumeAsync(failed.LogsRoot);
        Assert.Equal(StageStatus.Success, resumed.Status);
        Assert.Equal(1, fixedBackend.CallCount("a"));

        var again = new ScriptedBackend();
        var noop = await TestPipelines.Runner(again).ResumeAsync(failed.LogsRoot);
        Assert.Equal(StageStatus.Success, noop.Status);
        Assert.Empty(again.Calls);
    }

    [Fact]
    public async Task Manager_loop_supervises_a_child_pipeline()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("child.dot"), """
            digraph Child {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build"]
                test [prompt="test"]
                start -> build -> test -> exit
            }
            """);
        const string parent = """
            digraph Parent {
                graph [stack.child_dotfile="child.dot"]
                start [shape=Mdiamond] exit [shape=Msquare]
                supervise [shape=house, manager.poll_interval="50ms", manager.max_cycles=200]
                start -> supervise -> exit
            }
            """;
        var backend = new ScriptedBackend().On("build", "built").On("test", "tested");

        var result = await TestPipelines.Runner(backend).RunAsync(parent, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal("Child completed", result.NodeOutcomes["supervise"].Notes);
        Assert.Equal("completed", result.Context["context.stack.child.status"]);
        Assert.Equal("success", result.Context["context.stack.child.outcome"]);
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "supervise", "child", "checkpoint.json")));
        Assert.Equal(1, backend.CallCount("test"));
    }

    [Fact]
    public async Task Manager_loop_reports_child_failure_and_stop_conditions()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("child.dot"), """
            digraph Child {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build", goal_gate=true]
                start -> build -> exit
            }
            """);
        const string parent = """
            digraph Parent {
                graph [stack.child_dotfile="child.dot"]
                start [shape=Mdiamond] exit [shape=Msquare]
                supervise [shape=house, manager.poll_interval="20ms", goal_gate=true]
                start -> supervise -> exit
            }
            """;

        var failing = await TestPipelines.Runner(new ScriptedBackend().On("build", Outcome.Fail("compile error")))
            .RunAsync(parent, TestPipelines.Options(dir, "fail"));
        Assert.Equal(StageStatus.Fail, failing.NodeOutcomes["supervise"].Status);
        Assert.Contains("Child failed", failing.NodeOutcomes["supervise"].FailureReason, StringComparison.Ordinal);

        var slow = new ScriptedBackend { Delay = TimeSpan.FromSeconds(30) };
        var stopped = await TestPipelines.Runner(slow).RunAsync(
            parent.Replace("goal_gate=true]", "manager.stop_condition=\"context.stack.child.current_node=build\"]", StringComparison.Ordinal),
            TestPipelines.Options(dir, "stop"));
        Assert.Equal("Stop condition satisfied", stopped.NodeOutcomes["supervise"].Notes);
    }

    [Fact]
    public async Task Spec_integration_smoke_test_with_a_scripted_llm()
    {
        const string dot = """
            digraph test_pipeline {
                graph [goal="Create a hello world Python script"]

                start       [shape=Mdiamond]
                plan        [shape=box, prompt="Plan how to create a hello world script for: $goal"]
                implement   [shape=box, prompt="Write the code based on the plan", goal_gate=true]
                review      [shape=box, prompt="Review the code for correctness"]
                done        [shape=Msquare]

                start -> plan
                plan -> implement
                implement -> review [condition="outcome=success"]
                implement -> plan   [condition="outcome=fail", label="Retry"]
                review -> done      [condition="outcome=success"]
                review -> implement [condition="outcome=fail", label="Fix"]
            }
            """;

        var graph = DotParser.Parse(dot);
        Assert.Equal("Create a hello world Python script", graph.Goal);
        Assert.Equal(5, graph.Nodes.Count);
        Assert.Equal(6, graph.Edges.Count);

        var runner = TestPipelines.Runner(new ScriptedBackend()
            .On("plan", (_, prompt, _) =>
            {
                Assert.Contains("for: Create a hello world Python script", prompt, StringComparison.Ordinal);
                return "1. write hello.py";
            })
            .On("implement", "print('Hello, World!')")
            .On("review", "Looks correct."));
        var prepared = runner.Prepare(graph);
        Assert.False(prepared.HasErrors);

        using var dir = new TempDir();
        var outcome = await runner.RunAsync(graph, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, outcome.Status);
        Assert.Contains("implement", outcome.CompletedNodes);
        foreach (var stage in new[] { "plan", "implement", "review" })
        {
            foreach (var file in new[] { "prompt.md", "response.md", "status.json" })
            {
                Assert.True(File.Exists(Path.Combine(outcome.LogsRoot, stage, file)), $"{stage}/{file}");
            }
        }

        Assert.True(outcome.NodeOutcomes["implement"].IsSuccessful);
        var checkpoint = Checkpoint.Load(Path.Combine(outcome.LogsRoot, "checkpoint.json"));
        Assert.Equal("done", checkpoint.CurrentNode);
        Assert.Contains("plan", checkpoint.CompletedNodes);
        Assert.Contains("implement", checkpoint.CompletedNodes);
        Assert.Contains("review", checkpoint.CompletedNodes);
    }
}

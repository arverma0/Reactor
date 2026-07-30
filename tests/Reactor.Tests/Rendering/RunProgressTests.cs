using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Rendering;
using Reactor.Tests.Support;

namespace Reactor.Tests.Rendering;

public class RunProgressTests
{
    private const string FanOut = """
        digraph live {
            start [shape=Mdiamond] exit [shape=Msquare]
            plan [prompt="plan"]
            fork [shape=component]
            a [prompt="approach a"]
            b [prompt="approach b"]
            join [shape=tripleoctagon]
            start -> plan -> fork
            fork -> a
            fork -> b
            a -> join
            b -> join
            join -> exit
        }
        """;

    [Fact]
    public async Task A_live_run_reports_the_stages_executing_now_not_the_last_checkpointed_one()
    {
        using var dir = new TempDir();
        using var release = new ManualResetEventSlim();
        var aCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ScriptedBackend()
            .On("a", Outcome.PartialSuccess("good enough"))
            .On("b", (_, _, _) =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return "b done";
            });
        var options = TestPipelines.Options(dir) with
        {
            OnEvent = e =>
            {
                if (e is StageCompleted { NodeId: "a" })
                {
                    aCompleted.TrySetResult();
                }
                else if (e is StageStarted { NodeId: "b" })
                {
                    bStarted.TrySetResult();
                }
            },
        };

        var run = TestPipelines.Runner(backend).RunAsync(FanOut, options);
        RunProgress live;
        try
        {
            await Task.WhenAll(aCompleted.Task, bStarted.Task).WaitAsync(TimeSpan.FromSeconds(30));
            live = RunProgress.Load(options.LogsRoot!);
        }
        finally
        {
            release.Set();
        }

        // The checkpoint names the last finished top-level stage; it is done, not running.
        Assert.Equal("plan", Checkpoint.Load(Path.Combine(options.LogsRoot!, Checkpoint.FileName)).CurrentNode);
        Assert.Equal(["fork", "b"], live.Running);
        Assert.Equal("success", live.Statuses["plan"]);
        Assert.Equal("partial_success", live.Statuses["a"]);
        Assert.False(live.Statuses.ContainsKey("b"));
        Assert.False(live.Finished);

        var result = await run;
        var done = RunProgress.Load(result.LogsRoot);
        Assert.Empty(done.Running);
        Assert.True(done.Finished);
        Assert.Equal("success", done.Statuses["b"]);
        Assert.Equal("success", done.Statuses["exit"]);
    }

    [Fact]
    public void Stages_left_open_by_an_interrupted_session_are_not_running_after_a_resume()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("events.jsonl"), string.Join('\n',
            Event("PipelineStarted"),
            Event("StageStarted", "start"),
            Event("StageCompleted", "start", "success"),
            Event("StageStarted", "plan"),
            Event("PipelineStarted"),
            Event("StageStarted", "plan", attempt: 1),
            Event("StageFailed", "plan"),
            Event("StageStarted", "plan", attempt: 2),
            "{\"node_id\":\"plan\",\"type\":\"StageComp"));

        var progress = RunProgress.Load(dir.Path);

        Assert.Equal(["plan"], progress.Running);
        Assert.Equal("success", progress.Statuses["start"]);
        Assert.False(progress.Finished);
    }

    [Fact]
    public void A_run_that_ended_has_nothing_running()
    {
        using var dir = new TempDir();
        File.WriteAllLines(dir.Combine("events.jsonl"),
        [
            Event("PipelineStarted"),
            Event("StageStarted", "start"),
            Event("StageCompleted", "start", "success"),
            Event("StageStarted", "plan"),
            Event("PipelineFailed"),
        ]);

        var progress = RunProgress.Load(dir.Path);

        Assert.Empty(progress.Running);
        Assert.True(progress.Finished);
    }

    [Fact]
    public void Without_events_the_checkpoint_gives_the_statuses_and_nothing_is_running()
    {
        using var dir = new TempDir();
        new Checkpoint
        {
            CurrentNode = "check",
            CompletedNodes = ["start", "plan", "check"],
            NodeOutcomes = { ["start"] = Outcome.Success(), ["plan"] = Outcome.Fail("no") },
        }.Save(dir.Combine(Checkpoint.FileName));

        var progress = RunProgress.Load(dir.Path);

        Assert.Equal("fail", progress.Statuses["plan"]);
        Assert.Equal("skipped", progress.Statuses["check"]);
        Assert.Empty(progress.Running);
        Assert.False(progress.Finished);
    }

    [Fact]
    public void A_directory_that_is_not_a_run_is_reported()
    {
        using var dir = new TempDir();

        Assert.Throws<FileNotFoundException>(() => RunProgress.Load(dir.Path));
        Assert.Throws<DirectoryNotFoundException>(() => RunProgress.Load(dir.Combine("missing")));
    }

    [Fact]
    public void Display_dot_highlights_every_running_stage()
    {
        var graph = DotParser.Parse("digraph g { start [shape=Mdiamond] a b c exit [shape=Msquare] start -> a -> b -> c -> exit }");

        var lines = GraphRenderer.ToDisplayDot(graph, new Dictionary<string, string> { ["start"] = "success", ["a"] = "success" }, ["a", "b"]).Split('\n');

        string NodeLine(string id) => Assert.Single(lines, line => line.Trim() == id || line.TrimStart().StartsWith(id + " [", StringComparison.Ordinal));
        Assert.Contains("fillcolor=\"#bbdefb\"", NodeLine("a"), StringComparison.Ordinal);
        Assert.Contains("penwidth", NodeLine("b"), StringComparison.Ordinal);
        Assert.Contains("fillcolor=\"#c8e6c9\"", NodeLine("start"), StringComparison.Ordinal);
        Assert.DoesNotContain("fillcolor", NodeLine("c"), StringComparison.Ordinal);
    }

    private static string Event(string type, string? node = null, string? status = null, int? attempt = null)
    {
        // Unrelated values, nested objects included, must be skipped without affecting the fields read.
        var json = new JsonObject
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["details"] = new JsonObject { ["type"] = "Nested", ["node_id"] = "nested" },
        };
        if (node is not null)
        {
            json["node_id"] = node;
        }

        if (status is not null)
        {
            json["status"] = status;
        }

        if (attempt is not null)
        {
            json["attempt"] = attempt;
        }

        json["type"] = type;
        return json.ToJsonString();
    }
}

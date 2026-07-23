using Reactor.Events;
using Reactor.Execution;
using Reactor.Tests.Support;

namespace Reactor.Tests.Engine;

public class CheckpointHistoryTests
{
    private const string Linear = """
        digraph Linear {
            start [shape=Mdiamond] exit [shape=Msquare]
            a [prompt="a"]
            b [prompt="b"]
            start -> a -> b -> exit
        }
        """;

    private static string[] SnapshotNames(string logsRoot)
    {
        var dir = Path.Combine(logsRoot, Checkpoint.SnapshotDirectoryName);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    [Fact]
    public async Task Snapshots_are_kept_per_top_level_stage_when_enabled()
    {
        using var dir = new TempDir();
        var result = await TestPipelines.Runner(new ScriptedBackend()).RunAsync(Linear, TestPipelines.Options(dir) with { KeepCheckpointHistory = true });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["0001-start.json", "0002-a.json", "0003-b.json", "0004-exit.json"], SnapshotNames(result.LogsRoot));

        var afterA = Checkpoint.Load(Path.Combine(result.LogsRoot, "checkpoints", "0002-a.json"));
        Assert.Equal("a", afterA.CurrentNode);
        Assert.Equal(["start", "a"], afterA.CompletedNodes);
        Assert.Null(afterA.FinalStatus);
        Assert.Equal(result.RunId, afterA.RunId);
        Assert.Equal(StageStatus.Success, afterA.NodeOutcomes["a"].Status);

        // The final save (with final_status) is not a stage snapshot.
        Assert.Null(Checkpoint.Load(Path.Combine(result.LogsRoot, "checkpoints", "0004-exit.json")).FinalStatus);
        Assert.Equal("success", Checkpoint.Load(Path.Combine(result.LogsRoot, "checkpoint.json")).FinalStatus);
    }

    [Fact]
    public async Task No_snapshots_are_kept_by_default()
    {
        using var dir = new TempDir();
        var result = await TestPipelines.Runner(new ScriptedBackend()).RunAsync(Linear, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.False(Directory.Exists(Path.Combine(result.LogsRoot, "checkpoints")));
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "checkpoint.json")));
    }

    [Fact]
    public async Task Snapshot_indexes_match_stage_indexes_and_skip_parallel_branch_stages()
    {
        const string source = """
            digraph P {
                start [shape=Mdiamond] exit [shape=Msquare]
                fork [shape=component]
                x [prompt="x"]
                y [prompt="y"]
                join [shape=tripleoctagon]
                start -> fork
                fork -> x
                fork -> y
                x -> join
                y -> join
                join -> exit
            }
            """;
        using var dir = new TempDir();
        var events = new List<PipelineEvent>();
        var options = TestPipelines.Options(dir) with
        {
            KeepCheckpointHistory = true,
            OnEvent = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }
            },
        };

        var result = await TestPipelines.Runner(new ScriptedBackend()).RunAsync(source, options);

        Assert.Equal(StageStatus.Success, result.Status);
        var topLevel = events.OfType<StageCompleted>().Where(e => e.NodeId is "start" or "fork" or "join" or "exit")
            .Select(e => Checkpoint.SnapshotName(e.Index, e.NodeId) + ".json").Order(StringComparer.Ordinal);
        Assert.Equal(topLevel, SnapshotNames(result.LogsRoot));
        Assert.DoesNotContain(SnapshotNames(result.LogsRoot), n => n.EndsWith("-x.json", StringComparison.Ordinal) || n.EndsWith("-y.json", StringComparison.Ordinal));
        Assert.Equal(["0001-start.json", "0002-fork.json", "0005-join.json", "0006-exit.json"], SnapshotNames(result.LogsRoot));
    }

    [Fact]
    public async Task A_snapshot_resumes_after_its_stage_in_a_new_run_directory()
    {
        using var dir = new TempDir();
        var first = await TestPipelines.Runner(new ScriptedBackend()).RunAsync(Linear, TestPipelines.Options(dir) with { KeepCheckpointHistory = true });

        var fork = dir.Combine("fork");
        Directory.CreateDirectory(fork);
        File.Copy(Path.Combine(first.LogsRoot, "graph.dot"), Path.Combine(fork, "graph.dot"));
        File.Copy(Path.Combine(first.LogsRoot, "checkpoints", "0002-a.json"), Path.Combine(fork, "checkpoint.json"));

        var backend = new ScriptedBackend();
        var resumed = await TestPipelines.Runner(backend).ResumeAsync(fork, new RunOptions { KeepCheckpointHistory = true, Backoff = BackoffConfig.Immediate });

        Assert.Equal(StageStatus.Success, resumed.Status);
        Assert.Equal(0, backend.CallCount("a"));
        Assert.Equal(1, backend.CallCount("b"));
        Assert.Equal(["start", "a", "b", "exit"], resumed.CompletedNodes);

        // Stage indexes continue after the snapshot's stage.
        Assert.Equal(["0003-b.json", "0004-exit.json"], SnapshotNames(fork));
    }

    [Fact]
    public void Snapshot_names_pad_the_stage_index()
    {
        Assert.Equal("0004-plan", Checkpoint.SnapshotName(4, "plan"));
        Assert.Equal("12345-x", Checkpoint.SnapshotName(12345, "x"));
    }
}

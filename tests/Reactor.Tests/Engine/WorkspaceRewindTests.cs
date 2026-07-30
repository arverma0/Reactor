using System.Security.Cryptography;
using Reactor.Execution;
using Reactor.Human;
using Reactor.Tests.Support;
using Reactor.Workspaces;

namespace Reactor.Tests.Engine;

/// <summary>Workspace snapshots saved with checkpoint snapshots, and rewinding a working directory to one (Re-fly).</summary>
public sealed class WorkspaceRewindTests
{
    private const string WithGate = """
        digraph G {
            start [shape=Mdiamond] exit [shape=Msquare]
            build [prompt="build it"]
            review [shape=hexagon, label="Ship it?"]
            start -> build -> review
            review -> exit [label="[Y] Yes"]
        }
        """;

    private static readonly Dictionary<string, string> Files = new()
    {
        ["seed.txt"] = "seed",
        ["sub/x.txt"] = "x",
        [".gitignore"] = "ignored/\n",
    };

    private static PipelineRunner Runner(Action<string>? atGate = null) => TestPipelines.Runner(
        new ScriptedBackend().On("build", (_, _, context) =>
        {
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "app.txt"), "built");
            return "built";
        }),
        new CallbackInterviewer(question =>
        {
            atGate?.Invoke(question.Stage);
            return Answer.ForOption(question.Options[0]);
        }));

    private static RunOptions Options(string workingDirectory, string logsRoot) => new()
    {
        LogsRoot = logsRoot,
        WorkingDirectory = workingDirectory,
        Backoff = BackoffConfig.Immediate,
        KeepCheckpointHistory = true,
    };

    private static string SidecarPath(string logsRoot, string snapshot) =>
        CheckpointWorkspace.PathFor(Path.Combine(logsRoot, Checkpoint.SnapshotDirectoryName), snapshot);

    private static CheckpointWorkspace Sidecar(string logsRoot, string snapshot) =>
        CheckpointWorkspace.Load(SidecarPath(logsRoot, snapshot)) ?? throw new InvalidOperationException($"No workspace snapshot for {snapshot}.");

    private static byte[] IndexHash(TempRepo repo) => SHA256.HashData(File.ReadAllBytes(repo.Combine(".git", "index")));

    [Fact]
    public async Task Checkpoint_snapshots_record_the_workspace_after_each_top_level_stage_including_gates()
    {
        using var repo = await TempRepo.CreateAsync(Files);

        // The person at the gate edits a file while the run waits: the gate's snapshot must see it.
        var result = await Runner(_ => repo.Write("review-notes.md", "looks good"))
            .RunAsync(WithGate, Options(repo.Path, repo.Combine(".reactor", "runs", "r1")), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        var start = Sidecar(result.LogsRoot, "0001-start");
        var build = Sidecar(result.LogsRoot, "0002-build");
        var gate = Sidecar(result.LogsRoot, "0003-review");
        Assert.Equal(("0002-build", "build", 2, "git"), (build.Checkpoint, build.Node, build.Index, build.Mode));
        Assert.Equal(repo.Path, build.Directory, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("", build.Prefix);
        Assert.Equal($"refs/reactor/runs/{result.RunId}/main", build.Ref);

        // A tracked stage's checkpoint reuses its "after" snapshot; the gate and the start get snapshots of their own.
        Assert.Equal((string?)ChangesFile.Read(result.LogsRoot, "build")["after"]!["commit"], build.Commit);
        Assert.DoesNotContain("app.txt", await repo.GitAsync("ls-tree", "-r", "--name-only", start.Id));
        Assert.Contains("app.txt", await repo.GitAsync("ls-tree", "-r", "--name-only", build.Id));
        Assert.DoesNotContain("review-notes.md", await repo.GitAsync("ls-tree", "-r", "--name-only", build.Id));
        Assert.Contains("review-notes.md", await repo.GitAsync("ls-tree", "-r", "--name-only", gate.Id));
        Assert.Equal(gate.Commit, Sidecar(result.LogsRoot, "0004-exit").Commit);

        // The run's snapshot ref keeps every checkpoint's commit reachable.
        Assert.Equal(gate.Commit, await repo.GitAsync("rev-parse", build.Ref));
        await repo.GitAsync("merge-base", "--is-ancestor", start.Id, gate.Id);
    }

    [Fact]
    public async Task No_workspace_snapshots_are_saved_with_checkpoints_when_tracking_is_off_or_outside_git()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        var ct = TestContext.Current.CancellationToken;

        var untracked = await Runner().RunAsync(WithGate, Options(repo.Path, repo.Combine(".reactor", "runs", "off")) with { TrackWorkspaceChanges = false }, ct);
        var attribute = await Runner().RunAsync(WithGate.Replace("digraph G {", "digraph G { graph [track_changes=false]", StringComparison.Ordinal), Options(repo.Path, repo.Combine(".reactor", "runs", "attr")), ct);
        using var plain = new TempDir();
        var walk = await Runner().RunAsync(WithGate, Options(plain.Path, plain.Combine(".reactor", "runs", "walk")), ct);

        foreach (var result in new[] { untracked, attribute, walk })
        {
            Assert.Equal(StageStatus.Success, result.Status);
            Assert.True(File.Exists(Path.Combine(result.LogsRoot, Checkpoint.SnapshotDirectoryName, "0002-build.json")));
            Assert.Empty(Directory.GetFiles(Path.Combine(result.LogsRoot, Checkpoint.SnapshotDirectoryName), "*" + CheckpointWorkspace.FileSuffix));
        }

        // The file walk still records what each stage changed; it just keeps no contents to rewind to.
        Assert.Equal("walk", (string?)ChangesFile.Read(walk.LogsRoot, "build")["mode"]);
    }

    [Fact]
    public async Task Rewind_restores_a_checkpoint_snapshot_leaves_ignored_files_and_run_directories_alone_and_can_be_undone()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        var ct = TestContext.Current.CancellationToken;

        // The run directory sits inside the work tree (not under .reactor), as with --logs runs/r1.
        var result = await Runner().RunAsync(WithGate, Options(repo.Path, repo.Combine("runs", "r1")), ct);
        Assert.Equal(StageStatus.Success, result.Status);
        repo.Write("app.txt", "edited by hand");
        repo.Write("extra/deep/new.txt", "untracked");
        File.Delete(repo.Combine("seed.txt"));
        repo.Write("ignored/cache.bin", "cache");
        repo.Write("node_modules/pkg/index.js", "module");
        var head = await repo.GitAsync("rev-parse", "HEAD");
        var index = IndexHash(repo);

        var forkDirectory = repo.Combine("runs", "fork");
        var restored = await WorkspaceRewind.RestoreAsync(
            repo.Path, Sidecar(result.LogsRoot, "0001-start"), "fork1", forkDirectory, [repo.Combine("runs")], WorkspaceRewind.MainIndexOf(result.LogsRoot), ct);

        Assert.Equal("seed", repo.Read("seed.txt"));
        Assert.False(File.Exists(repo.Combine("app.txt")));
        Assert.False(Directory.Exists(repo.Combine("extra")));
        Assert.Equal("x", repo.Read("sub/x.txt"));
        Assert.Equal("cache", repo.Read("ignored/cache.bin"));
        Assert.Equal("module", repo.Read("node_modules/pkg/index.js"));
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, Checkpoint.FileName)));
        Assert.Equal((1, 2), (restored.Written, restored.Deleted));
        Assert.Equal(["app.txt", "extra/deep/new.txt", "seed.txt"], restored.Paths);
        Assert.Equal("0001-start", restored.Checkpoint);
        Assert.Equal(head, await repo.GitAsync("rev-parse", "HEAD"));
        Assert.Equal(index, IndexHash(repo));

        // The state before the rewind is a commit on the fork's ref; the documented command brings it back.
        Assert.Equal(restored.UndoCommit, await repo.GitAsync("rev-parse", WorkspaceRewind.UndoRefFor("fork1")));
        Assert.Equal("refs/reactor/runs/fork1/before-rewind", restored.UndoRef);
        Assert.True(File.Exists(WorkspaceRewind.MainIndexOf(forkDirectory)));
        await repo.GitAsync("restore", "--source", restored.UndoCommit, "--worktree", "--", ".");
        Assert.Equal("edited by hand", repo.Read("app.txt"));
        Assert.Equal("untracked", repo.Read("extra/deep/new.txt"));
        Assert.False(File.Exists(repo.Combine("seed.txt")));
        Assert.Equal(head, await repo.GitAsync("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Rewind_refuses_other_directories_missing_snapshots_and_file_walks_without_changing_anything()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        using var other = await TempRepo.CreateAsync(Files);
        using var plain = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var result = await Runner().RunAsync(WithGate, Options(repo.Path, repo.Combine(".reactor", "runs", "r1")), ct);
        var target = Sidecar(result.LogsRoot, "0001-start");
        var forkDirectory = repo.Combine(".reactor", "runs", "fork");

        async Task<string> RefusalAsync(string directory, CheckpointWorkspace snapshot) =>
            (await Assert.ThrowsAsync<WorkspaceRewindException>(() => WorkspaceRewind.RestoreAsync(directory, snapshot, "fork", forkDirectory, [], null, ct))).Code;

        Assert.Equal(WorkspaceRewind.Mismatch, await RefusalAsync(repo.Combine("sub"), target));
        Assert.Equal(WorkspaceRewind.Mismatch, await RefusalAsync(other.Path, target));
        Assert.Equal(WorkspaceRewind.Mismatch, await RefusalAsync(plain.Path, target));
        Assert.Equal(WorkspaceRewind.SnapshotMissing, await RefusalAsync(repo.Path, target with { Commit = null, Tree = new string('1', 40) }));
        Assert.Equal(WorkspaceRewind.Unavailable, await RefusalAsync(repo.Path, target with { Mode = "walk" }));

        Assert.Equal("built", repo.Read("app.txt"));
        Assert.False(Directory.Exists(forkDirectory));
        Assert.Equal("", await repo.GitAsync("for-each-ref", "refs/reactor/runs/fork/"));
    }
}

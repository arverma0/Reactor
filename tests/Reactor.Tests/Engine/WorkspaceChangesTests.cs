using System.Security.Cryptography;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Simulation;
using Reactor.Tests.Support;
using Reactor.Workspaces;

namespace Reactor.Tests.Engine;

/// <summary>Per-stage workspace changes: changes.json, WorkspaceChanged, write_scope, and opting out.</summary>
public sealed class WorkspaceChangesTests
{
    private const string OneStage = """
        digraph W {
            start [shape=Mdiamond] exit [shape=Msquare]
            build [prompt="build it"]
            check [prompt="look only"]
            start -> build -> check -> exit
        }
        """;

    private static readonly Dictionary<string, string> Files = new()
    {
        ["keep.txt"] = "keep",
        ["edit.txt"] = "old",
        ["gone.txt"] = "bye",
        [".gitignore"] = "ignored/\n*.log\n",
        ["app/main.ts"] = "export {};\n",
    };

    private static ScriptedBackend Builder() => new ScriptedBackend().On("build", (_, _, context) =>
    {
        var dir = context.WorkingDirectory;
        File.WriteAllText(Path.Combine(dir, "new.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "edit.txt"), "new content");
        File.Delete(Path.Combine(dir, "gone.txt"));
        Directory.CreateDirectory(Path.Combine(dir, "ignored"));
        File.WriteAllText(Path.Combine(dir, "ignored", "x.txt"), "ignored by .gitignore");
        File.WriteAllText(Path.Combine(dir, "debug.log"), "ignored by .gitignore");
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "pkg", "index.js"), "always left out");
        return "built";
    });

    [Fact]
    public async Task Git_workspace_records_each_stage_without_touching_the_index_or_head()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        repo.Write("notes.md", "uncommitted before the run");
        var head = await repo.GitAsync("rev-parse", "HEAD");
        var index = SHA256.HashData(File.ReadAllBytes(repo.Combine(".git", "index")));
        var events = new List<PipelineEvent>();

        // The run directory sits inside the work tree (not under .reactor), so it must be left out explicitly.
        var result = await TestPipelines.Runner(Builder()).RunAsync(OneStage, new RunOptions
        {
            LogsRoot = repo.Combine("runs", "r1"),
            WorkingDirectory = repo.Path,
            Backoff = BackoffConfig.Immediate,
            OnEvent = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        var build = ChangesFile.Read(result.LogsRoot, "build");
        Assert.Equal("git", (string?)build["mode"]);
        Assert.Equal(1, (int)build["visit"]!);
        Assert.Equal(
            [("edit.txt", "modified"), ("gone.txt", "deleted"), ("new.txt", "added")],
            ChangesFile.Entries(build));
        var added = build["changes"]!.AsArray().Single(c => (string?)c!["path"] == "new.txt")!;
        Assert.Equal(5L, (long)added["size"]!);
        Assert.Equal(40, ((string)added["blob"]!).Length);
        var modified = build["changes"]!.AsArray().Single(c => (string?)c!["path"] == "edit.txt")!;
        Assert.Equal(3L, (long)modified["old_size"]!);
        Assert.False((bool)build["concurrent"]!);
        Assert.False((bool)build["truncated"]!);
        var after = (string)build["after"]!["commit"]!;
        Assert.NotEqual((string)build["before"]!["commit"]!, after);

        // The snapshots are commits on the run's ref; the untracked notes.md was part of the starting state.
        var refName = (string)build["ref"]!;
        Assert.Equal($"refs/reactor/runs/{result.RunId}/main", refName);
        var check = ChangesFile.Read(result.LogsRoot, "check");
        Assert.Empty(check["changes"]!.AsArray());
        Assert.Equal(after, (string)check["before"]!["commit"]!);
        Assert.Equal(after, await repo.GitAsync("rev-parse", refName));
        Assert.Contains("notes.md", await repo.GitAsync("ls-tree", "-r", "--name-only", after));

        // The user's HEAD and index are untouched.
        Assert.Equal(head, await repo.GitAsync("rev-parse", "HEAD"));
        Assert.Equal(index, SHA256.HashData(File.ReadAllBytes(repo.Combine(".git", "index"))));

        var changed = Assert.Single(events.OfType<WorkspaceChanged>());
        Assert.Equal(("build", 1, 1, 1), (changed.NodeId, changed.Added, changed.Modified, changed.Deleted));
        Assert.Equal(["edit.txt", "gone.txt", "new.txt"], changed.Sample);
        Assert.True(events.IndexOf(changed) > events.FindIndex(e => e is StageCompleted { NodeId: "build" }));
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, WorkspaceTracker.RunFileName)));
    }

    [Fact]
    public async Task Working_directory_in_a_subdirectory_records_only_that_subdirectory()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        var backend = new ScriptedBackend().On("build", (_, _, context) =>
        {
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "util.ts"), "export const x = 1;\n");
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "..", "outside.txt"), "not in the workspace");
            return "ok";
        });

        var result = await TestPipelines.Runner(backend).RunAsync(OneStage, new RunOptions
        {
            LogsRoot = repo.Combine(".reactor", "runs", "r1"),
            WorkingDirectory = repo.Combine("app"),
            Backoff = BackoffConfig.Immediate,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal([("util.ts", "added")], ChangesFile.Entries(ChangesFile.Read(result.LogsRoot, "build")));
    }

    [Fact]
    public async Task File_walk_outside_git_hashes_changes_and_archives_changes_json_per_visit()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("edit.txt"), "old");
        File.WriteAllText(dir.Combine("gone.txt"), "bye");
        var visits = 0;
        var backend = new ScriptedBackend().On("build", (_, _, context) =>
        {
            var root = context.WorkingDirectory;
            if (++visits == 1)
            {
                File.WriteAllText(Path.Combine(root, "edit.txt"), "new");
                File.Delete(Path.Combine(root, "gone.txt"));
                Directory.CreateDirectory(Path.Combine(root, "node_modules"));
                File.WriteAllText(Path.Combine(root, "node_modules", "skip.js"), "skipped");
                return Outcome.Success("first", new Dictionary<string, object?> { ["again"] = "yes" });
            }

            File.WriteAllText(Path.Combine(root, "second.txt"), "2");
            return Outcome.Success("second", new Dictionary<string, object?> { ["again"] = "no" });
        });
        const string Loop = """
            digraph L {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build"]
                start -> build
                build -> build [condition="context.again=yes"]
                build -> exit [condition="context.again=no"]
            }
            """;

        var result = await TestPipelines.Runner(backend).RunAsync(Loop, TestPipelines.Options(dir), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        var first = ChangesFile.Read(result.LogsRoot, "build", "visit-1");
        Assert.Equal("walk", (string?)first["mode"]);
        Assert.Equal([("edit.txt", "modified"), ("gone.txt", "deleted")], ChangesFile.Entries(first));
        Assert.Equal(64, ((string)first["changes"]![0]!["sha256"]!).Length);
        Assert.StartsWith("walk:", (string)first["after"]!["id"]!, StringComparison.Ordinal);
        var second = ChangesFile.Read(result.LogsRoot, "build");
        Assert.Equal(2, (int)second["visit"]!);
        Assert.Equal([("second.txt", "added")], ChangesFile.Entries(second));
        Assert.Equal((string)first["after"]!["id"]!, (string)second["before"]!["id"]!);
    }

    [Fact]
    public async Task Write_scope_lists_violations_and_warns()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("build", (_, _, context) =>
        {
            Directory.CreateDirectory(Path.Combine(context.WorkingDirectory, "src"));
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "src", "a.ts"), "in scope");
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "README.md"), "out of scope");
            return "ok";
        });
        var source = OneStage.Replace("build [prompt=\"build it\"]", "build [prompt=\"build it\", write_scope=\"src/**\"]", StringComparison.Ordinal);
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        var scope = ChangesFile.Read(result.LogsRoot, "build")["write_scope"]!;
        Assert.Equal(["README.md"], scope["violations"]!.AsArray().Select(v => (string)v!));
        Assert.False((bool)scope["enforced"]!);
        Assert.Contains(events.OfType<StageLog>(), l => l is { NodeId: "build", Level: "warn" } && l.Message.Contains("README.md", StringComparison.Ordinal));
        Assert.Equal(["README.md"], Assert.Single(events.OfType<WorkspaceChanged>()).OutOfScope);
    }

    [Fact]
    public async Task Write_scope_enforce_fails_the_stage_once_without_retrying()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("build", (_, _, context) =>
        {
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "README.md"), "out of scope");
            return Outcome.Success("done", new Dictionary<string, object?> { ["built"] = true }) with { PreferredLabel = "Ship" };
        });
        const string Source = """
            digraph W {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build", write_scope="prototypes/proto_a/**", write_scope_enforce=true, max_retries=2]
                start -> build
                build -> exit [label="Ship", condition="outcome=success"]
            }
            """;
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(Source, TestPipelines.Options(dir) with { OnEvent = events.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(1, backend.CallCount("build"));
        var outcome = result.NodeOutcomes["build"];
        Assert.Equal(StageStatus.Fail, outcome.Status);
        Assert.Contains("outside write_scope \"prototypes/proto_a/**\": README.md", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Equal("", outcome.PreferredLabel);
        Assert.Equal(StageStatus.Fail, result.Status);

        // Observers see one completion for the visit, already marked failed.
        var completed = Assert.Single(events.OfType<StageCompleted>(), e => e.NodeId == "build");
        Assert.Equal("fail", completed.Status);
        Assert.True(events.FindIndex(e => e is StageFailed { NodeId: "build", WillRetry: false }) < events.IndexOf(completed));
        Assert.Contains("\"outcome\": \"fail\"", File.ReadAllText(Path.Combine(result.LogsRoot, "build", "status.json")), StringComparison.Ordinal);
        Assert.True((bool)ChangesFile.Read(result.LogsRoot, "build")["write_scope"]!["enforced"]!);
    }

    [Fact]
    public async Task Track_changes_false_simulations_and_in_memory_runs_take_no_snapshots()
    {
        using var dir = new TempDir();
        var off = OneStage.Replace("digraph W {", "digraph W {\n    graph [track_changes=false]", StringComparison.Ordinal);
        var optedOut = await TestPipelines.Runner(Builder()).RunAsync(off, TestPipelines.Options(dir, "off"), TestContext.Current.CancellationToken);
        var simulated = await TestPipelines.Runner().RunAsync(OneStage, TestPipelines.Options(dir, "sim"), TestContext.Current.CancellationToken);
        var runOption = await TestPipelines.Runner(Builder()).RunAsync(OneStage, TestPipelines.Options(dir, "opt") with { TrackWorkspaceChanges = false }, TestContext.Current.CancellationToken);

        foreach (var run in new[] { optedOut, simulated, runOption })
        {
            Assert.Equal(StageStatus.Success, run.Status);
            Assert.False(File.Exists(Path.Combine(run.LogsRoot, "build", WorkspaceTracker.ChangesFileName)), run.LogsRoot);
            Assert.False(Directory.Exists(Path.Combine(run.LogsRoot, WorkspaceTracker.StateDirectoryName)), run.LogsRoot);
        }

        // A node can opt back in.
        var nodeOn = off.Replace("build [prompt=\"build it\"]", "build [prompt=\"build it\", track_changes=true]", StringComparison.Ordinal);
        var tracked = await TestPipelines.Runner(Builder()).RunAsync(nodeOn, TestPipelines.Options(dir, "node"), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(tracked.LogsRoot, "build", WorkspaceTracker.ChangesFileName)));
        Assert.False(File.Exists(Path.Combine(tracked.LogsRoot, "check", WorkspaceTracker.ChangesFileName)));

        var memory = new PipelineRunner(new PipelineRunnerOptions { Backend = Builder(), PersistRunState = false, WriteEventLog = false });
        using var memoryDir = new TempDir();
        var inMemory = await memory.RunAsync(OneStage, TestPipelines.Options(memoryDir), TestContext.Current.CancellationToken);
        Assert.Equal(StageStatus.Success, inMemory.Status);
        Assert.False(File.Exists(Path.Combine(memoryDir.Path, "run", "build", WorkspaceTracker.ChangesFileName)));
        Assert.False(Directory.Exists(Path.Combine(memoryDir.Path, "run", WorkspaceTracker.StateDirectoryName)));
    }

    [Fact]
    public async Task A_real_workspace_without_a_backend_tracks_tool_stages_and_isolates_branches()
    {
        using var dir = new TempDir();
        const string Source = """
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                make [shape=parallelogram, tool_command="echo made> made.txt"]
                fork [shape=component, branch_workdir="copy"] join [shape=tripleoctagon]
                a [shape=parallelogram, tool_command="echo a> branch.txt"]
                b [shape=parallelogram, tool_command="echo b> branch.txt"]
                start -> make -> fork
                fork -> a -> join
                fork -> b -> join
                join -> exit
            }
            """;
        var runner = new PipelineRunner(new PipelineRunnerOptions
        {
            LogsBaseDirectory = dir.Combine("runs"),
            RealWorkspace = true,
        });

        var result = await runner.RunAsync(Source, TestPipelines.Options(dir), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal([("made.txt", "added")], ChangesFile.Entries(ChangesFile.Read(result.LogsRoot, "make")));
        Assert.False(File.Exists(dir.Combine("branch.txt")));

        // Simulation runners never touch the working directory, whatever the options they are given say.
        using var simDir = new TempDir();
        var sample = new SimulationSample(SimulationScenario.Empty, seed: 1);
        var simulated = await sample.CreateRunner(runner.Options).RunAsync(Source, TestPipelines.Options(simDir), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(simulated.LogsRoot, "make", WorkspaceTracker.ChangesFileName)));
        Assert.False(Directory.Exists(Path.Combine(simulated.LogsRoot, WorkspaceTracker.StateDirectoryName)));
    }

    [Fact]
    public async Task Nested_repository_without_commits_is_skipped_and_the_rest_is_recorded()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        var backend = new ScriptedBackend().On("build", (_, _, context) =>
        {
            var nested = Path.Combine(context.WorkingDirectory, "proto");
            Directory.CreateDirectory(nested);
            GitCli.RunCheckedAsync(nested, ["init", "-q"]).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(nested, "x.txt"), "inside a repository without commits");
            File.WriteAllText(Path.Combine(context.WorkingDirectory, "new.txt"), "recorded");
            return "ok";
        });

        var result = await TestPipelines.Runner(backend).RunAsync(OneStage, new RunOptions
        {
            LogsRoot = repo.Combine(".reactor", "runs", "r1"),
            WorkingDirectory = repo.Path,
            Backoff = BackoffConfig.Immediate,
        }, TestContext.Current.CancellationToken);

        var build = ChangesFile.Read(result.LogsRoot, "build");
        Assert.Equal([("new.txt", "added")], ChangesFile.Entries(build));
        Assert.Contains("proto/", build["skipped"]!.AsArray().Select(p => (string)p!));
    }

    [Theory]
    [InlineData("src/**", "src/a/b.ts", true)]
    [InlineData("src/**", "src", false)]
    [InlineData("src/**", "README.md", false)]
    [InlineData("prototypes/proto_a", "prototypes/proto_a/index.html", true)]
    [InlineData("prototypes/proto_a", "prototypes/proto_ab/index.html", false)]
    [InlineData("docs/", "docs/x/y.md", true)]
    [InlineData("**/*.md", "a/b/c.md", true)]
    [InlineData("**/*.md", "c.md", true)]
    [InlineData("*.md", "a/c.md", false)]
    [InlineData("a/**/z.txt", "a/z.txt", true)]
    [InlineData("a/**/z.txt", "a/b/c/z.txt", true)]
    [InlineData(".\\src\\*.ts", "src/x.ts", true)]
    public void Write_scope_globs(string pattern, string path, bool allowed) =>
        Assert.Equal(allowed, WriteScope.Parse(pattern)!.Allows(path));

    [Fact]
    public void A_truncated_walk_only_compares_the_paths_both_snapshots_covered()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(dir.Combine($"f{i}.txt"), "v1");
        }

        var walker = new WalkSnapshotter(dir.Path, fileLimit: 5);
        var before = walker.Walk(TestContext.Current.CancellationToken);
        File.WriteAllText(dir.Combine("f1.txt"), "changed");
        File.WriteAllText(dir.Combine("f8.txt"), "changed beyond the limit");
        var after = walker.Walk(TestContext.Current.CancellationToken);

        Assert.True(before.Truncated);
        var changes = WalkSnapshotter.Diff(before, after);
        Assert.True(changes.Truncated);
        Assert.Equal(["f1.txt"], changes.Changes.Select(c => c.Path));
    }
}

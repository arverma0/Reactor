using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Tests.Support;
using Reactor.Validation;
using Reactor.Workspaces;

namespace Reactor.Tests.Engine;

/// <summary>Parallel branch workspaces (branch_workdir), promotion, cleanup, and concurrency groups.</summary>
public sealed class WorkspaceBranchTests
{
    private const string BakeOff = """
        digraph B {
            start [shape=Mdiamond] exit [shape=Msquare]
            fork [shape=component, branch_workdir="worktree"]
            a [prompt="build prototype a"]
            b [prompt="build prototype b"]
            join [shape=tripleoctagon, promote="best"]
            after [prompt="continue with the winner"]
            start -> fork
            fork -> a
            fork -> b
            a -> join
            b -> join
            join -> after -> exit
        }
        """;

    private static readonly Dictionary<string, string> Files = new()
    {
        ["shared.txt"] = "base",
        ["old.txt"] = "to be deleted by b",
        [".gitignore"] = "dist/\n",
    };

    private static RunOptions Options(string workdir, string logsRoot) => new()
    {
        LogsRoot = logsRoot,
        WorkingDirectory = workdir,
        Backoff = BackoffConfig.Immediate,
        MaxSteps = 100,
    };

    /// <summary>A branch stage that writes <c>shared.txt</c> and its own file, and reports a score.</summary>
    private static Func<Node, string, PipelineContext, CodergenResult> Prototype(string name, double score, Dictionary<string, string> seen, bool deleteOld = false) =>
        (node, _, context) =>
        {
            var dir = context.WorkingDirectory;
            lock (seen)
            {
                seen[name] = dir;
            }

            Assert.True(File.Exists(Path.Combine(dir, "draft.md")), "branches start from the current state, uncommitted files included");
            Assert.False(Directory.Exists(Path.Combine(dir, "dist")), "ignored files are not part of the fork");
            File.WriteAllText(Path.Combine(dir, "shared.txt"), name.ToUpperInvariant());
            File.WriteAllText(Path.Combine(dir, name + ".txt"), "from " + name);
            if (deleteOld)
            {
                File.Delete(Path.Combine(dir, "old.txt"));
            }

            Thread.Sleep(100);
            Assert.Equal(name.ToUpperInvariant(), File.ReadAllText(Path.Combine(dir, "shared.txt")));
            return Outcome.Success("built " + name, new Dictionary<string, object?> { ["score"] = score });
        };

    [Fact]
    public async Task Worktree_branches_are_isolated_and_the_best_one_is_promoted_then_cleaned_up()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        repo.Write("draft.md", "uncommitted");
        repo.Write("dist/bundle.js", "ignored build output");
        var seen = new Dictionary<string, string>();
        var backend = new ScriptedBackend()
            .On("a", Prototype("a", 0.2, seen))
            .On("b", Prototype("b", 0.9, seen, deleteOld: true))
            .On("after", (_, _, context) =>
            {
                Assert.Equal("B", File.ReadAllText(Path.Combine(context.WorkingDirectory, "shared.txt")));
                return "continued";
            });
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(BakeOff, Options(repo.Path, repo.Combine(".reactor", "runs", "r1")) with
        {
            OnEvent = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        var branchRoot = Path.Combine(result.LogsRoot, "branches", "fork");
        Assert.Equal(TempRepoPath(Path.Combine(branchRoot, "a")), TempRepoPath(seen["a"]));
        Assert.Equal(TempRepoPath(Path.Combine(branchRoot, "b")), TempRepoPath(seen["b"]));

        // The winner's changes (relative to the fork) are in the main workspace; the loser's are not.
        Assert.Equal("B", repo.Read("shared.txt"));
        Assert.Equal("from b", repo.Read("b.txt"));
        Assert.False(File.Exists(repo.Combine("a.txt")));
        Assert.False(File.Exists(repo.Combine("old.txt")));
        Assert.Equal("uncommitted", repo.Read("draft.md"));
        Assert.Equal("b", result.Context["parallel.fan_in.best_id"]);
        Assert.Equal("b", result.Context["parallel.fan_in.promoted"]);
        Assert.Equal(TempRepoPath(seen["b"]), TempRepoPath((string)result.Context["parallel.fan_in.best_workdir"]!));

        var results = Assert.IsType<List<object?>>(result.Context["parallel.results"]);
        var b = results.Cast<Dictionary<string, object?>>().Single(r => (string?)r["id"] == "b");
        Assert.Equal("worktree", b["workdir_mode"]);
        Assert.Equal(40, ((string)b["base_snapshot"]!).Length);
        Assert.Equal(40, ((string)b["tree"]!).Length);
        var bChanges = Assert.IsType<Dictionary<string, object?>>(b["changes"]);
        Assert.Equal((1L, 1L, 1L), ((long)bChanges["added"]!, (long)bChanges["modified"]!, (long)bChanges["deleted"]!));

        // Branch stages record their own workspace; the fan-in records what it promoted.
        var aChanges = ChangesFile.Read(result.LogsRoot, "a");
        Assert.Equal([("a.txt", "added"), ("shared.txt", "modified")], ChangesFile.Entries(aChanges));
        Assert.Equal("branches/fork/a", (string?)aChanges["workspace_label"]);
        var join = ChangesFile.Read(result.LogsRoot, "join");
        Assert.Equal([("b.txt", "added"), ("old.txt", "deleted"), ("shared.txt", "modified")], ChangesFile.Entries(join));
        Assert.Equal("b", (string?)join["promotion"]!["branch"]);
        Assert.Equal(3, (int)join["promotion"]!["files"]!);
        var promoted = Assert.Single(events.OfType<WorkspacePromoted>());
        Assert.Equal(("join", "b", 3), (promoted.NodeId, promoted.Branch, promoted.Files));

        // Worktrees are removed when the run ends; their snapshots stay on the run's refs.
        Assert.Single(await repo.WorktreesAsync());
        Assert.False(Directory.Exists(Path.Combine(branchRoot, "a")));
        var refs = await repo.GitAsync("for-each-ref", "--format=%(refname)", "refs/reactor/");
        Assert.Contains($"refs/reactor/runs/{result.RunId}/branches/fork/a", refs, StringComparison.Ordinal);
        Assert.Contains($"refs/reactor/runs/{result.RunId}/main", refs, StringComparison.Ordinal);
        Assert.Equal("B", await repo.GitAsync("show", $"refs/reactor/runs/{result.RunId}/branches/fork/b:shared.txt"));

        // The user's branch and index were never touched.
        Assert.Equal("", await repo.GitAsync("diff", "--cached", "--name-only"));
        var owner = JsonNode.Parse(File.ReadAllText(Path.Combine(result.LogsRoot, WorkspaceTracker.StateDirectoryName, "owner.json")))!;
        Assert.True((bool)owner["finished"]!);
    }

    [Fact]
    public async Task Promotion_fails_without_changing_anything_when_the_main_workspace_changed_the_same_file()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        repo.Write("draft.md", "uncommitted");
        var seen = new Dictionary<string, string>();
        var main = repo.Path;
        var winner = Prototype("b", 0.9, seen);
        var backend = new ScriptedBackend()
            .On("a", Prototype("a", 0.1, seen))
            .On("b", (node, prompt, context) =>
            {
                // A misbehaving agent writes into the main workspace by absolute path.
                File.WriteAllText(Path.Combine(main, "shared.txt"), "edited in main meanwhile");
                return winner(node, prompt, context);
            });

        var result = await TestPipelines.Runner(backend).RunAsync(BakeOff, Options(repo.Path, repo.Combine(".reactor", "runs", "r1")), TestContext.Current.CancellationToken);

        var join = result.NodeOutcomes["join"];
        Assert.Equal(StageStatus.Fail, join.Status);
        Assert.Contains("Cannot promote branch b", join.FailureReason, StringComparison.Ordinal);
        Assert.Contains("shared.txt", join.FailureReason, StringComparison.Ordinal);
        Assert.Equal("edited in main meanwhile", repo.Read("shared.txt"));
        Assert.False(File.Exists(repo.Combine("b.txt")), "nothing is applied when a path conflicts");
        Assert.Equal(["shared.txt"], ChangesFile.Read(result.LogsRoot, "join")["promotion"]!["conflicts"]!.AsArray().Select(c => (string)c!));
        Assert.Empty(ChangesFile.Read(result.LogsRoot, "join")["changes"]!.AsArray());
        Assert.Single(await repo.WorktreesAsync());

        // The write outside branch b's workspace is caught when the branches finish, and not blamed on the fan-in.
        var fork = ChangesFile.Read(result.LogsRoot, "fork");
        Assert.Equal([("shared.txt", "modified")], ChangesFile.Entries(fork));
        Assert.Contains("while the parallel branches worked in their own workspaces", (string)fork["note"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_mode_works_outside_git_and_promotes_the_best_copy()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("shared.txt"), "base");
        File.WriteAllText(dir.Combine("old.txt"), "to be deleted by b");
        File.WriteAllText(dir.Combine("draft.md"), "draft");
        Directory.CreateDirectory(dir.Combine("node_modules", "x"));
        File.WriteAllText(dir.Combine("node_modules", "x", "index.js"), "not copied");
        var seen = new Dictionary<string, string>();
        var backend = new ScriptedBackend()
            .On("a", Prototype("a", 0.2, seen))
            .On("b", (node, prompt, context) =>
            {
                Assert.False(Directory.Exists(Path.Combine(context.WorkingDirectory, "node_modules")));
                return Prototype("b", 0.9, seen, deleteOld: true)(node, prompt, context);
            });
        var source = BakeOff.Replace("branch_workdir=\"worktree\"", "branch_workdir=\"copy\"", StringComparison.Ordinal);

        var result = await TestPipelines.Runner(backend).RunAsync(source, Options(dir.Path, dir.Combine("run")), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal("B", File.ReadAllText(dir.Combine("shared.txt")));
        Assert.True(File.Exists(dir.Combine("b.txt")));
        Assert.False(File.Exists(dir.Combine("a.txt")));
        Assert.False(File.Exists(dir.Combine("old.txt")));
        var b = Assert.IsType<List<object?>>(result.Context["parallel.results"]).Cast<Dictionary<string, object?>>().Single(r => (string?)r["id"] == "b");
        Assert.Equal("copy", b["workdir_mode"]);
        Assert.StartsWith("walk:", (string)b["snapshot"]!, StringComparison.Ordinal);
        Assert.Equal("walk", (string?)ChangesFile.Read(result.LogsRoot, "a")["mode"]);

        // Copies have no other record, so they stay in the run directory.
        Assert.Equal("A", File.ReadAllText(Path.Combine(seen["a"], "shared.txt")));
    }

    [Fact]
    public async Task Copy_mode_in_git_leaves_ignored_build_output_out_of_changes_scope_and_promotion()
    {
        using var repo = await TempRepo.CreateAsync(new Dictionary<string, string> { ["src/app.js"] = "v1", [".gitignore"] = "dist/\n*.log\n" });
        repo.Write("dist/bundle.js", "main's own build");
        var backend = new ScriptedBackend().On("a", (_, _, context) =>
        {
            var dir = context.WorkingDirectory;
            Assert.False(File.Exists(Path.Combine(dir, "dist", "bundle.js")), "ignored files are not copied");
            File.WriteAllText(Path.Combine(dir, "src", "app.js"), "v2");
            Directory.CreateDirectory(Path.Combine(dir, "dist"));
            File.WriteAllText(Path.Combine(dir, "dist", "bundle.js"), "branch build of v2");
            File.WriteAllText(Path.Combine(dir, "build.log"), "log");
            return Outcome.Success("built", new Dictionary<string, object?> { ["score"] = 1.0 });
        });
        const string Source = """
            digraph B {
                start [shape=Mdiamond] exit [shape=Msquare]
                fork [shape=component, branch_workdir="copy"]
                a [prompt="build a", write_scope="src/**", write_scope_enforce=true]
                b [prompt="look only"]
                join [shape=tripleoctagon, promote="best"]
                start -> fork
                fork -> a -> join
                fork -> b -> join
                join -> exit
            }
            """;

        var result = await TestPipelines.Runner(backend).RunAsync(Source, Options(repo.Path, repo.Combine(".reactor", "runs", "r1")), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal([("src/app.js", "modified")], ChangesFile.Entries(ChangesFile.Read(result.LogsRoot, "a")));
        Assert.Equal("v2", repo.Read("src/app.js"));
        Assert.Equal("main's own build", repo.Read("dist/bundle.js"));
        Assert.False(File.Exists(repo.Combine("build.log")));
    }

    [Fact]
    public async Task A_resumed_copy_promotion_never_deletes_ignored_files_an_unfiltered_fork_copied()
    {
        using var repo = await TempRepo.CreateAsync(new Dictionary<string, string> { ["src/app.js"] = "v1", [".gitignore"] = ".env\n" });
        repo.Write(".env", "SECRET=1");
        const string Source = """
            digraph B {
                start [shape=Mdiamond] exit [shape=Msquare]
                fork [shape=component, branch_workdir="copy"]
                a [prompt="build a"]
                b [prompt="look only"]
                join [shape=tripleoctagon, prompt="Pick the best", promote="best"]
                start -> fork
                fork -> a -> join
                fork -> b -> join
                join -> exit
            }
            """;
        using var cts = new CancellationTokenSource();
        var first = new ScriptedBackend()
            .On("a", (_, _, context) =>
            {
                File.WriteAllText(Path.Combine(context.WorkingDirectory, "src", "app.js"), "v2");
                return "built";
            })
            .On("join", (_, _, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        var logs = repo.Combine(".reactor", "runs", "r1");
        var interrupted = await TestPipelines.Runner(first).RunAsync(Source, Options(repo.Path, logs), cts.Token);
        Assert.True(interrupted.Cancelled);

        // What a fork whose git snapshot failed leaves behind: a copy (and a saved starting manifest) that includes ignored files.
        var copy = Path.Combine(logs, "branches", "fork", "a");
        File.WriteAllText(Path.Combine(copy, ".env"), "SECRET=1");
        var basePath = Path.Combine(logs, WorkspaceTracker.StateDirectoryName, "branches.fork.a.base.json");
        var manifest = JsonNode.Parse(File.ReadAllText(basePath))!;
        manifest["files"]![".env"] = new JsonArray(8L, Convert.ToHexStringLower(SHA256.HashData("SECRET=1"u8)));
        File.WriteAllText(basePath, manifest.ToJsonString());

        var resumed = await TestPipelines.Runner(new ScriptedBackend().On("join", "BEST: a")).ResumeAsync(logs, Options(repo.Path, logs));

        Assert.True(resumed.Succeeded, resumed.FailureReason);
        Assert.Equal("v2", repo.Read("src/app.js"));
        Assert.Equal("SECRET=1", repo.Read(".env"));
    }

    [Fact]
    public async Task Worktree_mode_outside_git_fails_the_parallel_node_with_a_clear_reason()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend();

        var result = await TestPipelines.Runner(backend).RunAsync(BakeOff, Options(dir.Path, dir.Combine("run")), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.Contains("branch_workdir=\"worktree\" needs a git work tree", result.NodeOutcomes["fork"].FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, backend.CallCount("a"));
    }

    [Fact]
    public async Task Simulations_never_create_branch_workspaces_or_snapshots()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        repo.Write("draft.md", "uncommitted");

        var result = await TestPipelines.Runner().RunAsync(BakeOff, Options(repo.Path, repo.Combine(".reactor", "runs", "sim")), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.False(Directory.Exists(Path.Combine(result.LogsRoot, "branches")));
        Assert.False(Directory.Exists(Path.Combine(result.LogsRoot, WorkspaceTracker.StateDirectoryName)));
        Assert.Single(await repo.WorktreesAsync());
        Assert.Equal("", await repo.GitAsync("for-each-ref", "refs/reactor/"));
        Assert.Equal("base", repo.Read("shared.txt"));
    }

    [Fact]
    public async Task Tools_and_artifacts_in_a_branch_resolve_against_the_branch_workspace()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        repo.Write("draft.md", "uncommitted");
        var print = OperatingSystem.IsWindows() ? "cd" : "pwd";
        var source = $$"""
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                fork [shape=component, branch_workdir="worktree"]
                where [shape=parallelogram, tool_command="{{print}}"]
                write [shape=parallelogram, tool_command="echo report> report.txt", outputs="report:report.txt"]
                other [prompt="other branch"]
                join [shape=tripleoctagon]
                start -> fork
                fork -> where -> write -> join
                fork -> other -> join
                join -> exit
            }
            """;

        var result = await TestPipelines.Runner(new ScriptedBackend()).RunAsync(source, Options(repo.Path, repo.Combine(".reactor", "runs", "r1")), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        var where = Assert.IsType<List<object?>>(result.Context["parallel.results"]).Cast<Dictionary<string, object?>>().Single(r => (string?)r["id"] == "where");
        var workdir = (string)where["workdir"]!;
        Assert.Equal(TempRepoPath(Path.Combine(result.LogsRoot, "branches", "fork", "where")), TempRepoPath(workdir));
        Assert.Equal(TempRepoPath(workdir), TempRepoPath(File.ReadAllText(Path.Combine(result.LogsRoot, "where", "stdout.txt")).Trim()));
        Assert.Equal([("report.txt", "added")], ChangesFile.Entries(ChangesFile.Read(result.LogsRoot, "write")));
        Assert.False(File.Exists(repo.Combine("report.txt")), "the tool wrote into its branch, not the main workspace");
        var report = Assert.IsType<Dictionary<string, object?>>(result.Context["artifacts.report"]);
        Assert.Equal(64, ((string)report["sha256"]!).Length);
    }

    [Fact]
    public async Task Concurrency_group_runs_stages_one_at_a_time_across_branches()
    {
        using var dir = new TempDir();
        const string Source = """
            digraph Q {
                start [shape=Mdiamond] exit [shape=Msquare]
                fork [shape=component, max_parallel=3]
                a [prompt="qa a", concurrency_group="chrome"]
                b [prompt="qa b", concurrency_group="chrome"]
                c [prompt="qa c", concurrency_group="chrome"]
                join [shape=tripleoctagon]
                start -> fork
                fork -> a -> join
                fork -> b -> join
                fork -> c -> join
                join -> exit
            }
            """;
        var active = 0;
        var peak = 0;
        CodergenResult Exclusive(Node node, string prompt, PipelineContext context)
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref active);
            return "checked";
        }

        var backend = new ScriptedBackend().On("a", Exclusive).On("b", Exclusive).On("c", Exclusive);
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(Source, TestPipelines.Options(dir) with
        {
            OnEvent = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(1, peak);
        Assert.Contains(events.OfType<StageLog>(), l => l.Message.Contains("waiting for concurrency_group 'chrome'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stages_that_overlap_in_a_shared_workspace_are_marked_concurrent_and_not_blamed_for_each_other()
    {
        using var dir = new TempDir();
        const string Source = """
            digraph S {
                start [shape=Mdiamond] exit [shape=Msquare]
                fork [shape=component, branch_workdir="shared"]
                a [prompt="a", write_scope="a/**", write_scope_enforce=true]
                b [prompt="b", write_scope="b/**", write_scope_enforce=true]
                join [shape=tripleoctagon]
                start -> fork
                fork -> a -> join
                fork -> b -> join
                join -> exit
            }
            """;
        using var barrier = new Barrier(2);
        CodergenResult Write(Node node, string prompt, PipelineContext context)
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            Directory.CreateDirectory(Path.Combine(context.WorkingDirectory, node.Id));
            File.WriteAllText(Path.Combine(context.WorkingDirectory, node.Id, "out.txt"), node.Id);
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return "ok";
        }

        var result = await TestPipelines.Runner(new ScriptedBackend().On("a", Write).On("b", Write)).RunAsync(Source, TestPipelines.Options(dir), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["a"].Status);
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["b"].Status);
        var a = ChangesFile.Read(result.LogsRoot, "a");
        Assert.True((bool)a["concurrent"]!);
        Assert.Equal(["b"], a["concurrent_with"]!.AsArray().Select(n => (string)n!));
        Assert.Empty(a["write_scope"]!["violations"]!.AsArray());
        Assert.Equal(["b/out.txt"], a["write_scope"]!["attributed_to_concurrent"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("shared", Assert.IsType<List<object?>>(result.Context["parallel.results"]).Cast<Dictionary<string, object?>>().First()["workdir_mode"]);
    }

    [Fact]
    public async Task Worktrees_left_by_runs_that_did_not_finish_are_removed_at_the_next_run()
    {
        using var repo = await TempRepo.CreateAsync(Files);
        var head = await repo.GitAsync("rev-parse", "HEAD");

        async Task<string> LeftBehindAsync(string run, JsonObject owner)
        {
            var root = repo.Combine(".reactor", "runs", run);
            Directory.CreateDirectory(Path.Combine(root, WorkspaceTracker.StateDirectoryName));
            File.WriteAllText(Path.Combine(root, WorkspaceTracker.StateDirectoryName, "owner.json"), owner.ToJsonString());
            var worktree = Path.Combine(root, "branches", "fork", "a");
            await repo.GitAsync("worktree", "add", "--detach", worktree, head);
            return worktree;
        }

        using var self = Process.GetCurrentProcess();
        var finished = await LeftBehindAsync("finished", new JsonObject { ["pid"] = Environment.ProcessId, ["finished"] = true });
        var crashed = await LeftBehindAsync("crashed", await ExitedProcessOwnerAsync());
        var running = await LeftBehindAsync("running", new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["process_started_ticks"] = self.StartTime.ToUniversalTime().Ticks,
            ["finished"] = false,
        });
        Assert.Equal(4, (await repo.WorktreesAsync()).Count);

        var result = await TestPipelines.Runner(new ScriptedBackend()).RunAsync("""
            digraph P { start [shape=Mdiamond] exit [shape=Msquare] work [prompt="x"] start -> work -> exit }
            """, Options(repo.Path, repo.Combine(".reactor", "runs", "next")), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        var remaining = await repo.WorktreesAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(TempRepoPath(running), remaining.Select(TempRepoPath));
        Assert.False(Directory.Exists(finished));
        Assert.False(Directory.Exists(crashed));
        await repo.GitAsync("worktree", "remove", "--force", running);
    }

    /// <summary>An owner record naming a process that has exited.</summary>
    private static async Task<JsonObject> ExitedProcessOwnerAsync()
    {
        using var process = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "true") { Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "", UseShellExecute = false, CreateNoWindow = true })!;
        var started = process.StartTime.ToUniversalTime().Ticks;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new JsonObject { ["pid"] = process.Id, ["process_started_ticks"] = started, ["finished"] = false };
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static string TempRepoPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
}

/// <summary>The branch_isolation and workspace_attributes lint rules.</summary>
public sealed class WorkspaceLintTests
{
    private static IReadOnlyList<Diagnostic> Lint(string source) => PipelineValidator.Validate(DotParser.Parse(source));

    private const string Parallel = """
        digraph P {
            start [shape=Mdiamond] exit [shape=Msquare]
            fork [shape=component]
            a [prompt="write a"]
            b1 [shape=diamond]
            b2 [shape=parallelogram, tool_command="npm test"]
            join [shape=tripleoctagon]
            start -> fork
            fork -> a -> join
            fork -> b1 -> b2 -> join
            join -> exit
        }
        """;

    [Fact]
    public void Branches_that_write_files_in_a_shared_workdir_get_an_info_note()
    {
        var note = Assert.Single(Lint(Parallel), d => d.Rule == "branch_isolation");
        Assert.Equal(Severity.Info, note.Severity);
        Assert.Equal("fork", note.NodeId);
        Assert.Contains("branch_workdir=\"worktree\"", note.Fix, StringComparison.Ordinal);

        Assert.DoesNotContain(Lint(Parallel.Replace("fork [shape=component]", "fork [shape=component, branch_workdir=\"shared\"]", StringComparison.Ordinal)), d => d.Rule == "branch_isolation");
        Assert.DoesNotContain(Lint(Parallel.Replace("b2 [shape=parallelogram, tool_command=\"npm test\"]", "b2 [shape=hexagon]", StringComparison.Ordinal)), d => d.Rule == "branch_isolation");
    }

    [Fact]
    public void Workspace_attribute_values_and_placement_are_checked()
    {
        var source = Parallel
            .Replace("digraph P {", "digraph P {\n    graph [track_changes=maybe]", StringComparison.Ordinal)
            .Replace("fork [shape=component]", "fork [shape=component, branch_workdir=\"clone\", concurrency_group=\"x\"]", StringComparison.Ordinal)
            .Replace("a [prompt=\"write a\"]", "a [prompt=\"write a\", write_scope=\"../up/**, C:/abs\", write_scope_enforce=sure, branch_workdir=\"copy\"]", StringComparison.Ordinal)
            .Replace("b1 [shape=diamond]", "b1 [shape=diamond, write_scope=\"x/**\", write_scope_enforce=true, promote=\"best\"]", StringComparison.Ordinal)
            .Replace("join [shape=tripleoctagon]", "join [shape=tripleoctagon, promote=\"best\", track_changes=nope]", StringComparison.Ordinal);

        var messages = Lint(source).Where(d => d.Rule == "workspace_attributes").Select(d => $"{d.NodeId}: {d.Message}").ToList();

        Assert.All(Lint(source).Where(d => d.Rule == "workspace_attributes"), d => Assert.Equal(Severity.Warning, d.Severity));
        Assert.Contains(": Graph attribute track_changes='maybe' must be true or false.", messages);
        Assert.Contains("fork: branch_workdir='clone' must be one of: shared, worktree, copy.", messages);
        Assert.Contains("fork: concurrency_group is ignored on parallel nodes; set it on the stages inside the branches.", messages);
        Assert.Contains("a: write_scope_enforce='sure' must be true or false.", messages);
        Assert.Contains("a: branch_workdir is ignored except on parallel (component) nodes.", messages);
        Assert.Contains(messages, m => m.StartsWith("a: write_scope: '../up/**' leaves", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("a: write_scope: 'C:/abs' is absolute", StringComparison.Ordinal));
        Assert.Contains("b1: write_scope is ignored on conditional nodes, which do not change files.", messages);
        Assert.Contains("b1: promote is ignored except on fan-in (tripleoctagon) nodes.", messages);
        Assert.Contains("join: track_changes='nope' must be true or false.", messages);
        Assert.Contains(messages, m => m.StartsWith("join: promote=\"best\" has nothing to promote", StringComparison.Ordinal));

        var isolated = Parallel
            .Replace("fork [shape=component]", "fork [shape=component, branch_workdir=\"worktree\"]", StringComparison.Ordinal)
            .Replace("join [shape=tripleoctagon]", "join [shape=tripleoctagon, promote=\"best\"]", StringComparison.Ordinal);
        Assert.DoesNotContain(Lint(isolated), d => d.Rule is "workspace_attributes" or "branch_isolation");
    }
}

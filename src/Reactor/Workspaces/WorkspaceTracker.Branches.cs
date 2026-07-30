using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Workspaces;

/// <summary>A parallel branch's own workspace (<c>branch_workdir="worktree"</c> or <c>"copy"</c>).</summary>
internal sealed class BranchWorkspace
{
    public required string ParallelNode { get; init; }

    public required string BranchId { get; init; }

    /// <summary><c>worktree</c> or <c>copy</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>The worktree (or copy) root, under <c>&lt;run&gt;/branches/&lt;parallel&gt;/</c>.</summary>
    public required string Root { get; init; }

    /// <summary>The branch's working directory: the root, plus the parent's subdirectory of the repository for worktrees.</summary>
    public required string Directory { get; init; }

    /// <summary>The working directory the branch was forked from.</summary>
    public required string ParentDirectory { get; init; }

    /// <summary>The parent workspace's state the branch started from (a commit for worktrees, a manifest for copies).</summary>
    public required WorkspaceSnapshot Base { get; init; }

    /// <summary>The snapshot of <see cref="ParentDirectory"/> taken at the fork (null when it could not be taken).</summary>
    public WorkspaceSnapshot? ParentBase { get; init; }

    public string? Ref { get; init; }

    public bool Removed { get; set; }
}

/// <summary>The result of promoting a branch into a fan-in's working directory.</summary>
internal sealed record PromotionResult(bool Promoted, string? Failure, int Files, string Note);

internal sealed partial class WorkspaceTracker
{
    private readonly List<BranchWorkspace> _branches = [];
    private readonly Dictionary<string, int> _parallelVisits = new(StringComparer.Ordinal);
    private readonly HashSet<string> _prunedRepositories = new(PathComparer);
    private readonly List<Task> _pruning = [];
    private bool _ownerWritten;
    private int _promotions;

    /// <summary>Starts removing stale worktrees of <paramref name="location"/>'s repository in the background (once per run).</summary>
    private void StartPruning(GitLocation location, string nodeId)
    {
        lock (_gate)
        {
            if (!_prunedRepositories.Add(location.CommonDir))
            {
                return;
            }

            _pruning.Add(Task.Run(async () =>
            {
                try
                {
                    await PruneStaleWorktreesAsync(location, nodeId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or Win32Exception)
                {
                    Log(nodeId, "could not check for stale branch worktrees: " + ex.Message, "warn");
                }
            }));
        }
    }

    /// <summary>Waits for background pruning, so worktree commands of this run never race it.</summary>
    private Task PruningAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_pruning.ToList());
        }
    }

    /// <summary>
    /// Creates one workspace per branch of <paramref name="parallel"/>, forked from the current state of the context's
    /// working directory (uncommitted and untracked files included, ignored files not): a detached git worktree of a
    /// snapshot commit (<c>worktree</c>) or a copy of the files (<c>copy</c>), under
    /// <c>&lt;logs root&gt;/branches/&lt;parallel&gt;/&lt;branch&gt;</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The workspaces cannot be created (e.g. <c>worktree</c> outside git).</exception>
    public async Task<IReadOnlyList<BranchWorkspace>> CreateBranchWorkspacesAsync(
        Node parallel,
        string mode,
        PipelineContext context,
        IReadOnlyList<string> branchIds,
        string logsRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parallel);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(branchIds);
        var parentDirectory = Normalize(context.WorkingDirectory);
        int visit;
        lock (_gate)
        {
            visit = _parallelVisits[parallel.Id] = _parallelVisits.GetValueOrDefault(parallel.Id) + 1;
        }

        var branchesDirectory = _options.Graph.ContainsNode("branches") ? ".branches" : "branches";
        var baseName = visit == 1 ? parallel.Id : string.Create(CultureInfo.InvariantCulture, $"{parallel.Id}-visit{visit}");
        var baseDirectory = Path.Combine(logsRoot, branchesDirectory, baseName);
        WriteOwner(finished: false);

        var parent = await GetWorkspaceAsync(parentDirectory, parallel.Id, cancellationToken).ConfigureAwait(false);
        var created = mode == WorkspaceAttributes.BranchModes.Worktree
            ? await CreateWorktreesAsync(parallel, parent, parentDirectory, branchIds, baseDirectory, baseName, cancellationToken).ConfigureAwait(false)
            : await CreateCopiesAsync(parallel, parent, parentDirectory, branchIds, baseDirectory, baseName, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_runRecord["branches"] is not JsonArray list)
            {
                _runRecord["branches"] = list = [];
            }

            foreach (var branch in created)
            {
                list.Add(new JsonObject
                {
                    ["parallel"] = branch.ParallelNode,
                    ["branch"] = branch.BranchId,
                    ["mode"] = branch.Mode,
                    ["directory"] = branch.Directory,
                    ["base"] = branch.Base.Id,
                    ["ref"] = branch.Ref,
                });
            }
        }

        SaveRunRecord();
        return created;
    }

    private async Task<IReadOnlyList<BranchWorkspace>> CreateWorktreesAsync(
        Node parallel,
        Workspace? parent,
        string parentDirectory,
        IReadOnlyList<string> branchIds,
        string baseDirectory,
        string baseName,
        CancellationToken cancellationToken)
    {
        if (parent?.Snapshotter is not GitSnapshotter git)
        {
            var reason = !await GitCli.IsAvailableAsync().ConfigureAwait(false)
                ? "git is not installed (or not on the PATH)"
                : $"'{parentDirectory}' is not in a git work tree (or git does not track it)";
            throw new InvalidOperationException($"branch_workdir=\"worktree\" needs a git work tree, but {reason}. Use branch_workdir=\"copy\" to give each branch a copy of the files instead.");
        }

        var fork = await FreshSnapshotAsync(parent, $"fork {parallel.Id}", parallel.Id, cancellationToken).ConfigureAwait(false);
        if (fork?.Commit is not { } commit || fork.Tree is null)
        {
            throw new InvalidOperationException($"Could not record the state of '{parentDirectory}' to fork the branches from (see the stage log).");
        }

        StartPruning(git.Location, parallel.Id);
        await PruningAsync().ConfigureAwait(false);

        // A loop re-entering the parallel node: the earlier branches' worktrees go (their snapshots stay on their refs).
        List<BranchWorkspace> previous;
        lock (_gate)
        {
            previous = _branches.Where(b => b.ParallelNode == parallel.Id && b.Mode == WorkspaceAttributes.BranchModes.Worktree && !b.Removed &&
                                            PathComparer.Equals(b.ParentDirectory, parentDirectory)).ToList();
        }

        foreach (var old in previous)
        {
            await RemoveWorktreeAsync(old, parallel.Id).ConfigureAwait(false);
        }

        var roots = branchIds.Select(id => UniqueDirectory(baseDirectory, id)).ToList();
        if (roots.Distinct(PathComparer).Count() != roots.Count)
        {
            // Two edges to the same node: give each its own directory.
            roots = roots.Select((root, i) => Normalize(Path.Combine(baseDirectory, string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(root)}-{i + 1}")))).ToList();
        }

        // Worktrees are independent, so they are checked out side by side.
        return await Task.WhenAll(branchIds.Select(async (branchId, i) =>
        {
            var root = roots[i];
            var add = await GitCli.RunAsync(parentDirectory, ["worktree", "add", "--detach", root, commit], new GitCall { Timeout = TimeSpan.FromMinutes(30) }, cancellationToken).ConfigureAwait(false);
            if (!add.Succeeded)
            {
                throw new InvalidOperationException($"git worktree add failed for branch '{branchId}': {add.Describe()}");
            }

            var directory = Normalize(Path.Combine(root, git.Location.Prefix));
            Directory.CreateDirectory(directory);
            var label = $"branches/{baseName}/{Path.GetFileName(root)}";
            var location = await GitLocation.ProbeAsync(directory, cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidOperationException($"The worktree for branch '{branchId}' ({root}) is not usable as a git work tree.");
            var snapshotter = await GitSnapshotter.OpenAsync(
                location,
                Path.Combine(StateDirectory, IndexName(label)),
                RefFor(label),
                [],
                seedIndex: Path.Combine(location.GitDir, "index"),
                cancellationToken,
                startCommit: commit,
                startTree: fork.Tree).ConfigureAwait(false);
            var branch = new BranchWorkspace
            {
                ParallelNode = parallel.Id,
                BranchId = branchId,
                Mode = WorkspaceAttributes.BranchModes.Worktree,
                Root = root,
                Directory = directory,
                ParentDirectory = parentDirectory,
                Base = fork,
                ParentBase = fork,
                Ref = snapshotter.RefName,
            };
            Register(branch, label, snapshotter, fork);
            Log(parallel.Id, $"branch {branchId} works in its own git worktree {directory} (detached at snapshot {commit[..Math.Min(12, commit.Length)]})");
            return branch;
        })).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BranchWorkspace>> CreateCopiesAsync(
        Node parallel,
        Workspace? parent,
        string parentDirectory,
        IReadOnlyList<string> branchIds,
        string baseDirectory,
        string baseName,
        CancellationToken cancellationToken)
    {
        // The files to copy: what a snapshot of the parent records (git: non-ignored files; else the walk's files).
        IReadOnlyList<string> files;
        GitIgnoreFilter? ignore = null;
        var fork = parent is null ? null : await FreshSnapshotAsync(parent, $"fork {parallel.Id}", parallel.Id, cancellationToken).ConfigureAwait(false);
        if (fork is not null && parent!.Snapshotter is GitSnapshotter git)
        {
            var listed = await GitCli.RunCheckedAsync(parentDirectory, ["ls-files", "-z", "-c", "--", "."], git.IndexCall, cancellationToken).ConfigureAwait(false);
            files = listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            ignore = new GitIgnoreFilter(parentDirectory);
        }
        else
        {
            var manifest = fork?.Manifest ?? new WalkSnapshotter(parentDirectory, IsRunDirectory, _options.WalkFileLimit).Walk(cancellationToken).Manifest!;
            files = manifest.Keys.ToList();
        }

        var roots = branchIds.Select(id => UniqueDirectory(baseDirectory, id)).ToList();
        if (roots.Distinct(PathComparer).Count() != roots.Count)
        {
            roots = roots.Select((root, i) => Normalize(Path.Combine(baseDirectory, string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(root)}-{i + 1}")))).ToList();
        }

        var walkers = roots.Select(root => new WalkSnapshotter(root, fileLimit: _options.WalkFileLimit)).ToList();
        var failed = await Task.Run(() => CopyFiles(parentDirectory, walkers, files, cancellationToken), cancellationToken).ConfigureAwait(false);
        var created = new List<BranchWorkspace>();
        for (var i = 0; i < branchIds.Count; i++)
        {
            var branchId = branchIds[i];
            var root = roots[i];
            if (failed[i].Count > 0)
            {
                Log(parallel.Id, $"branch {branchId}: {failed[i].Count} file(s) could not be copied (e.g. {string.Join(", ", failed[i].Take(3))})", "warn");
            }

            // The copy was hashed on the way, so this walk reads nothing.
            var label = $"branches/{baseName}/{Path.GetFileName(root)}";
            var start = walkers[i].Walk(cancellationToken);
            if (ignore is not null)
            {
                start = await ignore.ApplyAsync(start, cancellationToken).ConfigureAwait(false);
            }

            SaveBaseManifest(label, start);
            var branch = new BranchWorkspace
            {
                ParallelNode = parallel.Id,
                BranchId = branchId,
                Mode = WorkspaceAttributes.BranchModes.Copy,
                Root = root,
                Directory = root,
                ParentDirectory = parentDirectory,
                Base = start,
                ParentBase = fork,
            };
            Register(branch, label, ignore is null ? walkers[i] : new CopyWalkSnapshotter(walkers[i], ignore), start);
            created.Add(branch);
            Log(parallel.Id, $"branch {branchId} works in its own copy {root} ({start.Manifest!.Count} file(s); ignored files such as node_modules are not copied)");
        }

        return created;
    }

    private void Register(BranchWorkspace branch, string label, IWorkspaceSnapshotter snapshotter, WorkspaceSnapshot start)
    {
        var workspace = new Workspace(branch.Directory, label)
        {
            Branch = branch,
            Snapshotter = snapshotter,
            Opening = Task.FromResult(true),
            Last = start,
            Stale = false,
        };
        _workspaces[branch.Directory] = workspace;
        lock (_gate)
        {
            _branches.Add(branch);
        }
    }

    /// <summary>
    /// After the branches of <paramref name="parallel"/> worked in their own workspaces: whatever changed in the parent's
    /// working directory meanwhile was written outside them (an agent using absolute paths, a tool writing elsewhere, the
    /// operator). Such changes are recorded in the parallel node's <c>changes.json</c>, logged as a warning, and announced
    /// with <see cref="WorkspaceChanged"/>; nothing is recorded when nothing changed.
    /// </summary>
    public async Task CheckParentAfterBranchesAsync(Node parallel, PipelineContext context, IReadOnlyList<BranchWorkspace> branches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parallel);
        ArgumentNullException.ThrowIfNull(context);
        var graphSetting = AttributeValues.ParseBool(_options.Graph[WorkspaceAttributes.TrackChanges]) ?? true;
        if (!_options.TrackChanges || !graphSetting || branches.FirstOrDefault()?.ParentBase is not { } before ||
            !_workspaces.TryGetValue(Normalize(context.WorkingDirectory), out var workspace))
        {
            return;
        }

        var (after, changes) = await TakeSnapshotAsync(workspace, before, $"after the branches of {parallel.Id}", parallel.Id, cancellationToken).ConfigureAwait(false);
        if (after is null || changes is null || changes.Changes.Count == 0)
        {
            return;
        }

        var paths = changes.Changes.Select(c => c.Path).ToList();
        Log(parallel.Id, $"{paths.Count} file(s) changed in '{workspace.Directory}' while the branches worked in their own workspaces (written outside them?): {string.Join(", ", paths.Take(MaxReportedViolations))}{(paths.Count > MaxReportedViolations ? ", …" : "")}", "warn");
        int visit;
        lock (_gate)
        {
            visit = _parallelVisits.GetValueOrDefault(parallel.Id);
        }

        var watch = new StageWatch(this, parallel, HandlerTypes.Parallel, context, 0)
        {
            Before = before,
            Note = "changed in this working directory while the parallel branches worked in their own workspaces",
        };
        WriteChanges(watch, workspace, after, changes, visit, [], [], new HashSet<string>(), false, null);
        _options.Emit(new WorkspaceChanged(parallel.Id, 0, changes.Added, changes.Modified, changes.Deleted, paths.Take(SampleSize).ToList()) { Snapshot = after.Id });
    }

    /// <summary>
    /// Records where a branch ended: its final snapshot and what it changed since the fork. Returns the fields added to its
    /// <c>parallel.results</c> entry.
    /// </summary>
    public async Task<Dictionary<string, object?>> FinishBranchAsync(BranchWorkspace branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var fields = new Dictionary<string, object?>
        {
            ["workdir"] = branch.Directory,
            ["workdir_mode"] = branch.Mode,
            ["base_snapshot"] = branch.Base.Id,
        };
        if (!_workspaces.TryGetValue(branch.Directory, out var workspace) || workspace.Snapshotter is not { } snapshotter)
        {
            return fields;
        }

        var final = await FreshSnapshotAsync(workspace, $"branch {branch.BranchId} finished", branch.ParallelNode, cancellationToken).ConfigureAwait(false);
        if (final is null)
        {
            return fields;
        }

        fields["snapshot"] = final.Id;
        if (final.Tree is not null)
        {
            fields["tree"] = final.Tree;
        }

        try
        {
            var changes = await snapshotter.DiffAsync(branch.Base, final, 0, cancellationToken).ConfigureAwait(false);
            fields["changes"] = new Dictionary<string, object?>
            {
                ["added"] = changes.Added,
                ["modified"] = changes.Modified,
                ["deleted"] = changes.Deleted,
                ["paths"] = changes.Changes.Take(20).Select(c => (object?)c.Path).ToList(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(branch.ParallelNode, $"could not compare branch {branch.BranchId} with its fork: {ex.Message}", "warn");
        }

        if (snapshotter is IAsyncDisposable disposable)
        {
            // The branch is done: stop its helper process (its working directory is inside the worktree) and flush its ref.
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        return fields;
    }

    /// <summary>
    /// Applies the changes the winning branch made since its fork (described by its <c>parallel.results</c> entry) to the
    /// fan-in's working directory. Fails, changing nothing, when the working directory changed a path differently since the
    /// fork. Git branches are applied from the branch's final tree (<c>git checkout-index</c> through a temporary index);
    /// copies are applied file by file. The fan-in's <c>changes.json</c> records what was promoted.
    /// </summary>
    public async Task<PromotionResult> PromoteAsync(Node fanIn, PipelineContext context, IReadOnlyDictionary<string, object?> best, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fanIn);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(best);
        var branchId = Text(best, "id");
        var mode = Text(best, "workdir_mode");
        if (mode is not (WorkspaceAttributes.BranchModes.Worktree or WorkspaceAttributes.BranchModes.Copy))
        {
            return new PromotionResult(false, null, 0, $"branch {branchId} worked in the shared working directory, so there is nothing to promote");
        }

        var workdir = Text(best, "workdir");
        var baseId = Text(best, "base_snapshot");
        var finalId = Text(best, "snapshot");
        var finalTree = Text(best, "tree") is { Length: > 0 } tree ? tree : finalId;
        var target = Normalize(context.WorkingDirectory);
        var record = new JsonObject
        {
            ["branch"] = branchId,
            ["mode"] = mode,
            ["workdir"] = workdir,
            ["from"] = baseId,
            ["to"] = finalId,
        };

        List<FileChange> changes;
        List<string> conflicts;
        string? error;
        try
        {
            (changes, conflicts, error) = mode == WorkspaceAttributes.BranchModes.Worktree
                ? await PlanWorktreePromotionAsync(fanIn, target, baseId, finalTree, cancellationToken).ConfigureAwait(false)
                : await PlanCopyPromotionAsync(fanIn, target, workdir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            (changes, conflicts, error) = ([], [], ex.Message);
        }

        if (error is not null)
        {
            record["error"] = error;
            Attach(context, record);
            return new PromotionResult(false, $"Could not promote branch {branchId}: {error}", 0, "");
        }

        if (conflicts.Count > 0)
        {
            record["conflicts"] = new JsonArray(conflicts.Select(c => (JsonNode?)c).ToArray());
            Attach(context, record);
            var listed = string.Join(", ", conflicts.Take(MaxReportedViolations)) + (conflicts.Count > MaxReportedViolations ? $", … ({conflicts.Count} in all)" : "");
            return new PromotionResult(false, $"Cannot promote branch {branchId}: the working directory changed {conflicts.Count} of its file(s) since the fork: {listed}", 0, "");
        }

        try
        {
            if (mode == WorkspaceAttributes.BranchModes.Worktree)
            {
                await ApplyFromTreeAsync(target, finalTree, changes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ApplyFromCopy(workdir, target, changes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            record["error"] = ex.Message;
            Attach(context, record);
            return new PromotionResult(false, $"Promoting branch {branchId} failed part-way: {ex.Message}", 0, "");
        }

        record["files"] = changes.Count;
        record["applied"] = new JsonArray(changes.Take(MaxListedChanges)
            .Select(c => (JsonNode?)new JsonObject { ["path"] = c.Path, ["change"] = FileChange.Wire(c.Kind) }).ToArray());
        Attach(context, record);
        if (_workspaces.TryGetValue(target, out var targetWorkspace))
        {
            lock (targetWorkspace)
            {
                targetWorkspace.Stale = true;
            }
        }

        _options.Emit(new WorkspacePromoted(fanIn.Id, branchId, workdir, changes.Count));
        return new PromotionResult(true, null, changes.Count, $"promoted branch {branchId}: {changes.Count} file(s)");
    }

    private async Task<(List<FileChange> Changes, List<string> Conflicts, string? Error)> PlanWorktreePromotionAsync(
        Node fanIn,
        string target,
        string baseId,
        string finalTree,
        CancellationToken cancellationToken)
    {
        if (baseId.Length == 0 || finalTree.Length == 0)
        {
            return ([], [], "the branch's snapshots were not recorded");
        }

        var workspace = await GetWorkspaceAsync(target, fanIn.Id, cancellationToken).ConfigureAwait(false);
        if (workspace?.Snapshotter is not GitSnapshotter)
        {
            return ([], [], $"'{target}' is not a git work tree");
        }

        var now = await FreshSnapshotAsync(workspace, $"before promoting into {fanIn.Id}", fanIn.Id, cancellationToken).ConfigureAwait(false);
        if (now?.Tree is null)
        {
            return ([], [], "could not record the working directory's current state");
        }

        var branchChanges = await GitSnapshotter.DiffTreesAsync(target, baseId, finalTree, cancellationToken).ConfigureAwait(false);
        var ownChanges = (await GitSnapshotter.DiffTreesAsync(target, baseId, now.Tree, cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Path, StringComparer.Ordinal);
        var conflicts = new List<string>();
        var apply = new List<FileChange>();
        foreach (var change in branchChanges)
        {
            if (!ownChanges.TryGetValue(change.Path, out var own))
            {
                apply.Add(change);
            }
            else if (own.Kind != change.Kind || !string.Equals(own.Id, change.Id, StringComparison.Ordinal))
            {
                conflicts.Add(change.Path);
            }
        }

        return (apply, conflicts, null);
    }

    private async Task<(List<FileChange> Changes, List<string> Conflicts, string? Error)> PlanCopyPromotionAsync(
        Node fanIn,
        string target,
        string workdir,
        CancellationToken cancellationToken)
    {
        var branch = FindBranch(workdir);
        var start = branch?.Base ?? LoadBaseManifest(workdir);
        if (start?.Manifest is null || !Directory.Exists(workdir))
        {
            return ([], [], $"the branch workspace '{workdir}' (or its starting manifest) is gone");
        }

        WorkspaceSnapshot? final = null;
        if (_workspaces.TryGetValue(Normalize(workdir), out var workspace))
        {
            final = await FreshSnapshotAsync(workspace, "promote", fanIn.Id, cancellationToken).ConfigureAwait(false);
        }

        if (final is null)
        {
            final = new WalkSnapshotter(workdir, fileLimit: _options.WalkFileLimit).Walk(cancellationToken);
            if (await GitLocation.ProbeAsync(target, cancellationToken).ConfigureAwait(false) is not null)
            {
                // A copy of a git work tree: ignored files (the branch's own build output, or ones copied when the fork's
                // git snapshot failed) are left out on both sides, so they are neither promoted nor deleted.
                var ignore = new GitIgnoreFilter(target);
                start = await ignore.ApplyAsync(start, cancellationToken).ConfigureAwait(false);
                final = await ignore.ApplyAsync(final, cancellationToken).ConfigureAwait(false);
            }
        }

        var changes = WalkSnapshotter.Diff(start, final).Changes;
        var conflicts = new List<string>();
        var apply = new List<FileChange>();
        foreach (var change in changes)
        {
            var current = CurrentState(Path.Combine(target, change.Path));
            var before = start.Manifest!.TryGetValue(change.Path, out var b) ? b : (FileState?)null;
            var after = final.Manifest!.TryGetValue(change.Path, out var a) ? a : (FileState?)null;
            if (SameContent(current, before))
            {
                apply.Add(change);
            }
            else if (!SameContent(current, after))
            {
                conflicts.Add(change.Path);
            }
        }

        return (apply, conflicts, null);
    }

    private static bool SameContent(FileState? a, FileState? b) =>
        a is null || b is null ? a is null && b is null : a.Value.Size == b.Value.Size && a.Value.Sha256 is not null && a.Value.Sha256 == b.Value.Sha256;

    private static FileState? CurrentState(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new FileState(stream.Length, 0, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    /// <summary>Writes the branch's version of each added or modified path (from <paramref name="tree"/>) and deletes deleted ones.</summary>
    private async Task ApplyFromTreeAsync(string target, string tree, IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        var location = await GitLocation.ProbeAsync(target, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"'{target}' is not a git work tree.");
        var index = Path.Combine(StateDirectory, string.Create(CultureInfo.InvariantCulture, $"promote-{Interlocked.Increment(ref _promotions)}.index"));
        await WorkspaceRewind.ApplyTreeAsync(location, tree, changes, index, removeEmptyDirectories: false, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyFromCopy(string workdir, string target, IReadOnlyList<FileChange> changes)
    {
        foreach (var change in changes)
        {
            var destination = Path.Combine(target, change.Path);
            if (change.Kind == ChangeKind.Deleted)
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(workdir, change.Path), destination, overwrite: true);
        }
    }

    private void Attach(PipelineContext context, JsonObject record)
    {
        if (_watches.TryGetValue(context, out var watch))
        {
            watch.Promotion = record;
        }
    }

    private BranchWorkspace? FindBranch(string directory)
    {
        lock (_gate)
        {
            return _branches.LastOrDefault(b => PathComparer.Equals(b.Directory, Normalize(directory)));
        }
    }

    private void SaveBaseManifest(string label, WorkspaceSnapshot snapshot)
    {
        try
        {
            var files = new JsonObject();
            foreach (var (path, state) in snapshot.Manifest!.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                files[path] = new JsonArray(state.Size, state.Sha256);
            }

            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(Path.Combine(StateDirectory, label.Replace('/', '.') + ".base.json"), new JsonObject { ["files"] = files }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only needed to promote a copied branch after a resume.
        }
    }

    /// <summary>The starting manifest of a copied branch saved by an earlier process (a resumed run).</summary>
    private WorkspaceSnapshot? LoadBaseManifest(string workdir)
    {
        var relative = Path.GetRelativePath(RunDirectory, Normalize(workdir)).Replace('\\', '/');
        var path = Path.Combine(StateDirectory, relative.Replace('/', '.') + ".base.json");
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path))?["files"] is not JsonObject files)
            {
                return null;
            }

            var manifest = new Dictionary<string, FileState>(StringComparer.Ordinal);
            foreach (var (file, value) in files)
            {
                if (value is JsonArray { Count: 2 } entry)
                {
                    manifest[file] = new FileState(entry[0]?.GetValue<long>() ?? 0, 0, entry[1]?.GetValue<string>());
                }
            }

            return new WorkspaceSnapshot { Id = "walk:base", Manifest = manifest };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies <paramref name="files"/> (relative to <paramref name="source"/>) into each walker's directory, several at a
    /// time, hashing each file once on the way (each walker remembers the copies). Returns the files that failed, per walker.
    /// </summary>
    private static List<string>[] CopyFiles(string source, IReadOnlyList<WalkSnapshotter> walkers, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var failed = walkers.Select(_ => new List<string>()).ToArray();
        foreach (var walker in walkers)
        {
            Directory.CreateDirectory(walker.Directory);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = cancellationToken };
        Parallel.ForEach(files, options, file =>
        {
            var from = Path.Combine(source, file);
            if (!File.Exists(from))
            {
                return;
            }

            for (var i = 0; i < walkers.Count; i++)
            {
                var to = Path.Combine(walkers[i].Directory, file);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    walkers[i].Remember(file.Replace('\\', '/'), WalkSnapshotter.CopyAndHash(from, to));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lock (failed[i])
                    {
                        failed[i].Add(file);
                    }
                }
            }
        });
        return failed;
    }

    /// <summary><c>&lt;base&gt;/&lt;name&gt;</c>, or <c>&lt;name&gt;-2</c>, … when a directory of that name is left from before.</summary>
    private static string UniqueDirectory(string baseDirectory, string name)
    {
        var candidate = Path.Combine(baseDirectory, name);
        for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = Path.Combine(baseDirectory, string.Create(CultureInfo.InvariantCulture, $"{name}-{n}"));
        }

        return Normalize(candidate);
    }

    private static string Text(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? ContextValues.ToDisplayString(value) : "";

    private async Task RemoveWorktreeAsync(BranchWorkspace branch, string nodeId)
    {
        var directory = System.IO.Directory.Exists(branch.ParentDirectory) ? branch.ParentDirectory : MainDirectory;
        try
        {
            var removed = await GitCli.RunAsync(directory, ["worktree", "remove", "--force", "--force", branch.Root], new GitCall { Timeout = TimeSpan.FromMinutes(10) }).ConfigureAwait(false);
            if (removed.Succeeded || !System.IO.Directory.Exists(branch.Root))
            {
                branch.Removed = true;
                return;
            }

            Log(nodeId, $"could not remove the worktree of branch {branch.BranchId} ({branch.Root}): {removed.Describe()}. The next run in this repository removes it, or run: git worktree remove --force \"{branch.Root}\"", "warn");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or Win32Exception)
        {
            Log(nodeId, $"could not remove the worktree of branch {branch.BranchId} ({branch.Root}): {ex.Message}", "warn");
        }
    }

    /// <summary>
    /// Removes branch worktrees that runs which did not finish (crashed or killed) left registered in the repository: worktrees
    /// under a run directory whose <c>.workspace/owner.json</c> says the run finished or names a process that is gone.
    /// Registrations whose directories were deleted are pruned.
    /// </summary>
    private async Task PruneStaleWorktreesAsync(GitLocation location, string nodeId, CancellationToken cancellationToken)
    {
        var list = await GitCli.RunAsync(location.Directory, ["worktree", "list", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!list.Succeeded)
        {
            return;
        }

        var paths = list.Output.Split('\n')
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => l["worktree ".Length..].TrimEnd('\r'))
            .Skip(1)
            .ToList();
        var removed = 0;
        var dangling = false;
        foreach (var path in paths)
        {
            string full;
            try
            {
                full = Normalize(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!System.IO.Directory.Exists(full))
            {
                dangling |= IsBranchWorkspacePath(full);
                continue;
            }

            var runDirectory = OwningRunDirectory(full);
            if (runDirectory is null || PathComparer.Equals(runDirectory, RunDirectory) || !OwnerIsGone(runDirectory))
            {
                continue;
            }

            var result = await GitCli.RunAsync(location.Directory, ["worktree", "remove", "--force", "--force", full], new GitCall { Timeout = TimeSpan.FromMinutes(10) }, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                removed++;
            }
            else
            {
                Log(nodeId, $"could not remove the stale branch worktree {full}: {result.Describe()}", "warn");
            }
        }

        if (dangling)
        {
            await GitCli.RunAsync(location.Directory, ["worktree", "prune"], cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (removed > 0)
        {
            Log(nodeId, $"removed {removed} branch worktree(s) left registered by earlier runs that did not finish");
        }
    }

    /// <summary><c>&lt;run&gt;/branches/&lt;parallel&gt;/&lt;branch&gt;</c> → the run directory (marked by <c>.workspace/owner.json</c>).</summary>
    private static string? OwningRunDirectory(string worktree)
    {
        var current = Path.GetDirectoryName(worktree);
        for (var level = 0; level < 4 && current is not null; level++, current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, StateDirectoryName, OwnerFileName)))
            {
                return current;
            }
        }

        return null;
    }

    private static bool IsBranchWorkspacePath(string path)
    {
        var grandparent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? "");
        return grandparent is "branches" or ".branches";
    }

    private const string OwnerFileName = "owner.json";

    /// <summary>True when the run that owns <paramref name="runDirectory"/> finished or its process is gone.</summary>
    private static bool OwnerIsGone(string runDirectory)
    {
        try
        {
            var owner = JsonNode.Parse(File.ReadAllText(Path.Combine(runDirectory, StateDirectoryName, OwnerFileName)));
            if (owner?["finished"]?.GetValue<bool>() == true)
            {
                return true;
            }

            var pid = owner?["pid"]?.GetValue<int>() ?? 0;
            var started = owner?["process_started_ticks"]?.GetValue<long>() ?? 0;
            using var process = Process.GetProcessById(pid);
            return Math.Abs(process.StartTime.ToUniversalTime().Ticks - started) > TimeSpan.TicksPerSecond;
        }
        catch (ArgumentException)
        {
            // No process with that id.
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process exists but cannot be inspected: assume it is still running.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            return false;
        }
    }

    private void WriteOwner(bool finished)
    {
        lock (_gate)
        {
            if (!finished && _ownerWritten)
            {
                return;
            }

            _ownerWritten = true;
        }

        try
        {
            using var self = Process.GetCurrentProcess();
            var owner = new JsonObject
            {
                ["run_id"] = _options.RunId,
                ["pid"] = Environment.ProcessId,
                ["process_started_ticks"] = self.StartTime.ToUniversalTime().Ticks,
                ["machine"] = Environment.MachineName,
                ["finished"] = finished,
            };
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(Path.Combine(StateDirectory, OwnerFileName), owner.ToJsonString(Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            // Without the owner file, a later run cannot tell whether these worktrees are stale; they are kept.
        }
    }

    /// <summary>Ends a stage visit that did not complete (an exception or cancellation) and releases what it holds.</summary>
    internal void Release(StageWatch watch)
    {
        if (!watch.Completed && watch.Workspace is { } workspace)
        {
            lock (workspace)
            {
                workspace.Active.Remove(watch);

                // The stage may have stopped half-way through writing: look again before the next stage.
                workspace.Stale = true;
            }
        }

        _watches.TryRemove(new KeyValuePair<PipelineContext, StageWatch>(watch.Context, watch));
        ReleaseHold(watch, null);
    }

    /// <summary>Removes the run's branch worktrees (their snapshot refs stay) and marks the run finished for later runs.</summary>
    public async ValueTask DisposeAsync()
    {
        await PruningAsync().ConfigureAwait(false);

        // Flush snapshot ref updates and stop helper processes before removing worktrees they may be working in.
        foreach (var workspace in _workspaces.Values)
        {
            if (workspace.Snapshotter is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }

            if (workspace.Snapshotter is GitSnapshotter { RefError: { } refError } git)
            {
                Log(workspace.Branch?.ParallelNode ?? "workspace", $"could not update {git.RefName}: {refError}", "warn");
            }
        }

        List<BranchWorkspace> worktrees;
        lock (_gate)
        {
            worktrees = _branches.Where(b => b.Mode == WorkspaceAttributes.BranchModes.Worktree && !b.Removed).ToList();
        }

        foreach (var branch in worktrees)
        {
            await RemoveWorktreeAsync(branch, branch.ParallelNode).ConfigureAwait(false);
        }

        bool owned;
        lock (_gate)
        {
            owned = _ownerWritten;
        }

        if (owned)
        {
            WriteOwner(finished: true);
        }
    }
}

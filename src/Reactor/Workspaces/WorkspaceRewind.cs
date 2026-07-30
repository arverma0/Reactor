using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Workspaces;

/// <summary>
/// The workspace snapshot recorded with a checkpoint snapshot (<c>checkpoints/&lt;NNNN-node&gt;.workspace.json</c>): the
/// state of the run's working directory right after that stage, as a git commit (git work trees only).
/// </summary>
public sealed record CheckpointWorkspace
{
    /// <summary>Suffix of the file next to <c>checkpoints/&lt;NNNN-node&gt;.json</c>.</summary>
    public const string FileSuffix = ".workspace.json";

    /// <summary>The checkpoint snapshot name, e.g. <c>0004-plan</c>.</summary>
    public required string Checkpoint { get; init; }

    public int Index { get; init; }

    public string Node { get; init; } = "";

    /// <summary><c>git</c> (only git snapshots are recorded).</summary>
    public string Mode { get; init; } = "git";

    /// <summary>The snapshot commit (null when committing failed; <see cref="Tree"/> still holds the state).</summary>
    public string? Commit { get; init; }

    public required string Tree { get; init; }

    /// <summary>The ref whose commit chain keeps the snapshot reachable.</summary>
    public string Ref { get; init; } = "";

    /// <summary>The work tree's root.</summary>
    public string Repository { get; init; } = "";

    /// <summary>The repository's shared git directory (identifies the repository across its worktrees).</summary>
    public required string GitDir { get; init; }

    /// <summary>The working directory relative to <see cref="Repository"/>, <c>/</c>-terminated (empty at the root).</summary>
    public string Prefix { get; init; } = "";

    /// <summary>The run's working directory.</summary>
    public string Directory { get; init; } = "";

    public DateTimeOffset TakenAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The commit, or the tree when there is no commit.</summary>
    public string Id => Commit ?? Tree;

    /// <summary>The file recording the workspace for checkpoint snapshot <paramref name="snapshotName"/>.</summary>
    public static string PathFor(string checkpointsDirectory, string snapshotName) => System.IO.Path.Combine(checkpointsDirectory, snapshotName + FileSuffix);

    /// <summary>Reads a record; null when the file is missing or unreadable.</summary>
    public static CheckpointWorkspace? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
            {
                return null;
            }

            string Text(string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            var tree = Text("tree");
            var gitDir = Text("git_dir");
            if (tree.Length == 0 || gitDir.Length == 0)
            {
                return null;
            }

            return new CheckpointWorkspace
            {
                Checkpoint = Text("checkpoint"),
                Index = json["index"] is JsonValue index && index.TryGetValue<int>(out var number) ? number : 0,
                Node = Text("node"),
                Mode = Text("mode") is { Length: > 0 } mode ? mode : "git",
                Commit = Text("commit") is { Length: > 0 } commit ? commit : null,
                Tree = tree,
                Ref = Text("ref"),
                Repository = Text("repository"),
                GitDir = gitDir,
                Prefix = Text("prefix"),
                Directory = Text("directory"),
                TakenAt = DateTimeOffset.TryParse(Text("taken_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : default,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public JsonObject ToJson() => new()
    {
        ["checkpoint"] = Checkpoint,
        ["index"] = Index,
        ["node"] = Node,
        ["mode"] = Mode,
        ["commit"] = Commit,
        ["tree"] = Tree,
        ["ref"] = Ref,
        ["repository"] = Repository,
        ["git_dir"] = GitDir,
        ["prefix"] = Prefix,
        ["directory"] = Directory,
        ["taken_at"] = TakenAt.ToString("O", CultureInfo.InvariantCulture),
    };
}

/// <summary>What <see cref="WorkspaceRewind.RestoreAsync"/> did.</summary>
public sealed record WorkspaceRestore
{
    /// <summary>The directory that was rewound.</summary>
    public required string Directory { get; init; }

    /// <summary>The checkpoint whose workspace was restored.</summary>
    public required string Checkpoint { get; init; }

    /// <summary>The snapshot restored (commit, or tree).</summary>
    public required string Target { get; init; }

    /// <summary>The snapshot of the directory taken just before the rewind: restore it to undo the rewind.</summary>
    public required string UndoCommit { get; init; }

    /// <summary>The ref that keeps <see cref="UndoCommit"/> reachable.</summary>
    public required string UndoRef { get; init; }

    /// <summary>Files written (added or modified to match the snapshot).</summary>
    public int Written { get; init; }

    /// <summary>Files deleted (absent from the snapshot).</summary>
    public int Deleted { get; init; }

    /// <summary>The first changed paths (relative to <see cref="Directory"/>).</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>A command that puts the files back as they were before the rewind (files the rewind deleted come back too).</summary>
    public string UndoCommand => $"git -C \"{Directory}\" restore --source {UndoCommit} --worktree -- .";

    public JsonObject ToJson() => new()
    {
        ["directory"] = Directory,
        ["checkpoint"] = Checkpoint,
        ["target"] = Target,
        ["undo_commit"] = UndoCommit,
        ["undo_ref"] = UndoRef,
        ["undo_command"] = UndoCommand,
        ["written"] = Written,
        ["deleted"] = Deleted,
        ["paths"] = new JsonArray(Paths.Select(p => (JsonNode?)p).ToArray()),
    };
}

/// <summary>A workspace rewind that was refused or failed; <see cref="Code"/> is one of the <see cref="WorkspaceRewind"/> codes.</summary>
public sealed class WorkspaceRewindException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Rewinds a working directory to a recorded git snapshot (Reactor extension, used by Re-fly). The directory's current
/// state is snapshotted first (a commit on an undo ref), then the files that differ are written from the snapshot through
/// a temporary index (<c>read-tree</c> + <c>checkout-index</c>) and files the snapshot does not have are deleted. The
/// user's index, <c>HEAD</c>, and branches are never touched, and ignored files (<c>node_modules</c>, build output) and
/// the run directories are left alone, as they were never part of the snapshots.
/// </summary>
public static class WorkspaceRewind
{
    /// <summary>The run recorded no git snapshots (file-walk workspace, or git is not available here).</summary>
    public const string Unavailable = "rewind_unavailable";

    /// <summary>No workspace snapshot was recorded with the checkpoint, or it is no longer in the repository.</summary>
    public const string SnapshotMissing = "workspace_snapshot_missing";

    /// <summary>The directory is not the source run's repository and subdirectory.</summary>
    public const string Mismatch = "workspace_mismatch";

    /// <summary>The directory's current state could not be recorded or the files could not be written.</summary>
    public const string Failed = "rewind_failed";

    /// <summary>The ref (under the run's <c>refs/reactor/runs/&lt;run&gt;/</c>) of the snapshot taken before a rewind.</summary>
    public static string UndoRefFor(string runId) => $"refs/reactor/runs/{WorkspaceTracker.SafeRefPart(runId)}/before-rewind";

    /// <summary>The private index of a run's main workspace snapshots (a cheap starting point for a later snapshot).</summary>
    public static string MainIndexOf(string runDirectory) => Path.Combine(runDirectory, WorkspaceTracker.StateDirectoryName, "main.index");

    /// <summary>
    /// Rewinds <paramref name="directory"/> to <paramref name="target"/> for the run <paramref name="runId"/> that is about
    /// to start there: snapshots the directory's current state (the undo commit, on <see cref="UndoRefFor"/>), then
    /// writes the files that differ from the snapshot and deletes the ones it does not have. Ignored files, the
    /// <paramref name="excludedDirectories"/>, and <paramref name="runDirectory"/> are left alone.
    /// </summary>
    /// <param name="directory">The directory to rewind (it must be in the snapshot's repository, at the same subdirectory).</param>
    /// <param name="target">The snapshot to restore.</param>
    /// <param name="runId">The run that starts in the rewound directory (owns the undo ref).</param>
    /// <param name="runDirectory">That run's directory: temporary index files go to its <c>.workspace</c> directory.</param>
    /// <param name="excludedDirectories">Further directories never recorded or touched (e.g. the directory holding run directories).</param>
    /// <param name="seedIndex">
    /// A private index of an earlier snapshot of the directory (e.g. <see cref="MainIndexOf"/> the source run), copied as
    /// the starting point so only changed files are rehashed; null or missing = start from <c>HEAD</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the git calls (do not cancel while files are being written).</param>
    /// <exception cref="WorkspaceRewindException">
    /// The rewind was refused or failed. Nothing is changed before the undo snapshot exists; a failure while writing files
    /// names the undo commit.
    /// </exception>
    public static async Task<WorkspaceRestore> RestoreAsync(
        string directory,
        CheckpointWorkspace target,
        string runId,
        string runDirectory,
        IEnumerable<string> excludedDirectories,
        string? seedIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentNullException.ThrowIfNull(excludedDirectories);
        directory = WorkspaceTracker.Normalize(directory);
        if (!string.Equals(target.Mode, "git", StringComparison.Ordinal))
        {
            throw new WorkspaceRewindException(Unavailable, "Rewind is unavailable: the run recorded its workspace with a file walk, which keeps no file contents.");
        }

        if (!await GitCli.IsAvailableAsync().ConfigureAwait(false))
        {
            throw new WorkspaceRewindException(Unavailable, "Rewind is unavailable: git is not installed (or not on the PATH).");
        }

        var location = await GitLocation.ProbeAsync(directory, cancellationToken).ConfigureAwait(false)
                       ?? throw new WorkspaceRewindException(Mismatch,
                           $"Cannot rewind '{directory}': it is not a tracked directory of a git work tree, so it is not the directory the run recorded ('{target.Directory}').");
        if (!WorkspaceTracker.PathComparer.Equals(WorkspaceTracker.Normalize(location.CommonDir), WorkspaceTracker.Normalize(target.GitDir)) ||
            !string.Equals(location.Prefix, target.Prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new WorkspaceRewindException(Mismatch,
                $"Cannot rewind '{directory}': the snapshot belongs to {Describe(target.GitDir, target.Prefix)}, but this directory is {Describe(location.CommonDir, location.Prefix)}.");
        }

        var exists = await GitCli.RunAsync(directory, ["cat-file", "-e", target.Id + "^{tree}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!exists.Succeeded)
        {
            throw new WorkspaceRewindException(SnapshotMissing,
                $"The workspace snapshot {target.Id} is no longer in the repository (were the refs under '{target.Ref}' deleted and the repository garbage-collected?).");
        }

        runDirectory = WorkspaceTracker.Normalize(runDirectory);
        var excluded = excludedDirectories.Append(runDirectory).Select(WorkspaceTracker.Normalize).Distinct(WorkspaceTracker.PathComparer).ToList();
        var excludes = excluded.SelectMany(d => WorkspaceTracker.ExcludesFor(directory, d)).Distinct(StringComparer.Ordinal).ToList();
        var stateDirectory = Path.Combine(runDirectory, WorkspaceTracker.StateDirectoryName);
        var undoIndex = Path.Combine(stateDirectory, "rewind-undo.index");
        var undoRef = UndoRefFor(runId);
        var undo = await SnapshotBeforeAsync(
            location,
            target,
            undoIndex,
            undoRef,
            excludes,
            // Another work tree of the repository has other files behind the same stat data: start from HEAD there.
            target.Directory.Length > 0 && WorkspaceTracker.PathComparer.Equals(WorkspaceTracker.Normalize(target.Directory), directory) ? seedIndex : null,
            cancellationToken).ConfigureAwait(false);

        // Paths under excluded directories are left alone (they may still be in one of the trees, e.g. from HEAD).
        var skippedPrefixes = excluded.Where(d => WorkspaceTracker.IsUnder(d, directory) && !WorkspaceTracker.PathComparer.Equals(d, directory))
            .Select(d => Path.GetRelativePath(directory, d).Replace('\\', '/') + "/")
            .ToList();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<FileChange> changes;
        try
        {
            changes = (await GitSnapshotter.DiffTreesAsync(directory, undo.Tree!, target.Id, cancellationToken).ConfigureAwait(false))
                .Where(c => !skippedPrefixes.Any(p => c.Path.StartsWith(p, comparison)))
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            TryDelete(undoIndex);
            throw new WorkspaceRewindException(Failed, $"Could not compare the working directory with the snapshot ({ex.Message}); nothing was changed.");
        }

        try
        {
            await ApplyTreeAsync(location, target.Id, changes, Path.Combine(stateDirectory, "rewind-target.index"), removeEmptyDirectories: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            TryDelete(undoIndex);
            throw new WorkspaceRewindException(Failed, $"The rewind stopped part-way ({ex.Message}); undo it with: git -C \"{directory}\" restore --source {undo.Commit} --worktree -- .");
        }

        // The undo snapshot's index (stat data included) is a cheap starting point for the new run's own snapshots: only
        // the files just written get rehashed.
        var runIndex = MainIndexOf(runDirectory);
        try
        {
            if (!File.Exists(runIndex))
            {
                File.Move(undoIndex, runIndex);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        TryDelete(undoIndex);
        return new WorkspaceRestore
        {
            Directory = directory,
            Checkpoint = target.Checkpoint,
            Target = target.Id,
            UndoCommit = undo.Commit!,
            UndoRef = undoRef,
            Written = changes.Count(c => c.Kind != ChangeKind.Deleted),
            Deleted = changes.Count(c => c.Kind == ChangeKind.Deleted),
            Paths = changes.Take(20).Select(c => c.Path).ToList(),
        };
    }

    /// <summary>Snapshots the directory before the rewind (a commit on <paramref name="undoRef"/>); throws when it cannot.</summary>
    private static async Task<WorkspaceSnapshot> SnapshotBeforeAsync(
        GitLocation location,
        CheckpointWorkspace target,
        string undoIndex,
        string undoRef,
        List<string> excludes,
        string? seedIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(undoIndex)!);
            TryDelete(undoIndex);
            if (seedIndex is not null && File.Exists(seedIndex))
            {
                try
                {
                    File.Copy(seedIndex, undoIndex, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Start from HEAD instead (slower: every file is hashed once).
                }
            }

            WorkspaceSnapshot undo;
            var snapshotter = await GitSnapshotter.OpenAsync(location, undoIndex, undoRef, excludes, seedIndex: null, cancellationToken).ConfigureAwait(false);
            try
            {
                undo = await snapshotter.SnapshotAsync($"reactor: before rewinding to checkpoint {target.Checkpoint} ({target.Id})", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Waits for the undo ref to be written.
                await snapshotter.DisposeAsync().ConfigureAwait(false);
            }

            if (undo.Commit is null || undo.Tree is null || snapshotter.RefError is not null)
            {
                throw new InvalidOperationException(snapshotter.RefError ?? "git commit-tree failed");
            }

            return undo;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            TryDelete(undoIndex);
            throw new WorkspaceRewindException(Failed, $"Could not record the working directory before the rewind ({ex.Message}); nothing was changed.");
        }
    }

    /// <summary>
    /// Makes the given changes real in <paramref name="location"/>'s directory: added and modified paths are written from
    /// <paramref name="tree"/> through a temporary index at <paramref name="indexPath"/> (<c>read-tree</c> +
    /// <c>checkout-index</c>, which applies the work tree's line-ending and filter settings and never touches the user's
    /// index or <c>HEAD</c>); deleted paths are removed, and with <paramref name="removeEmptyDirectories"/> so are the
    /// directories that leaves empty. Submodule entries are skipped.
    /// </summary>
    internal static async Task ApplyTreeAsync(GitLocation location, string tree, IReadOnlyList<FileChange> changes, string indexPath, bool removeEmptyDirectories, CancellationToken cancellationToken)
    {
        var writes = changes.Where(c => c.Kind != ChangeKind.Deleted && c.Type != "submodule").Select(c => location.Prefix + c.Path).ToList();
        if (writes.Count > 0)
        {
            var call = new GitCall { Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = indexPath } };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
                await GitCli.RunCheckedAsync(location.TopLevel, ["read-tree", tree], call, cancellationToken).ConfigureAwait(false);
                await GitCli.RunCheckedAsync(
                    location.TopLevel,
                    ["checkout-index", "-f", "-z", "--stdin"],
                    call with { Input = string.Join('\0', writes) + "\0", Timeout = TimeSpan.FromMinutes(30) },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(indexPath);
            }
        }

        foreach (var change in changes.Where(c => c.Kind == ChangeKind.Deleted && c.Type != "submodule"))
        {
            var path = Path.Combine(location.Directory, change.Path);
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            if (removeEmptyDirectories)
            {
                RemoveEmptyParents(Path.GetDirectoryName(path), location.Directory);
            }
        }
    }

    private static void RemoveEmptyParents(string? directory, string root)
    {
        var top = WorkspaceTracker.Normalize(root);
        while (directory is not null && !WorkspaceTracker.PathComparer.Equals(WorkspaceTracker.Normalize(directory), top) && WorkspaceTracker.IsUnder(directory, top))
        {
            try
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private static string Describe(string gitDir, string prefix) =>
        $"the repository at '{gitDir}'" + (prefix.Length > 0 ? $", subdirectory '{prefix.TrimEnd('/')}'" : " (its root)");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

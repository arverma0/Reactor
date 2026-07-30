using System.Text.RegularExpressions;

namespace Reactor.Workspaces;

/// <summary>Where a directory sits in a git work tree.</summary>
/// <param name="Directory">The directory (full path).</param>
/// <param name="TopLevel">The work tree's root.</param>
/// <param name="Prefix">The directory relative to <paramref name="TopLevel"/>, <c>/</c>-terminated (empty at the root).</param>
/// <param name="GitDir">The work tree's git directory (<c>.git</c>, or <c>.git/worktrees/&lt;name&gt;</c> for a linked worktree).</param>
/// <param name="CommonDir">The repository's shared git directory.</param>
internal sealed record GitLocation(string Directory, string TopLevel, string Prefix, string GitDir, string CommonDir)
{
    /// <summary>
    /// Locates <paramref name="directory"/> in a git work tree. Null when git is not installed, the directory is not in a
    /// work tree (or is inside a <c>.git</c> directory), or git would not track its files: it is ignored, or it lies under
    /// <c>.reactor</c> or <c>node_modules</c>.
    /// </summary>
    public static async Task<GitLocation?> ProbeAsync(string directory, CancellationToken cancellationToken)
    {
        if (!System.IO.Directory.Exists(directory) || !await GitCli.IsAvailableAsync().ConfigureAwait(false))
        {
            return null;
        }

        var probe = await GitCli.RunAsync(
            directory,
            ["rev-parse", "--is-inside-work-tree", "--show-toplevel", "--show-prefix", "--git-dir", "--git-common-dir"],
            new GitCall { Timeout = TimeSpan.FromMinutes(1) },
            cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return null;
        }

        var lines = probe.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (lines.Length < 5 || lines[0] != "true" || lines[1].Length == 0)
        {
            return null;
        }

        var prefix = lines[2];
        if (prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(WalkSnapshotter.IsSkippedName))
        {
            return null;
        }

        if (prefix.Length > 0)
        {
            var ignored = await GitCli.RunAsync(directory, ["check-ignore", "-q", "."], new GitCall { Timeout = TimeSpan.FromMinutes(1) }, cancellationToken).ConfigureAwait(false);
            if (ignored.ExitCode == 0)
            {
                return null;
            }
        }

        var full = Path.GetFullPath(directory);
        return new GitLocation(
            full,
            Path.GetFullPath(lines[1]),
            prefix,
            Path.GetFullPath(lines[3], full),
            Path.GetFullPath(lines[4], full));
    }
}

/// <summary>
/// Git-backed snapshots of one directory that never touch the user's index, HEAD, branches, or files: a private index
/// file (<c>GIT_INDEX_FILE</c>, kept in the run directory and seeded from <c>HEAD</c> so later snapshots only rehash
/// changed files) records the directory with <c>git add -A</c> (honoring <c>.gitignore</c>; <c>.reactor</c>,
/// <c>node_modules</c>, and given paths are always left out), <c>git write-tree</c> turns it into a tree, and each new
/// tree becomes a commit (<c>git commit-tree</c>) on a ref under <c>refs/reactor/runs/</c>, which keeps the snapshots
/// reachable for audits and rewinds. Diffs compare trees with <c>git diff-tree</c>. Only the directory's part of the
/// repository is recorded when it is a subdirectory.
/// </summary>
internal sealed partial class GitSnapshotter : IWorkspaceSnapshotter, IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, string> Identity = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GIT_AUTHOR_NAME"] = "Reactor",
        ["GIT_AUTHOR_EMAIL"] = "reactor@localhost",
        ["GIT_COMMITTER_NAME"] = "Reactor",
        ["GIT_COMMITTER_EMAIL"] = "reactor@localhost",
    };

    private readonly GitCall _indexCall;
    private readonly List<string> _addArguments;
    private readonly GitObjectSizes _sizes;
    private Task _refUpdate = Task.CompletedTask;
    private string? _lastCommit;
    private string? _lastCommitTree;

    /// <summary>The tree last written from the private index (null until then, or after a partial add).</summary>
    private string? _indexTree;

    private GitSnapshotter(GitLocation location, string indexPath, string refName, IEnumerable<string> excludes)
    {
        Location = location;
        IndexPath = indexPath;
        RefName = refName;
        _sizes = new GitObjectSizes(location.Directory);
        _indexCall = new GitCall { Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = indexPath } };
        _addArguments = ["add", "-A", "--verbose", "--ignore-errors", "--", "."];
        _addArguments.AddRange(DefaultExcludes);
        _addArguments.AddRange(excludes);
    }

    /// <summary>Pathspecs every snapshot leaves out (relative to the snapshotted directory, at any depth).</summary>
    public static IReadOnlyList<string> DefaultExcludes { get; } = [":(exclude,glob)**/.reactor/**", ":(exclude,glob)**/node_modules/**"];

    public string Mode => "git";

    public string Directory => Location.Directory;

    public GitLocation Location { get; }

    /// <summary>The private index file.</summary>
    public string IndexPath { get; }

    /// <summary>The environment that points git at the private index.</summary>
    public GitCall IndexCall => _indexCall;

    /// <summary>The ref whose commit chain holds this directory's snapshots.</summary>
    public string RefName { get; }

    /// <summary>
    /// Opens snapshots of <paramref name="location"/>. A missing private index is seeded from <paramref name="seedIndex"/>
    /// (a copy, when that file exists) or from <c>HEAD</c>. The snapshot chain continues <paramref name="startCommit"/>
    /// (whose ref is set to it), else an existing <paramref name="refName"/> (a resumed run).
    /// </summary>
    /// <param name="location">The directory's place in its repository.</param>
    /// <param name="indexPath">The private index file to use (created if missing).</param>
    /// <param name="refName">The snapshot ref, e.g. <c>refs/reactor/runs/&lt;run&gt;/main</c>.</param>
    /// <param name="excludes">Further exclude pathspecs, relative to the directory (e.g. the run directory).</param>
    /// <param name="seedIndex">An index to copy as the starting point (a fresh worktree's own index), or null.</param>
    /// <param name="startCommit">The commit the chain starts from (a branch's fork snapshot), or null.</param>
    /// <param name="startTree">The tree of <paramref name="startCommit"/>.</param>
    /// <param name="cancellationToken">Cancels the git calls.</param>
    public static async Task<GitSnapshotter> OpenAsync(
        GitLocation location,
        string indexPath,
        string refName,
        IEnumerable<string> excludes,
        string? seedIndex,
        CancellationToken cancellationToken,
        string? startCommit = null,
        string? startTree = null)
    {
        var snapshotter = new GitSnapshotter(location, indexPath, refName, excludes);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        // One lookup for HEAD and the snapshot ref (each line: "<id> <type> <size>", or "<name> missing").
        var lookup = await GitCli.RunAsync(
            location.Directory,
            ["cat-file", "--batch-check=%(objectname)"],
            new GitCall { Input = $"HEAD^{{commit}}\n{refName}^{{commit}}\n{refName}^{{tree}}\n" },
            cancellationToken).ConfigureAwait(false);
        var ids = lookup.Output.Split('\n').Select(l => l.Trim()).ToArray();
        string? Id(int line) => line < ids.Length && IsObjectId(ids[line]) ? ids[line] : null;

        if (!File.Exists(indexPath))
        {
            if (seedIndex is not null && File.Exists(seedIndex))
            {
                File.Copy(seedIndex, indexPath);
            }
            else if (Id(0) is { } head)
            {
                // Starting from HEAD's entries means the first "git add -A" rehashes them once; later snapshots are
                // incremental (the index caches each file's stat data).
                await GitCli.RunCheckedAsync(location.Directory, ["read-tree", head], snapshotter._indexCall, cancellationToken).ConfigureAwait(false);
            }
        }

        if (startCommit is not null)
        {
            await GitCli.RunCheckedAsync(location.Directory, ["update-ref", "-m", "reactor snapshot", refName, startCommit], cancellationToken: cancellationToken).ConfigureAwait(false);
            snapshotter._lastCommit = startCommit;
            snapshotter._lastCommitTree = startTree;
        }
        else if (Id(1) is { } tip)
        {
            // A resumed run continues its chain.
            snapshotter._lastCommit = tip;
            snapshotter._lastCommitTree = Id(2);
        }

        return snapshotter;
    }

    public async Task<WorkspaceSnapshot> SnapshotAsync(string message, CancellationToken cancellationToken)
    {
        var (tree, skipped) = await WriteTreeAsync(cancellationToken).ConfigureAwait(false);
        var commit = await CommitAsync(tree, message, cancellationToken).ConfigureAwait(false);
        return new WorkspaceSnapshot { Id = commit ?? tree, Tree = tree, Commit = commit, Skipped = skipped };
    }

    /// <summary>A snapshot and its changes since <paramref name="before"/>: the commit and the diff run side by side.</summary>
    public async Task<(WorkspaceSnapshot After, ChangeSet Changes)> SnapshotAndDiffAsync(WorkspaceSnapshot before, string message, int detailed, CancellationToken cancellationToken)
    {
        var (tree, skipped) = await WriteTreeAsync(cancellationToken).ConfigureAwait(false);
        var commit = CommitAsync(tree, message, cancellationToken);
        var changes = DiffAsync(before, new WorkspaceSnapshot { Id = tree, Tree = tree }, detailed, cancellationToken);
        await Task.WhenAll(commit, changes).ConfigureAwait(false);
        var id = await commit.ConfigureAwait(false);
        return (new WorkspaceSnapshot { Id = id ?? tree, Tree = tree, Commit = id, Skipped = skipped }, await changes.ConfigureAwait(false));
    }

    /// <summary>Records the directory in the private index and writes it as a tree.</summary>
    private async Task<(string Tree, List<string> Skipped)> WriteTreeAsync(CancellationToken cancellationToken)
    {
        var add = await GitCli.RunAsync(Directory, _addArguments, _indexCall, cancellationToken).ConfigureAwait(false);
        if (add.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException("git add failed: " + add.Describe());
        }

        // "git add --verbose" names every path it adds or removes: when it names none, the index (which only our snapshots
        // change) still holds the last tree written from it.
        if (add.ExitCode == 0 && _indexTree is { } unchanged && add.Output.Trim().Length == 0)
        {
            return (unchanged, []);
        }

        // With --ignore-errors, files git cannot index (a nested repository without commits, a locked file) are skipped.
        var skipped = add.ExitCode == 0
            ? []
            : UnindexedFile().Matches(add.Error).Select(m => m.Groups["path"].Value).Distinct(StringComparer.Ordinal).ToList();
        var tree = (await GitCli.RunCheckedAsync(Directory, ["write-tree"], _indexCall, cancellationToken).ConfigureAwait(false)).FirstLine;
        _indexTree = add.ExitCode == 0 ? tree : null;
        return (tree, skipped);
    }

    /// <summary>
    /// The commit for <paramref name="tree"/> on <see cref="RefName"/>: the current tip when it already records this tree,
    /// else a new commit on top of it (the ref is updated). Null when committing failed (the tree is still usable).
    /// </summary>
    private async Task<string?> CommitAsync(string tree, string message, CancellationToken cancellationToken)
    {
        if (_lastCommit is not null && string.Equals(tree, _lastCommitTree, StringComparison.Ordinal))
        {
            return _lastCommit;
        }

        var arguments = new List<string> { "commit-tree", "--no-gpg-sign", tree, "-m", message };
        if (_lastCommit is not null)
        {
            arguments.InsertRange(2, ["-p", _lastCommit]);
        }

        var commit = await GitCli.RunAsync(Directory, arguments, new GitCall { Environment = Identity }, cancellationToken).ConfigureAwait(false);
        if (!commit.Succeeded || commit.FirstLine.Length == 0)
        {
            return null;
        }

        _lastCommit = commit.FirstLine;
        _lastCommitTree = tree;
        UpdateRefInBackground(_lastCommit);
        return _lastCommit;
    }

    /// <summary>
    /// Moves <see cref="RefName"/> to <paramref name="commit"/> after any earlier update, off the snapshot's critical path
    /// (the commit exists already; the ref only keeps it reachable). <see cref="DisposeAsync"/> waits for the updates.
    /// </summary>
    private void UpdateRefInBackground(string commit)
    {
        var previous = _refUpdate;
        _refUpdate = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                var update = await GitCli.RunAsync(Directory, ["update-ref", "-m", "reactor snapshot", RefName, commit], new GitCall { Timeout = TimeSpan.FromMinutes(1) }).ConfigureAwait(false);
                RefError = update.Succeeded ? null : update.Describe();
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
            {
                RefError = ex.Message;
            }
        });
    }

    /// <summary>Why the last ref update failed (the snapshots are then unreferenced), or null.</summary>
    public string? RefError { get; private set; }

    /// <summary>Waits for pending ref updates and stops the size lookup process.</summary>
    public async ValueTask DisposeAsync()
    {
        await _refUpdate.ConfigureAwait(false);
        await _sizes.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<ChangeSet> DiffAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, int detailed, CancellationToken cancellationToken)
    {
        if (before.Tree is null || after.Tree is null || string.Equals(before.Tree, after.Tree, StringComparison.Ordinal))
        {
            return ChangeSet.Empty;
        }

        var changes = await DiffTreesAsync(Directory, before.Tree, after.Tree, cancellationToken).ConfigureAwait(false);
        return new ChangeSet(await WithSizesAsync(changes, detailed, cancellationToken).ConfigureAwait(false), false);
    }

    /// <summary>
    /// The changes between two trees (or commits), with paths relative to <paramref name="directory"/> when it is a
    /// subdirectory of its work tree (changes outside it are left out).
    /// </summary>
    public static async Task<List<FileChange>> DiffTreesAsync(string directory, string from, string to, CancellationToken cancellationToken)
    {
        var diff = await GitCli.RunCheckedAsync(directory, ["diff-tree", "-r", "-z", "--no-renames", "--relative", from, to], cancellationToken: cancellationToken).ConfigureAwait(false);
        var changes = new List<FileChange>();
        var fields = diff.Output.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            // ":<old mode> <new mode> <old id> <new id> <status>" then the path.
            var meta = fields[i].TrimStart('\n').Split(' ');
            if (meta.Length < 5 || !meta[0].StartsWith(':'))
            {
                continue;
            }

            var oldMode = meta[0][1..];
            var newMode = meta[1];
            var kind = meta[4].Length > 0 ? meta[4][0] switch
            {
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                _ => ChangeKind.Modified,
            } : ChangeKind.Modified;
            var mode = kind == ChangeKind.Deleted ? oldMode : newMode;
            changes.Add(new FileChange(fields[i + 1], kind)
            {
                Id = kind == ChangeKind.Deleted ? null : meta[3],
                OldId = kind == ChangeKind.Added || meta[2].All(c => c == '0') ? null : meta[2],
                Type = mode switch
                {
                    "160000" => "submodule",
                    "120000" => "symlink",
                    _ => null,
                },
            });
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return changes;
    }

    /// <summary>Adds blob sizes to the first <paramref name="detailed"/> changes.</summary>
    private async Task<List<FileChange>> WithSizesAsync(List<FileChange> changes, int detailed, CancellationToken cancellationToken)
    {
        var ids = changes.Take(detailed)
            .Where(c => c.Type != "submodule")
            .SelectMany(c => new[] { c.Id, c.OldId })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return changes;
        }

        var sizes = await _sizes.SizesAsync(ids, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < changes.Count && i < detailed; i++)
        {
            var change = changes[i];
            changes[i] = change with
            {
                Size = change.Id is { } id && sizes.TryGetValue(id, out var size) ? size : null,
                OldSize = change.OldId is { } oldId && sizes.TryGetValue(oldId, out var oldSize) ? oldSize : null,
            };
        }

        return changes;
    }

    /// <summary>True for a full object id (SHA-1 or SHA-256, hex).</summary>
    internal static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    [GeneratedRegex(@"unable to index file '(?<path>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex UnindexedFile();
}

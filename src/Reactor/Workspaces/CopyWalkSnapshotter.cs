namespace Reactor.Workspaces;

/// <summary>
/// The ignore rules of the git work tree a copied branch workspace came from. The copy holds only the files git does not
/// ignore, so its walks must leave ignored files out too: the build output and caches a branch creates (<c>dist/</c>,
/// <c>bin/</c>, <c>__pycache__/</c>) are then neither its changes (<c>changes.json</c>, <c>write_scope</c>) nor promoted
/// (nor conflicts with the working directory's own build output), just as in a worktree. Each path is asked of
/// <c>git check-ignore</c> in the source directory once.
/// </summary>
internal sealed class GitIgnoreFilter(string sourceDirectory)
{
    private readonly Dictionary<string, bool> _ignored = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The snapshot without the files the source work tree ignores (unchanged when git cannot say).</summary>
    public async Task<WorkspaceSnapshot> ApplyAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Manifest is not { Count: > 0 } manifest)
        {
            return snapshot;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var unknown = manifest.Keys.Where(path => !_ignored.ContainsKey(path)).ToList();
            if (unknown.Count > 0)
            {
                // Exit code 0: some paths are ignored (listed); 1: none is.
                var result = await GitCli.RunAsync(
                    sourceDirectory,
                    ["check-ignore", "-z", "--stdin"],
                    new GitCall { Input = string.Join('\0', unknown) + "\0" },
                    cancellationToken).ConfigureAwait(false);
                if (result.ExitCode is not (0 or 1))
                {
                    return snapshot;
                }

                var ignored = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                foreach (var path in unknown)
                {
                    _ignored[path] = ignored.Contains(path);
                }
            }

            if (!manifest.Keys.Any(path => _ignored[path]))
            {
                return snapshot;
            }

            var kept = manifest.Where(kv => !_ignored[kv.Key]).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return snapshot with { Id = "walk:" + WalkSnapshotter.Digest(kept), Manifest = kept };
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>File-walk snapshots of a branch copied from a git work tree, without the files that work tree ignores.</summary>
internal sealed class CopyWalkSnapshotter(WalkSnapshotter walk, GitIgnoreFilter ignore) : IWorkspaceSnapshotter
{
    public string Mode => walk.Mode;

    public string Directory => walk.Directory;

    public async Task<WorkspaceSnapshot> SnapshotAsync(string message, CancellationToken cancellationToken) =>
        await ignore.ApplyAsync(await walk.SnapshotAsync(message, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public Task<ChangeSet> DiffAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, int detailed, CancellationToken cancellationToken) =>
        Task.FromResult(WalkSnapshotter.Diff(before, after));

    public async Task<(WorkspaceSnapshot After, ChangeSet Changes)> SnapshotAndDiffAsync(WorkspaceSnapshot before, string message, int detailed, CancellationToken cancellationToken)
    {
        var after = await SnapshotAsync(message, cancellationToken).ConfigureAwait(false);
        return (after, WalkSnapshotter.Diff(before, after));
    }
}

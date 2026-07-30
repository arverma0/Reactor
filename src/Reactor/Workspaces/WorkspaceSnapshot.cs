using System.Globalization;
using System.Text.Json.Nodes;

namespace Reactor.Workspaces;

/// <summary>One file of a file-walk manifest: size, last write time, and SHA-256 (null for files too large to hash).</summary>
internal readonly record struct FileState(long Size, long ModifiedTicks, string? Sha256);

/// <summary>
/// A point-in-time record of a workspace. In git workspaces it is a tree (and usually a commit on the run's snapshot ref);
/// elsewhere it is a file-walk manifest identified by a digest (<c>walk:…</c>).
/// </summary>
internal sealed record WorkspaceSnapshot
{
    /// <summary>The commit id (git, when committed), the tree id (git), or <c>walk:&lt;digest&gt;</c>.</summary>
    public required string Id { get; init; }

    public string? Tree { get; init; }

    public string? Commit { get; init; }

    public DateTimeOffset TakenAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The walk stopped at its file limit, so the manifest covers only part of the workspace.</summary>
    public bool Truncated { get; init; }

    /// <summary>Paths git could not index (e.g. a nested repository without commits); their contents are not recorded.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>The file-walk manifest (relative path → state); null in git workspaces.</summary>
    internal IReadOnlyDictionary<string, FileState>? Manifest { get; init; }

    /// <summary>For a truncated walk, the last path it covered (in walk order).</summary>
    internal string? Boundary { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["id"] = Id };
        if (Commit is not null)
        {
            json["commit"] = Commit;
        }

        if (Tree is not null)
        {
            json["tree"] = Tree;
        }

        if (Manifest is not null)
        {
            json["files"] = Manifest.Count;
        }

        json["taken_at"] = TakenAt.ToString("O", CultureInfo.InvariantCulture);
        if (Truncated)
        {
            json["truncated"] = true;
        }

        return json;
    }
}

/// <summary>How a path changed between two snapshots.</summary>
internal enum ChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>
/// One changed path (relative to the stage's working directory, <c>/</c>-separated). <see cref="Id"/>/<see cref="OldId"/>
/// are git blob ids in git workspaces and SHA-256 digests otherwise.
/// </summary>
internal sealed record FileChange(string Path, ChangeKind Kind)
{
    public long? Size { get; init; }

    public long? OldSize { get; init; }

    public string? Id { get; init; }

    public string? OldId { get; init; }

    /// <summary><c>submodule</c> or <c>symlink</c> for entries that are not regular files.</summary>
    public string? Type { get; init; }

    public static string Wire(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Modified => "modified",
        _ => "deleted",
    };
}

/// <summary>The changes between two snapshots, sorted by path.</summary>
internal sealed record ChangeSet(IReadOnlyList<FileChange> Changes, bool Truncated)
{
    public static ChangeSet Empty { get; } = new([], false);

    public int Added => Changes.Count(c => c.Kind == ChangeKind.Added);

    public int Modified => Changes.Count(c => c.Kind == ChangeKind.Modified);

    public int Deleted => Changes.Count(c => c.Kind == ChangeKind.Deleted);
}

/// <summary>Takes snapshots of one directory and compares them.</summary>
internal interface IWorkspaceSnapshotter
{
    /// <summary><c>git</c> or <c>walk</c>.</summary>
    string Mode { get; }

    /// <summary>The directory snapshotted (full path).</summary>
    string Directory { get; }

    /// <summary>Records the directory's current state; <paramref name="message"/> labels the snapshot (git commit message).</summary>
    Task<WorkspaceSnapshot> SnapshotAsync(string message, CancellationToken cancellationToken);

    /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>; sizes are filled for the first <paramref name="detailed"/> entries.</summary>
    Task<ChangeSet> DiffAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, int detailed, CancellationToken cancellationToken);

    /// <summary>Takes a snapshot and compares it with <paramref name="before"/> (implementations may overlap the two).</summary>
    Task<(WorkspaceSnapshot After, ChangeSet Changes)> SnapshotAndDiffAsync(WorkspaceSnapshot before, string message, int detailed, CancellationToken cancellationToken);
}

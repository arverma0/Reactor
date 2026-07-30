using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reactor.Workspaces;

/// <summary>
/// File-walk snapshots of a directory outside git (or in a copied branch workspace): every file's size, last write time,
/// and SHA-256, with a cache so only new or changed files (by size and time) are hashed again. Directories named
/// <c>.git</c>, <c>node_modules</c>, or <c>.reactor</c>, directories the caller excludes (the run directory), and
/// symbolic links to directories are skipped. The walk visits directories in ordinal name order and stops after
/// <see cref="FileLimit"/> files; a truncated snapshot only compares the paths it covered.
/// </summary>
internal sealed class WalkSnapshotter : IWorkspaceSnapshotter
{
    /// <summary>Default <see cref="FileLimit"/>.</summary>
    public const int DefaultFileLimit = 20_000;

    /// <summary>Files larger than this are compared by size and write time only.</summary>
    public const long MaxHashedBytes = 64L * 1024 * 1024;

    private static readonly string[] SkippedNames = [".git", "node_modules", ".reactor"];

    private readonly Dictionary<string, FileState> _cache = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _skipDirectory;
    private readonly object _gate = new();

    public WalkSnapshotter(string directory, Func<string, bool>? skipDirectory = null, int fileLimit = DefaultFileLimit)
    {
        Directory = Path.GetFullPath(directory);
        FileLimit = Math.Max(1, fileLimit);
        _skipDirectory = skipDirectory ?? (_ => false);
    }

    public string Mode => "walk";

    public string Directory { get; }

    /// <summary>Maximum number of files a snapshot records.</summary>
    public int FileLimit { get; }

    /// <summary>True for directory names every snapshot skips (<c>.git</c>, <c>node_modules</c>, <c>.reactor</c>).</summary>
    public static bool IsSkippedName(string name) => SkippedNames.Contains(name, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Tells the cache a file's state (e.g. hashed while copying it), so the next walk does not read it again.</summary>
    public void Remember(string relativePath, FileState state)
    {
        lock (_gate)
        {
            _cache[relativePath] = state;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> (keeping its last write time) and hashes the
    /// content on the way, returning the copy's state.
    /// </summary>
    public static FileState CopyAndHash(string source, string destination)
    {
        long length;
        string sha;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }

            length = output.Length;
            sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        return new FileState(length, File.GetLastWriteTimeUtc(destination).Ticks, length <= MaxHashedBytes ? sha : null);
    }

    public Task<WorkspaceSnapshot> SnapshotAsync(string message, CancellationToken cancellationToken) =>
        Task.Run(() => Walk(cancellationToken), cancellationToken);

    /// <summary>Records the directory's state now (synchronously).</summary>
    public WorkspaceSnapshot Walk(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var manifest = new Dictionary<string, FileState>(StringComparer.Ordinal);
            var skipped = new List<string>();
            string? last = null;
            var truncated = false;
            if (System.IO.Directory.Exists(Directory))
            {
                truncated = !Visit(new DirectoryInfo(Directory), "", manifest, skipped, ref last, cancellationToken);
            }

            // Forget files that are gone so the cache does not grow without bound.
            foreach (var stale in _cache.Keys.Where(k => !manifest.ContainsKey(k)).ToList())
            {
                _cache.Remove(stale);
            }

            return new WorkspaceSnapshot
            {
                Id = "walk:" + Digest(manifest),
                Manifest = manifest,
                Truncated = truncated,
                Boundary = truncated ? last : null,
                Skipped = skipped,
            };
        }
    }

    /// <summary>Walks one directory; false when the file limit was reached.</summary>
    private bool Visit(DirectoryInfo directory, string relative, Dictionary<string, FileState> manifest, List<string> skipped, ref string? last, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(relative.Length == 0 ? "." : relative + "/");
            return true;
        }

        Array.Sort(entries, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var entry in entries)
        {
            var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (entry is DirectoryInfo child)
            {
                if (IsSkippedName(child.Name) || child.Attributes.HasFlag(FileAttributes.ReparsePoint) || _skipDirectory(child.FullName))
                {
                    continue;
                }

                if (!Visit(child, path, manifest, skipped, ref last, cancellationToken))
                {
                    return false;
                }

                continue;
            }

            if (manifest.Count >= FileLimit)
            {
                return false;
            }

            var file = (FileInfo)entry;
            manifest[path] = State(file, path, skipped);
            last = path;
        }

        return true;
    }

    private FileState State(FileInfo file, string path, List<string> skipped)
    {
        long size;
        long ticks;
        try
        {
            size = file.Length;
            ticks = file.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(path);
            return new FileState(0, 0, null);
        }

        if (_cache.TryGetValue(path, out var cached) && cached.Size == size && cached.ModifiedTicks == ticks)
        {
            return cached;
        }

        string? sha = null;
        if (size <= MaxHashedBytes)
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(path);
            }
        }

        var state = new FileState(size, ticks, sha);
        if (sha is not null || size > MaxHashedBytes)
        {
            _cache[path] = state;
        }

        return state;
    }

    public Task<ChangeSet> DiffAsync(WorkspaceSnapshot before, WorkspaceSnapshot after, int detailed, CancellationToken cancellationToken) =>
        Task.FromResult(Diff(before, after));

    public async Task<(WorkspaceSnapshot After, ChangeSet Changes)> SnapshotAndDiffAsync(WorkspaceSnapshot before, string message, int detailed, CancellationToken cancellationToken)
    {
        var after = await SnapshotAsync(message, cancellationToken).ConfigureAwait(false);
        return (after, Diff(before, after));
    }

    /// <summary>The changes between two walk snapshots (only paths both covered when either was truncated).</summary>
    public static ChangeSet Diff(WorkspaceSnapshot before, WorkspaceSnapshot after)
    {
        var from = before.Manifest ?? new Dictionary<string, FileState>();
        var to = after.Manifest ?? new Dictionary<string, FileState>();
        var boundary = (before.Boundary, after.Boundary) switch
        {
            (null, null) => null,
            (null, var b) => b,
            (var a, null) => a,
            var (a, b) => ComparePaths(a!, b!) <= 0 ? a : b,
        };
        bool Covered(string path) => boundary is null || ComparePaths(path, boundary) <= 0;

        var changes = new List<FileChange>();
        foreach (var (path, state) in to)
        {
            if (!Covered(path))
            {
                continue;
            }

            if (!from.TryGetValue(path, out var old))
            {
                changes.Add(new FileChange(path, ChangeKind.Added) { Size = state.Size, Id = state.Sha256 });
            }
            else if (!Same(old, state))
            {
                changes.Add(new FileChange(path, ChangeKind.Modified) { Size = state.Size, OldSize = old.Size, Id = state.Sha256, OldId = old.Sha256 });
            }
        }

        foreach (var (path, old) in from)
        {
            if (Covered(path) && !to.ContainsKey(path))
            {
                changes.Add(new FileChange(path, ChangeKind.Deleted) { OldSize = old.Size, OldId = old.Sha256 });
            }
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ChangeSet(changes, boundary is not null);
    }

    /// <summary>True when two states describe the same content (by digest when both have one, else by size and time).</summary>
    public static bool Same(FileState a, FileState b) =>
        a.Sha256 is not null && b.Sha256 is not null
            ? a.Size == b.Size && string.Equals(a.Sha256, b.Sha256, StringComparison.Ordinal)
            : a.Size == b.Size && a.ModifiedTicks == b.ModifiedTicks;

    /// <summary>Walk order: segment by segment, ordinal.</summary>
    internal static int ComparePaths(string a, string b)
    {
        var left = a.Split('/');
        var right = b.Split('/');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var order = string.CompareOrdinal(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    internal static string Digest(IReadOnlyDictionary<string, FileState> manifest)
    {
        var sb = new StringBuilder();
        foreach (var (path, state) in manifest.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(path).Append('\0').Append(state.Size.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(state.Sha256 ?? state.ModifiedTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..20];
    }
}

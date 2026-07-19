using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reactor.Execution;

/// <summary>Metadata for a stored artifact.</summary>
public sealed record ArtifactInfo(string Id, string Name, long SizeBytes, DateTimeOffset StoredAt, bool IsFileBacked);

/// <summary>
/// Named storage for large stage outputs that do not belong in the context. Artifacts larger than the file-backing
/// threshold (100 KB by default) are written to <c>{baseDir}/artifacts/{id}.json</c> when a base directory is configured;
/// smaller ones are kept in memory. Thread-safe.
/// </summary>
public sealed partial class ArtifactStore
{
    public const long DefaultFileBackingThreshold = 100 * 1024;

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, (ArtifactInfo Info, object? Data)> _artifacts = new(StringComparer.Ordinal);

    public ArtifactStore(string? baseDirectory = null, long fileBackingThreshold = DefaultFileBackingThreshold)
    {
        BaseDirectory = baseDirectory;
        FileBackingThreshold = fileBackingThreshold;
    }

    public string? BaseDirectory { get; }

    public long FileBackingThreshold { get; }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _artifacts.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Stores (or replaces) an artifact.</summary>
    public ArtifactInfo Store(string artifactId, string name, object? data)
    {
        if (string.IsNullOrWhiteSpace(artifactId) || !IdPattern().IsMatch(artifactId))
        {
            throw new ArgumentException($"Invalid artifact id '{artifactId}': use letters, digits, '.', '_' or '-'.", nameof(artifactId));
        }

        var normalized = ContextValues.Normalize(data);
        var json = ContextValues.ToJson(normalized)?.ToJsonString() ?? "null";
        var size = Encoding.UTF8.GetByteCount(json);
        var fileBacked = size > FileBackingThreshold && BaseDirectory is not null;
        object? stored = normalized;
        if (fileBacked)
        {
            var directory = Path.Combine(BaseDirectory!, "artifacts");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, artifactId + ".json");
            File.WriteAllText(path, json);
            stored = path;
        }

        var info = new ArtifactInfo(artifactId, name ?? artifactId, size, DateTimeOffset.UtcNow, fileBacked);
        _lock.EnterWriteLock();
        try
        {
            _artifacts[artifactId] = (info, stored);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return info;
    }

    /// <summary>Retrieves an artifact's data.</summary>
    /// <exception cref="KeyNotFoundException">No artifact with that id exists.</exception>
    public object? Retrieve(string artifactId)
    {
        (ArtifactInfo Info, object? Data) entry;
        _lock.EnterReadLock();
        try
        {
            if (!_artifacts.TryGetValue(artifactId, out entry))
            {
                throw new KeyNotFoundException($"Artifact not found: {artifactId}");
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (entry.Info.IsFileBacked)
        {
            using var stream = File.OpenRead((string)entry.Data!);
            return ContextValues.FromJson(JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { MaxDepth = 256 }));
        }

        return ContextValues.DeepCopy(entry.Data);
    }

    public bool Has(string artifactId)
    {
        _lock.EnterReadLock();
        try
        {
            return _artifacts.ContainsKey(artifactId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<ArtifactInfo> List()
    {
        _lock.EnterReadLock();
        try
        {
            return _artifacts.Values.Select(v => v.Info).OrderBy(i => i.StoredAt).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Removes an artifact (deleting its backing file).</summary>
    public bool Remove(string artifactId)
    {
        (ArtifactInfo Info, object? Data) entry;
        _lock.EnterWriteLock();
        try
        {
            if (!_artifacts.Remove(artifactId, out entry))
            {
                return false;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        DeleteBackingFile(entry);
        return true;
    }

    public void Clear()
    {
        List<(ArtifactInfo Info, object? Data)> entries;
        _lock.EnterWriteLock();
        try
        {
            entries = _artifacts.Values.ToList();
            _artifacts.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        foreach (var entry in entries)
        {
            DeleteBackingFile(entry);
        }
    }

    private static void DeleteBackingFile((ArtifactInfo Info, object? Data) entry)
    {
        if (entry.Info.IsFileBacked && entry.Data is string path && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex IdPattern();
}

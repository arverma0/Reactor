namespace Reactor.Execution;

/// <summary>
/// Thread-safe key/value store shared across stages of a pipeline run, plus an append-only run log.
/// Values are normalized to a JSON-compatible model (see <see cref="ContextValues"/>).
/// </summary>
public sealed class PipelineContext
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, object?> _values;
    private readonly List<string> _logs;

    public PipelineContext()
    {
        _values = new Dictionary<string, object?>(StringComparer.Ordinal);
        _logs = [];
    }

    private PipelineContext(Dictionary<string, object?> values, List<string> logs, ArtifactStore? artifacts, RunServices? run)
    {
        _values = values;
        _logs = logs;
        Artifacts = artifacts;
        Run = run;
    }

    /// <summary>Artifact storage for large outputs (transient; not part of snapshots, shared with clones).</summary>
    public ArtifactStore? Artifacts { get; set; }

    /// <summary>Per-run services (transient; not part of snapshots, shared with clones). Null outside an engine run.</summary>
    public RunServices? Run { get; set; }

    /// <summary>
    /// Context key holding the working directory of a parallel branch that runs in its own workspace
    /// (<c>branch_workdir="worktree"</c> or <c>"copy"</c>, a Reactor extension). Set on the branch's context clone only.
    /// </summary>
    public const string WorkingDirectoryKey = "internal.working_directory";

    /// <summary>
    /// The directory the current stage works in: the branch workspace (<see cref="WorkingDirectoryKey"/>) inside an isolated
    /// parallel branch, else the run's working directory, else the current directory. Tools, agents, artifact files, and
    /// child pipelines all resolve against it.
    /// </summary>
    public string WorkingDirectory =>
        GetString(WorkingDirectoryKey) is { Length: > 0 } branch ? branch : Run?.WorkingDirectory ?? Directory.GetCurrentDirectory();

    public void Set(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var normalized = ContextValues.Normalize(value);
        _lock.EnterWriteLock();
        try
        {
            _values[key] = normalized;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public object? Get(string key, object? defaultValue = null)
    {
        _lock.EnterReadLock();
        try
        {
            return _values.TryGetValue(key, out var value) ? ContextValues.DeepCopy(value) : defaultValue;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool TryGet(string key, out object? value)
    {
        _lock.EnterReadLock();
        try
        {
            if (_values.TryGetValue(key, out var stored))
            {
                value = ContextValues.DeepCopy(stored);
                return true;
            }

            value = null;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool ContainsKey(string key)
    {
        _lock.EnterReadLock();
        try
        {
            return _values.ContainsKey(key);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>The value as a string (see <see cref="ContextValues.ToDisplayString"/>), or <paramref name="defaultValue"/> when absent/null.</summary>
    public string GetString(string key, string defaultValue = "")
    {
        var value = Get(key);
        return value is null ? defaultValue : ContextValues.ToDisplayString(value);
    }

    public bool Remove(string key)
    {
        _lock.EnterWriteLock();
        try
        {
            return _values.Remove(key);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void AppendLog(string entry)
    {
        _lock.EnterWriteLock();
        try
        {
            _logs.Add(entry);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public IReadOnlyList<string> Logs
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _logs.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>A serializable deep copy of all values.</summary>
    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return _values.ToDictionary(kv => kv.Key, kv => ContextValues.DeepCopy(kv.Value), StringComparer.Ordinal);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Deep copy for parallel branch isolation (values and logs copied; artifact store shared).</summary>
    public PipelineContext Clone()
    {
        _lock.EnterReadLock();
        try
        {
            return new PipelineContext(
                _values.ToDictionary(kv => kv.Key, kv => ContextValues.DeepCopy(kv.Value), StringComparer.Ordinal),
                _logs.ToList(),
                Artifacts,
                Run);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Merges updates into the context atomically.</summary>
    public void ApplyUpdates(IEnumerable<KeyValuePair<string, object?>> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var normalized = updates.Select(kv => (kv.Key, Value: ContextValues.Normalize(kv.Value))).ToList();
        _lock.EnterWriteLock();
        try
        {
            foreach (var (key, value) in normalized)
            {
                _values[key] = value;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Replaces all values and logs (used when restoring from a checkpoint).</summary>
    public void Restore(IReadOnlyDictionary<string, object?> values, IEnumerable<string> logs)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(logs);
        _lock.EnterWriteLock();
        try
        {
            _values.Clear();
            foreach (var (key, value) in values)
            {
                _values[key] = ContextValues.Normalize(value);
            }

            _logs.Clear();
            _logs.AddRange(logs);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}

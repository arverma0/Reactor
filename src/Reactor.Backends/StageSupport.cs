using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Backends;

/// <summary>Per-stage settings a backend derives from the node attributes and the run context.</summary>
public sealed record StageSettings
{
    public required string NodeId { get; init; }

    /// <summary><c>llm_provider</c> after stylesheet resolution, if set.</summary>
    public string? Provider { get; init; }

    /// <summary><c>llm_model</c> after stylesheet resolution, if set.</summary>
    public string? Model { get; init; }

    /// <summary><c>reasoning_effort</c> only when explicitly set on the node (or by the stylesheet).</summary>
    public string? ExplicitReasoningEffort { get; init; }

    /// <summary>The node's reasoning effort with the spec default (<c>high</c>) applied.</summary>
    public string ReasoningEffort { get; init; } = "high";

    public string Fidelity { get; init; } = FidelityModes.Default;

    /// <summary>The conversation thread key, set only for <c>full</c> fidelity (session reuse).</summary>
    public string? ThreadId { get; init; }

    public string RunId { get; init; } = "";

    public required string WorkingDirectory { get; init; }

    /// <summary>The stage's log directory, or null when running outside a pipeline run.</summary>
    public string? StageDirectory { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Identifies one engine execution: a fresh run, a resume, and each <c>loop_restart</c> get distinct scopes, while
    /// parallel branches share their run's scope. Thread-scoped sessions never cross scopes.
    /// </summary>
    public string ExecutionScope { get; init; } = "";

    private static readonly ConditionalWeakTable<RunServices, string> Scopes = new();

    public static StageSettings From(Node node, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        var run = context.Run;
        var fidelity = context.GetString("internal.fidelity", FidelityModes.Default);
        var thread = fidelity == FidelityModes.Full ? context.GetString("internal.thread_id") : "";
        return new StageSettings
        {
            NodeId = node.Id,
            Provider = node.LlmProvider,
            Model = node.LlmModel,
            ExplicitReasoningEffort = string.IsNullOrWhiteSpace(node[AttrKeys.ReasoningEffort]) ? null : node.ReasoningEffort,
            ReasoningEffort = node.ReasoningEffort,
            Fidelity = fidelity,
            ThreadId = string.IsNullOrWhiteSpace(thread) ? null : thread,
            RunId = run?.RunId ?? context.GetString("internal.run_id"),
            WorkingDirectory = Path.GetFullPath(run?.WorkingDirectory ?? Directory.GetCurrentDirectory()),
            StageDirectory = run?.StageDirectory(node.Id),
            Timeout = node.Timeout,
            ExecutionScope = run is null ? "" : Scopes.GetValue(run, _ => Guid.NewGuid().ToString("N")),
        };
    }

    /// <summary>Key for thread-scoped session reuse (null when the stage does not share a thread).</summary>
    public string? ThreadKey(string scope) => ThreadId is null ? null : string.Join('\u001f', RunId, ExecutionScope, scope, ThreadId);
}

/// <summary>
/// Holds thread-scoped resources (agent sessions, CLI session ids) for <c>full</c>-fidelity reuse. A resource is leased to
/// one stage at a time; when parallel branches contend for the same thread, the contender gets a fresh, unpooled
/// resource (a fork) instead of waiting. Idle entries are evicted after <see cref="IdleTimeout"/>. A lease reports
/// <see cref="Lease.ContextLost"/> when its thread had a resource before but this lease got a fresh one (invalidated,
/// evicted, or forked), so the caller can carry the earlier context over another way.
/// </summary>
internal sealed class ThreadLeasePool<T>(Func<T, ValueTask>? dispose = null)
    where T : class
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.Ordinal);

    /// <summary>Evict resources unused for this long (null = never).</summary>
    public TimeSpan? IdleTimeout { get; init; } = TimeSpan.FromHours(6);

    public int Count => _entries.Count;

    /// <summary>Leases the resource for <paramref name="key"/> (creating it on first use); null key = always fresh.</summary>
    public async Task<Lease> AcquireAsync(string? key, Func<T> create)
    {
        await EvictIdleAsync().ConfigureAwait(false);
        if (key is null)
        {
            return new Lease(create(), null, this, pooled: false, isNew: true, contextLost: false);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry());
            if (!entry.Gate.Wait(0))
            {
                // Another stage holds this thread (parallel branches): work on a fork rather than wait.
                return new Lease(create(), null, this, pooled: false, isNew: true, contextLost: true);
            }

            try
            {
                if (entry.Removed)
                {
                    // Evicted between the lookup and the lock; look it up again.
                    entry.Gate.Release();
                    continue;
                }

                var isNew = entry.Value is null;
                var lost = isNew && _created.ContainsKey(key);
                if (isNew)
                {
                    entry.Value = create();
                    _created[key] = 0;
                }

                entry.LastUsed = DateTimeOffset.UtcNow;
                return new Lease(entry.Value!, entry, this, pooled: true, isNew, lost);
            }
            catch
            {
                entry.Gate.Release();
                throw;
            }
        }

        return new Lease(create(), null, this, pooled: false, isNew: true, contextLost: _created.ContainsKey(key));
    }

    /// <summary>Drops (and disposes) a pooled resource, e.g. after its session closed.</summary>
    public async Task InvalidateAsync(Lease lease)
    {
        if (lease.Entry is { } entry && entry.Value is { } value)
        {
            entry.Value = null;
            if (dispose is not null)
            {
                await dispose(value).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAllAsync()
    {
        foreach (var (key, entry) in _entries.ToList())
        {
            entry.Removed = true;
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            var value = entry.Value;
            entry.Value = null;
            if (value is not null && dispose is not null)
            {
                await dispose(value).ConfigureAwait(false);
            }
        }
    }

    private async Task EvictIdleAsync()
    {
        if (IdleTimeout is not { } idle)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - idle;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastUsed >= cutoff || !entry.Gate.Wait(0))
            {
                continue;
            }

            T? value;
            try
            {
                entry.Removed = true;
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                value = entry.Value;
                entry.Value = null;
            }
            finally
            {
                entry.Gate.Release();
            }

            if (value is not null && dispose is not null)
            {
                await dispose(value).ConfigureAwait(false);
            }
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public T? Value { get; set; }

        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Set (under the gate) when the entry leaves the pool; holders of a stale reference must look up again.</summary>
        public bool Removed { get; set; }
    }

    internal sealed class Lease(T value, Entry? entry, ThreadLeasePool<T> pool, bool pooled, bool isNew, bool contextLost) : IAsyncDisposable
    {
        private int _released;

        public T Value { get; } = value;

        public Entry? Entry { get; } = entry;

        /// <summary>False for forks and thread-less stages: the caller owns (and must dispose) the resource.</summary>
        public bool Pooled { get; } = pooled;

        /// <summary>True when the resource was created for this lease.</summary>
        public bool IsNew { get; } = isNew;

        /// <summary>True when the thread had a resource before, but this lease got a fresh one without its history.</summary>
        public bool ContextLost { get; } = contextLost;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            if (Entry is { } entry)
            {
                entry.LastUsed = DateTimeOffset.UtcNow;
                entry.Gate.Release();
            }
            else if (!Pooled)
            {
                await pool.DisposeUnpooledAsync(Value).ConfigureAwait(false);
            }
        }
    }

    private ValueTask DisposeUnpooledAsync(T value) => dispose?.Invoke(value) ?? ValueTask.CompletedTask;
}

/// <summary>Carries pipeline context into a stage whose thread conversation is unavailable.</summary>
internal static class ThreadContinuity
{
    public const string Notice =
        "Note: the earlier conversation for this stage's thread is not available (the agent session was restarted or " +
        "forked), so here is a summary of the pipeline so far.";

    /// <summary>
    /// For a <c>full</c>-fidelity stage that had to start a fresh session although its thread already had a session,
    /// prepends a <c>summary:high</c> preamble (as the engine does after a resume) so the agent does not start blind. The
    /// prompt actually sent is saved as <c>prompt.effective.md</c>.
    /// </summary>
    public static string Recover(string prompt, StageSettings stage, PipelineContext context, Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Run is not { } run)
        {
            return prompt;
        }

        var preamble = PreambleBuilder.Build(FidelityModes.SummaryHigh, run.Graph, context, run.History, run.RunId);
        if (preamble.Length == 0)
        {
            return prompt;
        }

        log("thread conversation unavailable (session restarted or forked); continuing with a summary of earlier stages", "warn");
        var effective = $"{Notice}\n\n{preamble}\n\n---\n\n{prompt}";
        if (stage.StageDirectory is { } dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "prompt.effective.md"), effective);
            }
            catch (IOException)
            {
            }
        }

        return effective;
    }

    /// <summary>How a stage's thread conversation was obtained (recorded in <c>backend.json</c>).</summary>
    public static string? Describe(StageSettings stage, bool isNew, bool contextLost) =>
        stage.ThreadId is null ? null : contextLost ? "recovered" : isNew ? "new" : "continued";
}

/// <summary>Appends backend activity to <c>{stage}/agent-events.jsonl</c> (one JSON object per line).</summary>
internal sealed class StageEventLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    public StageEventLog(string? stageDirectory, string fileName = "agent-events.jsonl")
    {
        if (stageDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(stageDirectory);
        _writer = new StreamWriter(new FileStream(Path.Combine(stageDirectory, fileName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
    }

    public void Write(JsonObject record)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            _writer.WriteLine(record.ToJsonString(JsonOptions));
            _writer.Flush();
        }
    }

    /// <summary>Writes an already-serialized JSON line (e.g. raw CLI output).</summary>
    public void WriteRaw(string line)
    {
        if (_writer is null || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}

/// <summary>Small formatting helpers for stage logs.</summary>
internal static class LogText
{
    public static string Clip(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    /// <summary>A compact one-line summary of tool arguments (well-known keys first).</summary>
    public static string SummarizeArguments(JsonNode? arguments)
    {
        if (arguments is not JsonObject obj)
        {
            return Clip(arguments?.ToJsonString(), 120);
        }

        foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "task", "agent_id", "description" })
        {
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0)
            {
                return Clip(s, 120);
            }
        }

        return Clip(obj.ToJsonString(), 120);
    }
}

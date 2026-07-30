using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reactor.Execution;

namespace Reactor.Server.Studio;

/// <summary>
/// The history of pipeline runs on disk: every run directory under the server's runs directory (runs started by this
/// server, by earlier server processes, and by the <c>reactor run</c> CLI alike), grouped with its <c>-restart-N</c>
/// loop-restart directories and merged with the live state of the runs this server manages. Parsed run data is cached
/// per directory and refreshed incrementally as <c>events.jsonl</c> grows, so listing hundreds of runs stays cheap. Files
/// are opened with full sharing (runs may still be writing them), and partial or corrupt files (a run killed mid-write)
/// are tolerated.
/// </summary>
public sealed partial class RunHistory
{
    internal const string ManifestFile = "manifest.json";
    internal const string ResultFile = "result.json";
    internal const string EventsFile = "events.jsonl";
    internal const string GraphFile = "graph.dot";
    internal const string SimulationFile = "simulation.json";

    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly char[] InvalidNameChars = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];
    private readonly PipelineRunManager _manager;
    private readonly ConcurrentDictionary<string, SegmentState> _cache = new(PathComparer);

    public RunHistory(PipelineRunManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        BaseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manager.RunsDirectory));
    }

    /// <summary>The runs directory: run directories are its direct children.</summary>
    public string BaseDirectory { get; }

    /// <summary>
    /// All runs, newest first: run directories with a <c>manifest.json</c> (loop-restart directories folded into their
    /// run), plus runs this server manages that have not written a manifest yet (queued runs).
    /// </summary>
    internal IReadOnlyList<RunRecord> LoadAll()
    {
        var groups = ScanGroups();
        var live = LiveRuns();
        var records = new List<RunRecord>(groups.Count);
        var seen = new HashSet<string>(PathComparer);
        var listed = new HashSet<string>(PathComparer);
        foreach (var segments in groups)
        {
            var basePath = segments[0].Path;
            seen.UnionWith(segments.Select(s => s.Path));
            if (Build(basePath, segments, live.GetValueOrDefault(basePath)) is { } record)
            {
                records.Add(record);
                listed.Add(basePath);
            }
        }

        foreach (var (path, run) in live)
        {
            if (!listed.Contains(path))
            {
                records.Add(RunRecord.ForUnstartedRun(Path.GetFileName(path), path, run));
            }
        }

        foreach (var key in _cache.Keys)
        {
            if (!seen.Contains(key))
            {
                _cache.TryRemove(key, out _);
            }
        }

        return records
            .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The run that directory <paramref name="id"/> (a run directory or one of its loop-restart directories) belongs to.</summary>
    internal RunRecord? Load(string? id)
    {
        var directory = ResolveDirectory(id);
        if (directory is null)
        {
            return null;
        }

        var basePath = directory;
        var match = RestartPattern().Match(Path.GetFileName(directory));
        if (match.Success && ResolveDirectory(match.Groups["base"].Value) is { } owner)
        {
            basePath = owner;
        }

        return Build(basePath, FindSegments(basePath), LiveRuns().GetValueOrDefault(basePath));
    }

    /// <summary>
    /// The full path of run directory <paramref name="id"/>: a plain directory name (no separators, no <c>..</c>) of an
    /// existing directory directly under <see cref="BaseDirectory"/> that contains a <c>manifest.json</c>; otherwise null.
    /// </summary>
    internal string? ResolveDirectory(string? id)
    {
        if (!IsPlainName(id))
        {
            return null;
        }

        var full = Path.Combine(BaseDirectory, id!);
        return Directory.Exists(full) && File.Exists(Path.Combine(full, ManifestFile)) ? full : null;
    }

    /// <summary>True for a single path segment that cannot address anything outside its parent directory.</summary>
    internal static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 255
        && name is not ("." or "..")
        && !name.Contains("..", StringComparison.Ordinal)
        && name.IndexOfAny(InvalidNameChars) < 0
        && name.Trim() == name
        && !name.EndsWith('.');

    /// <summary>
    /// Events of a run from <c>events.jsonl</c> across its run directory and loop-restart directories, in order.
    /// <c>seq</c> is the 1-based line number across those files. A trailing partial line (still being written) is not
    /// returned yet; corrupt lines are skipped but keep their number.
    /// </summary>
    internal EventsPage ReadEvents(RunRecord record, long since, int limit)
    {
        var events = new List<JsonObject>();
        var seq = 0L;
        var next = since;
        var full = false;
        for (var i = 0; i < record.Segments.Count && !full; i++)
        {
            var path = Path.Combine(record.Segments[i].Path, EventsFile);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = OpenShared(path);
                JsonLines.Read(stream, 0, includePartialTail: i < record.Segments.Count - 1, (line, _) =>
                {
                    if (seq < since)
                    {
                        seq++;
                        return true;
                    }

                    if (events.Count >= limit)
                    {
                        full = true;
                        return false;
                    }

                    seq++;
                    next = seq;
                    if (TryParseObject(line) is { } json)
                    {
                        json["seq"] = seq;
                        events.Add(json);
                    }

                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read right now contributes no events.
            }
        }

        return new EventsPage(events, next, Done: !full && !record.IsActive);
    }

    /// <summary>Opens a file for reading while other processes may still write, rename, or delete it.</summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

    /// <summary>Reads a whole text file with full sharing.</summary>
    internal static string ReadText(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Reads a JSON object file, or null when it is missing, unreadable, or not a JSON object.</summary>
    internal static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(ReadText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonObject? TryParseObject(ReadOnlySpan<byte> line)
    {
        if (line.Trim(" \t\r"u8).IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The run's record, or null when its directory has no manifest (not a run, or not started yet).</summary>
    private RunRecord? Build(string basePath, IReadOnlyList<RunSegment> segments, ManagedRun? live)
    {
        var builder = new RunRecordBuilder(Path.GetFileName(basePath), basePath, segments, live);
        foreach (var segment in segments)
        {
            var state = _cache.GetOrAdd(segment.Path, path => new SegmentState(path));
            lock (state)
            {
                state.Refresh(force: builder.Active);
                if (segment.Restart == 0 && !state.HasManifest)
                {
                    return null;
                }

                builder.Add(segment, state);
            }
        }

        return builder.Build();
    }

    private IReadOnlyList<RunSegment> FindSegments(string basePath)
    {
        var name = Path.GetFileName(basePath);
        var restarts = new List<RunSegment>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(BaseDirectory, name + "-restart-*"))
            {
                var match = RestartPattern().Match(Path.GetFileName(dir));
                if (match.Success && PathComparer.Equals(match.Groups["base"].Value, name))
                {
                    restarts.Add(new RunSegment(dir, Path.GetFileName(dir), int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return [new RunSegment(basePath, name, 0), .. restarts.OrderBy(s => s.Restart)];
    }

    private List<IReadOnlyList<RunSegment>> ScanGroups()
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(BaseDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var names = new HashSet<string>(directories.Select(d => Path.GetFileName(d)), PathComparer);
        var restarts = new Dictionary<string, List<RunSegment>>(PathComparer);
        var bases = new List<string>();
        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var match = RestartPattern().Match(name);
            if (match.Success && names.Contains(match.Groups["base"].Value))
            {
                var owner = match.Groups["base"].Value;
                if (!restarts.TryGetValue(owner, out var list))
                {
                    restarts[owner] = list = [];
                }

                list.Add(new RunSegment(directory, name, int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture)));
            }
            else
            {
                bases.Add(directory);
            }
        }

        var groups = new List<IReadOnlyList<RunSegment>>();
        foreach (var directory in bases)
        {
            // Directories without a manifest are skipped when their (cached) data is loaded.
            var name = Path.GetFileName(directory);
            var first = new RunSegment(directory, name, 0);
            groups.Add(restarts.TryGetValue(name, out var extra) ? [first, .. extra.OrderBy(s => s.Restart)] : [first]);
        }

        return groups;
    }

    private Dictionary<string, ManagedRun> LiveRuns()
    {
        var map = new Dictionary<string, ManagedRun>(PathComparer);
        foreach (var run in _manager.Runs)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(run.LogsRoot));

            // Simulated runs keep their directories apart (.reactor/simulations); the history covers the runs directory.
            if (PathComparer.Equals(Path.GetDirectoryName(path), BaseDirectory))
            {
                map.TryAdd(path, run);
            }
        }

        return map;
    }

    [GeneratedRegex(@"^(?<base>.+)-restart-(?<n>\d{1,6})$", RegexOptions.CultureInvariant)]
    internal static partial Regex RestartPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex NodeIdPattern();
}

/// <summary>One directory of a run: the run directory itself (<see cref="Restart"/> 0) or a loop-restart directory.</summary>
internal sealed record RunSegment(string Path, string Name, int Restart);

/// <summary>A page of run events.</summary>
internal sealed record EventsPage(IReadOnlyList<JsonObject> Events, long Next, bool Done);

/// <summary>
/// Usage and cost fields summed from stage <c>backend.json</c> files — every attempt's, including the attempts that were
/// retried (<c>attempts/attempt-N/</c>) and earlier visits (<c>history/visit-N/</c>) — the way the engine's usage ledger sums
/// them; fields no stage reported stay absent.
/// </summary>
internal sealed class CostTotals
{
    /// <summary>The summed <c>backend.json</c> fields (the engine's <see cref="UsageLedger.Fields"/>, dollars first).</summary>
    public static readonly string[] Fields =
        ["cost_usd", "ai_units", "premium_requests", "input_tokens", "output_tokens", "cache_read_tokens", "reasoning_tokens", "model_calls", "tool_calls"];

    private readonly double?[] _values = new double?[Fields.Length];

    public double? this[int field] => _values[field];

    public double? Get(string field) => Array.IndexOf(Fields, field) is var i and >= 0 ? _values[i] : null;

    public void Add(IReadOnlyList<double?> values)
    {
        for (var i = 0; i < Fields.Length; i++)
        {
            if (values[i] is { } value)
            {
                _values[i] = (_values[i] ?? 0) + value;
            }
        }
    }

    /// <summary>The reported fields.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        for (var i = 0; i < Fields.Length; i++)
        {
            if (_values[i] is { } value)
            {
                json[Fields[i]] = Number(value);
            }
        }

        return json;
    }

    /// <summary>Reads the cost fields of a <c>backend.json</c> object (null for absent or non-numeric fields).</summary>
    public static double?[] Read(JsonObject json)
    {
        var values = new double?[Fields.Length];
        for (var i = 0; i < Fields.Length; i++)
        {
            if (json[Fields[i]] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) && double.IsFinite(number))
            {
                values[i] = number;
            }
        }

        return values;
    }

    /// <summary>A JSON number: an integer when <paramref name="value"/> is whole, else rounded to nine decimals.</summary>
    public static JsonNode Number(double value) =>
        value % 1 == 0 && Math.Abs(value) < 9e15 ? JsonValue.Create((long)value) : JsonValue.Create(Math.Round(value, 9));
}

/// <summary>
/// Usage of one stage visit: its <c>backend.json</c> plus those of the visit's archived attempts (<c>attempts/attempt-N/</c>),
/// summed. <see cref="Model"/> is the model of the visit's last attempt that reported one.
/// </summary>
internal sealed record VisitUsage(string? Model, double?[] Values, int ArchivedAttempts = 0);

/// <summary>Outcome metrics one visit of a node reported (a <c>MetricsRecorded</c> event); <see cref="Visit"/> is 1-based.</summary>
internal sealed record MetricVisit(int Visit, IReadOnlyDictionary<string, double> Values);

/// <summary>Why a run stopped at its cost budget (its last <c>BudgetExceeded</c> event since it was last started or resumed).</summary>
internal sealed record BudgetStop(double SpentUsd, double LimitUsd, string? NextNode, DateTimeOffset? At);

/// <summary>What the events (and <c>backend.json</c> files) say about one node of a run.</summary>
internal sealed class NodeStats
{
    /// <summary>Executions of the node (first attempts).</summary>
    public int Visits;

    /// <summary>Extra attempts (engine retries within a visit).</summary>
    public int Retries;

    /// <summary>The latest visit started but has not completed.</summary>
    public bool Open;

    /// <summary>The most recent error reported during the current visit (scan state).</summary>
    public string? PendingError;

    /// <summary>Failures of this node that flowed onward along an unconditional edge (<c>FailedStageContinued</c>).</summary>
    public int FailuresContinued;

    /// <summary>
    /// The node's last failure flowed onward and no later visit of it succeeded: the run carries a failed stage behind it
    /// (a later successful visit, such as a goal gate sending the run back, makes it good — as in the wind tunnel).
    /// </summary>
    public bool FailedBehind;

    /// <summary>A continued failure or a success has set <see cref="FailedBehind"/> (scan state, for merging directories).</summary>
    public bool FailedBehindSet;

    /// <summary>Times the node's <c>max_visits</c> was used up and the run went elsewhere (<c>VisitLimitReached</c>).</summary>
    public int VisitLimits;

    /// <summary>Outcome metrics per visit that reported any (<c>MetricsRecorded</c>), oldest first.</summary>
    public List<MetricVisit> Metrics { get; } = [];

    /// <summary>The status of each completed visit, in order.</summary>
    public List<string> Statuses { get; } = [];

    /// <summary>The duration of each completed visit.</summary>
    public List<double> DurationsMs { get; } = [];

    /// <summary>Failure reasons of failed visits.</summary>
    public Dictionary<string, int> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary><c>backend.json</c> metadata of each visit that wrote one (oldest first).</summary>
    public List<VisitUsage> Usage { get; } = [];

    /// <summary>Appends the statistics of a later directory of the same run (a loop restart); visit numbers continue.</summary>
    public void MergeFrom(NodeStats other)
    {
        var offset = Visits;
        Visits += other.Visits;
        Retries += other.Retries;
        Statuses.AddRange(other.Statuses);
        DurationsMs.AddRange(other.DurationsMs);
        foreach (var (reason, count) in other.Failures)
        {
            Failures[reason] = Failures.GetValueOrDefault(reason) + count;
        }

        Metrics.AddRange(other.Metrics.Select(m => m with { Visit = m.Visit + offset }));
        FailuresContinued += other.FailuresContinued;
        VisitLimits += other.VisitLimits;
        if (other.FailedBehindSet)
        {
            FailedBehind = other.FailedBehind;
            FailedBehindSet = true;
        }

        Open = other.Open;
    }
}

/// <summary>Fields of <c>manifest.json</c> the history uses.</summary>
internal sealed record ManifestInfo(string? RunId, string? Name, string? Goal, DateTimeOffset? StartedAt, int? NodeCount, bool Simulated, string? WorkingDirectory)
{
    public static ManifestInfo? From(JsonObject? json) => json is null ? null : new ManifestInfo(
        HistoryJson.Str(json["run_id"]),
        HistoryJson.Str(json["name"]),
        HistoryJson.Str(json["goal"]),
        HistoryJson.Date(json["started_at"]),
        (int?)HistoryJson.Long(json["node_count"]),
        HistoryJson.Bool(json["simulated"]) == true || json["simulation"] is JsonObject,
        WorkingDirectoryOf(json["working_directory"]));

    /// <summary>The recorded working directory; only an absolute path (the engine records full paths) is usable.</summary>
    private static string? WorkingDirectoryOf(JsonNode? node) =>
        HistoryJson.Str(node) is { } path && !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) ? path : null;
}

/// <summary>Fields of <c>result.json</c> the history uses.</summary>
internal sealed record ResultInfo(string Status, string? FailureReason, DateTimeOffset? FinishedAt, long? DurationMs)
{
    public static ResultInfo? From(JsonObject? json)
    {
        if (json is null)
        {
            return null;
        }

        var status = HistoryJson.Bool(json["cancelled"]) == true ? RunStatus.Cancelled
            : StageStatusExtensions.TryParse(HistoryJson.Str(json["status"]), out var parsed) && parsed is StageStatus.Success or StageStatus.PartialSuccess
                ? parsed.ToWire()
                : RunStatus.Fail;
        return new ResultInfo(status, HistoryJson.NonEmpty(HistoryJson.Str(json["failure_reason"])), HistoryJson.Date(json["finished_at"]), HistoryJson.Long(json["duration_ms"]));
    }
}

/// <summary>Fields of <c>studio.json</c> the history lists (the file itself is read on demand).</summary>
internal sealed record StudioInfo(string? SourcePath, bool Simulated, string? ForkedFrom)
{
    public static StudioInfo? From(JsonObject? json) => json is null ? null : new StudioInfo(
        HistoryJson.Str(json["source_path"]),
        HistoryJson.Bool(json["simulated"]) == true || json["simulation"] is JsonObject,
        (json["forked_from"] as JsonObject)?.ToJsonString());
}

/// <summary>Everything the history knows about one run: its directories, files, events, and live state.</summary>
internal sealed class RunRecord
{
    /// <summary>The run directory name (the run's history id).</summary>
    public required string Id { get; init; }

    public required string Directory { get; init; }

    /// <summary>The run directory first, then loop-restart directories in order (none for a run that has not started).</summary>
    public required IReadOnlyList<RunSegment> Segments { get; init; }

    /// <summary>The server's run, when this directory belongs to one.</summary>
    public ManagedRun? Live { get; init; }

    public required string Status { get; init; }

    public string? FailureReason { get; init; }

    public string? RunId { get; init; }

    public string? Name { get; init; }

    public string? Goal { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public long? DurationMs { get; init; }

    public int? NodeCount { get; init; }

    public string? SourcePath { get; init; }

    /// <summary>The run's working directory (<c>manifest.json</c>, else the live run's); null when the run did not record one.</summary>
    public string? WorkingDirectory { get; init; }

    public bool Simulated { get; init; }

    /// <summary>The <c>forked_from</c> object of <c>studio.json</c>, serialized.</summary>
    public string? ForkedFromJson { get; init; }

    public int StageExecutions { get; init; }

    public CostTotals Cost { get; init; } = new();

    /// <summary>The final value of each outcome metric (the latest <c>MetricsRecorded</c> value, as <c>metrics.&lt;name&gt;</c> in the context).</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Set when the run's latest stop was its cost budget (<c>BudgetExceeded</c> since it was last started or resumed).</summary>
    public BudgetStop? Budget { get; init; }

    /// <summary>Per-node statistics, in order of first execution.</summary>
    public OrderedDictionary<string, NodeStats> Nodes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Selected edges with how often each was taken.</summary>
    public IReadOnlyDictionary<(string From, string To), int> Edges { get; init; } = new Dictionary<(string, string), int>();

    /// <summary>Failures that flowed onward along unconditional edges (<c>FailedStageContinued</c>), over all nodes.</summary>
    public int FailuresContinued => Nodes.Values.Sum(n => n.FailuresContinued);

    /// <summary>Nodes whose failure flowed onward and that never succeeded afterwards: the run carries them as failed stages.</summary>
    public IReadOnlyList<string> FailedStages => Nodes.Where(n => n.Value.FailedBehind).Select(n => n.Key).ToList();

    /// <summary>The run reached the exit, but with failed stages behind it (a "dirty" landing; see <see cref="FailedStages"/>).</summary>
    public bool LandedWithFailedStages => Status is RunStatus.Success or RunStatus.PartialSuccess && Nodes.Values.Any(n => n.FailedBehind);

    /// <summary>The run is queued or running in this server.</summary>
    public bool IsActive => Live is not null && !RunStatus.IsTerminal(Live.Status);

    /// <summary>The run reached a final state (success, partial success, failure, or cancellation).</summary>
    public bool IsFinished => Status is RunStatus.Success or RunStatus.PartialSuccess or RunStatus.Fail or RunStatus.Cancelled;

    /// <summary>False for a run the server manages that has not written its manifest yet.</summary>
    public bool OnDisk => Segments.Count > 0;

    public JsonObject? ForkedFrom => ForkedFromJson is null ? null : JsonNode.Parse(ForkedFromJson) as JsonObject;

    /// <summary>A run the server manages that has not created its run directory (or manifest) yet.</summary>
    public static RunRecord ForUnstartedRun(string id, string directory, ManagedRun run)
    {
        var studio = StudioInfo.From(run.Studio);
        return new RunRecord
        {
            Id = id,
            Directory = directory,
            Segments = [],
            Live = run,
            Status = run.Status == RunStatus.Error ? RunStatus.Fail : run.Status,
            FailureReason = run.Error,
            RunId = run.Id,
            Name = run.Name,
            Goal = run.Goal,
            StartedAt = run.StartedAt ?? run.CreatedAt,
            FinishedAt = run.FinishedAt,
            NodeCount = run.Graph.Nodes.Count,
            SourcePath = studio?.SourcePath,
            WorkingDirectory = run.WorkingDirectory,
            Simulated = studio?.Simulated ?? false,
            ForkedFromJson = studio?.ForkedFrom,
        };
    }
}

/// <summary>Merges the cached data of a run's directories into a <see cref="RunRecord"/>.</summary>
internal sealed class RunRecordBuilder(string id, string directory, IReadOnlyList<RunSegment> segments, ManagedRun? live)
{
    private readonly OrderedDictionary<string, NodeStats> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), int> _edges = [];
    private readonly CostTotals _cost = new();
    private readonly Dictionary<string, double> _metrics = new(StringComparer.Ordinal);
    private ManifestInfo? _manifest;
    private StudioInfo? _studio;
    private ResultInfo? _result;
    private BudgetStop? _budget;
    private bool _simulationFile;
    private int _stageExecutions;
    private DateTimeOffset? _firstEvent;
    private DateTimeOffset? _lastEvent;
    private string? _terminalError;

    /// <summary>The run is queued or running in this server.</summary>
    public bool Active { get; } = live is not null && !RunStatus.IsTerminal(live.Status);

    public void Add(RunSegment segment, SegmentState state)
    {
        if (segment.Restart == 0)
        {
            _manifest = state.Manifest;
            _studio = state.Studio;
            _simulationFile = state.HasSimulationFile;
        }

        // Only the directory a run finished in has a result.
        _result = state.Result;
        var events = state.Events;
        _stageExecutions += events.StageExecutions;
        _firstEvent ??= events.FirstTimestamp;
        _lastEvent = events.LastTimestamp ?? _lastEvent;
        _terminalError = events.TerminalError ?? _terminalError;

        // A budget stop ends the run, so only the directory the run ended in can hold the current one.
        _budget = events.Budget;
        foreach (var (name, value) in events.FinalMetrics)
        {
            _metrics[name] = value;
        }

        foreach (var (node, stats) in events.Nodes)
        {
            Node(node).MergeFrom(stats);
        }

        foreach (var (edge, count) in events.Edges)
        {
            _edges[edge] = _edges.GetValueOrDefault(edge) + count;
        }

        foreach (var (node, visits) in state.Usage)
        {
            var stats = Node(node);
            foreach (var visit in visits)
            {
                stats.Usage.Add(visit);
                _cost.Add(visit.Values);
            }
        }
    }

    public RunRecord Build()
    {
        var startedAt = _manifest?.StartedAt ?? _firstEvent;
        string status;
        string? failure = null;
        DateTimeOffset? finishedAt = null;
        long? duration = null;
        if (live is not null && Active)
        {
            status = live.Status;
            duration = startedAt is { } started ? (long)Math.Max(0, (DateTimeOffset.UtcNow - started).TotalMilliseconds) : null;
        }
        else if (_result is { } result)
        {
            status = result.Status;
            failure = result.FailureReason;
            finishedAt = result.FinishedAt;
            duration = result.DurationMs;
        }
        else if (live is not null)
        {
            status = live.Status == RunStatus.Error ? RunStatus.Fail : live.Status;
            failure = live.Error;
            finishedAt = live.FinishedAt;
            duration = live.StartedAt is { } started && live.FinishedAt is { } finished ? (long)(finished - started).TotalMilliseconds : null;
        }
        else
        {
            status = "interrupted";
            duration = startedAt is { } started && _lastEvent is { } last && last >= started ? (long)(last - started).TotalMilliseconds : null;
        }

        if (status == RunStatus.Fail)
        {
            failure ??= HistoryJson.NonEmpty(live?.Error) ?? HistoryJson.NonEmpty(_terminalError);
        }

        return new RunRecord
        {
            Id = id,
            Directory = directory,
            Segments = segments,
            Live = live,
            Status = status,
            FailureReason = failure,
            RunId = _manifest?.RunId ?? live?.Id,
            Name = _manifest?.Name ?? live?.Name,
            Goal = _manifest?.Goal ?? live?.Goal,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            DurationMs = duration,
            NodeCount = _manifest?.NodeCount,
            SourcePath = _studio?.SourcePath,
            WorkingDirectory = _manifest?.WorkingDirectory ?? live?.WorkingDirectory,
            Simulated = _simulationFile || _manifest?.Simulated == true || _studio?.Simulated == true,
            ForkedFromJson = _studio?.ForkedFrom,
            StageExecutions = _stageExecutions,
            Cost = _cost,
            Metrics = _metrics,
            Budget = Active ? null : _budget,
            Nodes = _nodes,
            Edges = _edges,
        };
    }

    private NodeStats Node(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var stats))
        {
            _nodes[nodeId] = stats = new NodeStats();
        }

        return stats;
    }
}

/// <summary>Cached, incrementally refreshed data of one run directory. Callers lock the instance while using it.</summary>
internal sealed class SegmentState(string path)
{
    private readonly Dictionary<string, (FileStamp Stamp, (string? Model, double?[] Values)? Usage)> _usageFiles = new(RunHistory.PathComparer);
    private bool _loaded;
    private FileStamp _manifestStamp;
    private FileStamp _resultStamp;
    private FileStamp _studioStamp;
    private FileStamp _eventsStamp;

    public ManifestInfo? Manifest { get; private set; }

    public ResultInfo? Result { get; private set; }

    public StudioInfo? Studio { get; private set; }

    public bool HasSimulationFile { get; private set; }

    /// <summary>The directory has a <c>manifest.json</c> (directories without one are not runs).</summary>
    public bool HasManifest { get; private set; }

    public EventScan Events { get; private set; } = new();

    /// <summary>Per node, the <c>backend.json</c> metadata of each visit (history visits first, the latest visit last).</summary>
    public IReadOnlyDictionary<string, List<VisitUsage>> Usage { get; private set; } = new Dictionary<string, List<VisitUsage>>();

    /// <summary>
    /// Re-reads what changed since the last call (by file length and write time, from one listing of the directory).
    /// With <paramref name="force"/> (a run that is executing now), <c>events.jsonl</c> is read past the last complete
    /// line even if its metadata looks unchanged.
    /// </summary>
    public void Refresh(bool force)
    {
        var changed = !_loaded;
        var files = TopLevelFiles(path);
        var manifest = files.GetValueOrDefault(RunHistory.ManifestFile, FileStamp.None);
        HasManifest = !manifest.Missing;
        HasSimulationFile = files.ContainsKey(RunHistory.SimulationFile);
        if (!_loaded || manifest != _manifestStamp)
        {
            Manifest = ManifestInfo.From(RunHistory.ReadJsonObject(Path.Combine(path, RunHistory.ManifestFile)));
            _manifestStamp = manifest;
            changed = true;
        }

        var result = files.GetValueOrDefault(RunHistory.ResultFile, FileStamp.None);
        if (!_loaded || result != _resultStamp)
        {
            Result = ResultInfo.From(RunHistory.ReadJsonObject(Path.Combine(path, RunHistory.ResultFile)));
            _resultStamp = result;
            changed = true;
        }

        var studio = files.GetValueOrDefault(PipelineRunManager.StudioFileName, FileStamp.None);
        if (!_loaded || studio != _studioStamp)
        {
            Studio = StudioInfo.From(RunHistory.ReadJsonObject(Path.Combine(path, PipelineRunManager.StudioFileName)));
            _studioStamp = studio;
            changed = true;
        }

        // A run without a result may still be appending: ask for the file's own metadata (directory listings may lag
        // behind for files a writer holds open).
        var events = result.Missing
            ? FileStamp.Of(Path.Combine(path, RunHistory.EventsFile))
            : files.GetValueOrDefault(RunHistory.EventsFile, FileStamp.None);
        if (!_loaded || force || events != _eventsStamp)
        {
            var before = Events.Lines;
            var reset = events.Missing || events.Length < Events.Offset;
            if (reset)
            {
                Events = new EventScan();
            }

            var read = true;
            if (!events.Missing)
            {
                try
                {
                    Events.ReadFrom(Path.Combine(path, RunHistory.EventsFile));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    read = false; // Keep the old stamp so the next refresh reads again.
                }
            }

            changed |= reset || Events.Lines != before;
            if (read)
            {
                _eventsStamp = events;
            }
        }

        if (changed)
        {
            ScanUsage();
        }

        _loaded = true;
    }

    /// <summary>Stamps of the files directly inside <paramref name="directory"/>, from one directory listing.</summary>
    private static Dictionary<string, FileStamp> TopLevelFiles(string directory)
    {
        var files = new Dictionary<string, FileStamp>(RunHistory.PathComparer);
        try
        {
            var listing = new FileSystemEnumerable<(string Name, FileStamp Stamp)>(
                directory,
                (ref FileSystemEntry entry) => (entry.FileName.ToString(), new FileStamp(entry.Length, entry.LastWriteTimeUtc.UtcTicks)),
                new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            };
            foreach (var (name, stamp) in listing)
            {
                files[name] = stamp;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that vanished or cannot be listed has no files.
        }

        return files;
    }

    private void ScanUsage()
    {
        var usage = new Dictionary<string, List<VisitUsage>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(RunHistory.PathComparer);
        try
        {
            foreach (var stageDir in Directory.EnumerateDirectories(path))
            {
                var node = Path.GetFileName(stageDir);
                if (!RunHistory.NodeIdPattern().IsMatch(node))
                {
                    continue;
                }

                var visits = new List<VisitUsage>();
                foreach (var (_, visitDir) in StageFiles.HistoryVisits(stageDir))
                {
                    AddVisit(visits, visitDir, seen);
                }

                AddVisit(visits, stageDir, seen);
                if (visits.Count > 0)
                {
                    usage[node] = visits;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (var key in _usageFiles.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _usageFiles.Remove(key);
        }

        Usage = usage;
    }

    /// <summary>
    /// Adds one visit's usage: the <c>backend.json</c> of its archived attempts (<c>attempts/attempt-N/</c>, which the engine
    /// keeps when it retries a stage — timed-out and failed attempts cost money too) and of its last attempt, summed.
    /// </summary>
    private void AddVisit(List<VisitUsage> visits, string visitDir, HashSet<string> seen)
    {
        var attempts = StageFiles.Attempts(visitDir);
        var parts = new List<(string? Model, double?[] Values)>(attempts.Count + 1);
        foreach (var (_, attemptDir) in attempts)
        {
            if (ReadUsage(Path.Combine(attemptDir, "backend.json"), seen) is { } attempt)
            {
                parts.Add(attempt);
            }
        }

        if (ReadUsage(Path.Combine(visitDir, "backend.json"), seen) is { } last)
        {
            parts.Add(last);
        }

        if (parts.Count == 0)
        {
            return;
        }

        var values = new double?[CostTotals.Fields.Length];
        foreach (var (_, part) in parts)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (part[i] is { } value)
                {
                    values[i] = (values[i] ?? 0) + value;
                }
            }
        }

        visits.Add(new VisitUsage(parts.LastOrDefault(p => !string.IsNullOrEmpty(p.Model)).Model, values, attempts.Count));
    }

    private (string? Model, double?[] Values)? ReadUsage(string file, HashSet<string> seen)
    {
        var stamp = FileStamp.Of(file);
        if (stamp.Missing)
        {
            return null;
        }

        seen.Add(file);
        if (!_usageFiles.TryGetValue(file, out var cached) || cached.Stamp != stamp)
        {
            var json = RunHistory.ReadJsonObject(file);
            cached = (stamp, json is null ? null : (HistoryJson.Str(json["model"]), CostTotals.Read(json)));
            _usageFiles[file] = cached;
        }

        return cached.Usage;
    }
}

/// <summary>A file's length and last write time (length -1 when it does not exist).</summary>
internal readonly record struct FileStamp(long Length, long Ticks)
{
    public static readonly FileStamp None = new(-1, 0);

    public bool Missing => Length < 0;

    public static FileStamp Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }
}

/// <summary>Statistics accumulated from <c>events.jsonl</c>, resumable after the last complete line.</summary>
internal sealed class EventScan
{
    private string? _firstTimestamp;
    private string? _lastTimestamp;

    /// <summary>Bytes consumed (the offset just past the last complete line).</summary>
    public long Offset { get; private set; }

    /// <summary>Complete lines consumed.</summary>
    public long Lines { get; private set; }

    /// <summary>Complete lines that were not valid event objects.</summary>
    public int Corrupt { get; private set; }

    public int StageExecutions { get; private set; }

    /// <summary>Per-node statistics, in order of first appearance.</summary>
    public OrderedDictionary<string, NodeStats> Nodes { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string From, string To), int> Edges { get; } = [];

    /// <summary>The error of the last <c>PipelineFailed</c> event (cleared when the run is resumed).</summary>
    public string? TerminalError { get; private set; }

    /// <summary>The latest value of each outcome metric (<c>MetricsRecorded</c>), in order of first report.</summary>
    public OrderedDictionary<string, double> FinalMetrics { get; } = new(StringComparer.Ordinal);

    /// <summary>The last <c>BudgetExceeded</c> since the run was last started or resumed (cleared by <c>PipelineStarted</c>).</summary>
    public BudgetStop? Budget { get; private set; }

    public DateTimeOffset? FirstTimestamp => HistoryJson.ParseDate(_firstTimestamp);

    public DateTimeOffset? LastTimestamp => HistoryJson.ParseDate(_lastTimestamp);

    /// <summary>Reads the complete lines after <see cref="Offset"/>.</summary>
    public void ReadFrom(string file)
    {
        using var stream = RunHistory.OpenShared(file);
        Offset = JsonLines.Read(stream, Offset, includePartialTail: false, (line, _) =>
        {
            Apply(line);
            return true;
        });
    }

    private void Apply(ReadOnlySpan<byte> line)
    {
        Lines++;
        if (line.Trim(" \t\r"u8).IsEmpty)
        {
            return;
        }

        if (!EventFields.TryRead(line, out var e))
        {
            Corrupt++;
            return;
        }

        if (e.Timestamp is { } timestamp)
        {
            _firstTimestamp ??= timestamp;
            _lastTimestamp = timestamp;
        }

        switch (e.Type)
        {
            case "PipelineStarted":
                TerminalError = null;
                Budget = null;
                break;
            case "PipelineFailed":
                TerminalError = e.Error;
                break;
            case "StageStarted" when e.NodeId is { } node:
                var started = Node(node);
                if (e.Attempt <= 1)
                {
                    started.Visits++;
                    started.PendingError = null;
                    StageExecutions++;
                }
                else
                {
                    started.Retries++;
                }

                started.Open = true;
                break;
            case "StageFailed" when e.NodeId is { } node:
                Node(node).PendingError = e.Error;
                break;
            case "StageCompleted" when e.NodeId is { } node:
                var completed = Node(node);
                var status = e.Status ?? "";
                completed.Statuses.Add(status);
                if (e.DurationMs is { } duration)
                {
                    completed.DurationsMs.Add(duration);
                }

                if (status == "fail" && FailureText(completed.PendingError ?? e.Notes) is { } reason)
                {
                    completed.Failures[reason] = completed.Failures.GetValueOrDefault(reason) + 1;
                }

                if (status is "success" or "partial_success")
                {
                    completed.FailedBehind = false;
                    completed.FailedBehindSet = true;
                }

                completed.PendingError = null;
                completed.Open = false;
                break;
            case "EdgeSelected" when e.From is { } from && e.To is { } to:
                Edges[(from, to)] = Edges.GetValueOrDefault((from, to)) + 1;
                break;
            case "FailedStageContinued" when e.NodeId is { } node:
                var continued = Node(node);
                continued.FailuresContinued++;
                continued.FailedBehind = true;
                continued.FailedBehindSet = true;
                break;
            case "VisitLimitReached" when e.NodeId is { } node:
                Node(node).VisitLimits++;
                break;
            case "MetricsRecorded" when e.NodeId is { } node && e.Metrics is { Count: > 0 } metrics:
                var reporter = Node(node);
                reporter.Metrics.Add(new MetricVisit(Math.Max(1, reporter.Visits), metrics));
                foreach (var (name, value) in metrics)
                {
                    FinalMetrics[name] = value;
                }

                break;
            case "BudgetExceeded":
                Budget = new BudgetStop(e.SpentUsd ?? 0, e.LimitUsd ?? 0, e.NextNodeId, HistoryJson.ParseDate(e.Timestamp));
                break;
        }
    }

    private NodeStats Node(string id)
    {
        if (!Nodes.TryGetValue(id, out var stats))
        {
            Nodes[id] = stats = new NodeStats();
        }

        return stats;
    }

    /// <summary>A failure reason for grouping: its first line, trimmed and clipped.</summary>
    private static string? FailureText(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var line = reason.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= 300 ? line : line[..300] + "…";
    }
}

/// <summary>The top-level event fields the history reads, extracted without building a document.</summary>
internal struct EventFields
{
    public string? Type;
    public string? NodeId;
    public string? Status;
    public string? Error;
    public string? Notes;
    public string? From;
    public string? To;
    public string? Timestamp;
    public int Attempt;
    public double? DurationMs;

    /// <summary><c>MetricsRecorded</c>: the reported numbers.</summary>
    public Dictionary<string, double>? Metrics;

    /// <summary><c>BudgetExceeded</c>: recorded cost and the budget, in US dollars, and the stage it stopped before.</summary>
    public double? SpentUsd;
    public double? LimitUsd;
    public string? NextNodeId;

    private enum Field
    {
        Other,
        Type,
        NodeId,
        Status,
        Error,
        Notes,
        From,
        To,
        Timestamp,
        Attempt,
        Duration,
        Metrics,
        SpentUsd,
        LimitUsd,
        NextNodeId,
    }

    public static bool TryRead(ReadOnlySpan<byte> line, out EventFields fields)
    {
        fields = default;
        var reader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return true;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var property = reader.ValueTextEquals("type"u8) ? Field.Type
                    : reader.ValueTextEquals("node_id"u8) ? Field.NodeId
                    : reader.ValueTextEquals("status"u8) ? Field.Status
                    : reader.ValueTextEquals("error"u8) ? Field.Error
                    : reader.ValueTextEquals("notes"u8) ? Field.Notes
                    : reader.ValueTextEquals("from"u8) ? Field.From
                    : reader.ValueTextEquals("to"u8) ? Field.To
                    : reader.ValueTextEquals("timestamp"u8) ? Field.Timestamp
                    : reader.ValueTextEquals("attempt"u8) ? Field.Attempt
                    : reader.ValueTextEquals("duration"u8) ? Field.Duration
                    : reader.ValueTextEquals("metrics"u8) ? Field.Metrics
                    : reader.ValueTextEquals("spent_usd"u8) ? Field.SpentUsd
                    : reader.ValueTextEquals("limit_usd"u8) ? Field.LimitUsd
                    : reader.ValueTextEquals("next_node_id"u8) ? Field.NextNodeId
                    : Field.Other;
                if (!reader.Read())
                {
                    return false;
                }

                if (property == Field.Metrics && reader.TokenType == JsonTokenType.StartObject)
                {
                    fields.Metrics = ReadNumbers(ref reader);
                    continue;
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }

                var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                double? number = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : null;
                switch (property)
                {
                    case Field.Type:
                        fields.Type = text;
                        break;
                    case Field.NodeId:
                        fields.NodeId = text;
                        break;
                    case Field.Status:
                        fields.Status = text;
                        break;
                    case Field.Error:
                        fields.Error = text;
                        break;
                    case Field.Notes:
                        fields.Notes = text;
                        break;
                    case Field.From:
                        fields.From = text;
                        break;
                    case Field.To:
                        fields.To = text;
                        break;
                    case Field.Timestamp:
                        fields.Timestamp = text;
                        break;
                    case Field.Attempt when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var attempt):
                        fields.Attempt = attempt;
                        break;
                    case Field.Duration when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var duration):
                        fields.DurationMs = duration.TotalMilliseconds;
                        break;
                    case Field.SpentUsd:
                        fields.SpentUsd = number;
                        break;
                    case Field.LimitUsd:
                        fields.LimitUsd = number;
                        break;
                    case Field.NextNodeId:
                        fields.NextNodeId = text;
                        break;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads an object of numbers (the reader is on its start); other values are skipped.</summary>
    private static Dictionary<string, double> ReadNumbers(ref Utf8JsonReader reader)
    {
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) && double.IsFinite(value))
            {
                numbers[name] = value;
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return numbers;
    }
}
/// <summary>Small JSON value helpers.</summary>
internal static class HistoryJson
{
    public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public static long? Long(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? (long)number : null;

    public static DateTimeOffset? Date(JsonNode? node) => ParseDate(Str(node));

    public static DateTimeOffset? ParseDate(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    public static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>An ISO 8601 timestamp, or null.</summary>
    public static string? Iso(DateTimeOffset? date) => date?.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Reads newline-delimited records from a stream without loading it whole.</summary>
internal static class JsonLines
{
    /// <summary>Lines longer than this are passed on empty (their content is skipped).</summary>
    public const int MaxLineBytes = 16 * 1024 * 1024;

    /// <summary>Receives one line (without its line break) and the offset just past it; returns false to stop.</summary>
    public delegate bool LineHandler(ReadOnlySpan<byte> line, long endOffset);

    /// <summary>
    /// Reads lines from <paramref name="start"/>; a final line without a line break is passed only when
    /// <paramref name="includePartialTail"/> is set. Returns the offset just past the last line passed.
    /// </summary>
    public static long Read(Stream stream, long start, bool includePartialTail, LineHandler onLine)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var pending = new ArrayBufferWriter<byte>();
        var overflow = false;
        var position = start;
        var consumed = start;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var span = buffer.AsSpan(0, read);
                if (position == 0 && span.StartsWith(Utf8Bom))
                {
                    span = span[Utf8Bom.Length..];
                    position = Utf8Bom.Length;
                }

                while (!span.IsEmpty)
                {
                    var newline = span.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        Append(pending, span, ref overflow);
                        position += span.Length;
                        break;
                    }

                    position += newline + 1;
                    consumed = position;
                    bool keepGoing;
                    if (pending.WrittenCount == 0 && !overflow)
                    {
                        keepGoing = onLine(TrimCarriageReturn(span[..newline]), consumed);
                    }
                    else
                    {
                        Append(pending, span[..newline], ref overflow);
                        keepGoing = onLine(overflow ? [] : TrimCarriageReturn(pending.WrittenSpan), consumed);
                        pending.ResetWrittenCount();
                        overflow = false;
                    }

                    if (!keepGoing)
                    {
                        return consumed;
                    }

                    span = span[(newline + 1)..];
                }
            }

            if (includePartialTail && (pending.WrittenCount > 0 || overflow))
            {
                consumed = position;
                onLine(overflow ? [] : TrimCarriageReturn(pending.WrittenSpan), consumed);
            }

            return consumed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Append(ArrayBufferWriter<byte> pending, ReadOnlySpan<byte> data, ref bool overflow)
    {
        if (overflow)
        {
            return;
        }

        if (pending.WrittenCount + data.Length > MaxLineBytes)
        {
            overflow = true;
            pending.ResetWrittenCount();
            return;
        }

        pending.Write(data);
    }

    private static ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line) =>
        !line.IsEmpty && line[^1] == (byte)'\r' ? line[..^1] : line;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
}

/// <summary>Stage directory helpers: history visits, archived attempts, and file listings.</summary>
internal static partial class StageFiles
{
    /// <summary>The <c>history/visit-N</c> directories of a stage directory, by visit number.</summary>
    public static IReadOnlyList<(int Visit, string Path)> HistoryVisits(string stageDirectory) =>
        Numbered(Path.Combine(stageDirectory, "history"), "visit-*", VisitPattern());

    /// <summary>
    /// The <c>attempts/attempt-N</c> directories of a stage directory or of a <c>history/visit-N</c> directory — the
    /// attempts of that visit that the engine retried — by attempt number.
    /// </summary>
    public static IReadOnlyList<(int Attempt, string Path)> Attempts(string visitDirectory) =>
        Numbered(Path.Combine(visitDirectory, StageAttemptArchive.DirectoryName), "attempt-*", AttemptPattern());

    private static IReadOnlyList<(int Number, string Path)> Numbered(string parent, string search, Regex pattern)
    {
        if (!Directory.Exists(parent))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(parent, search)
                .Select(d => (Match: pattern.Match(Path.GetFileName(d)), Path: d))
                .Where(v => v.Match.Success)
                .Select(v => (int.Parse(v.Match.Groups["n"].Value, CultureInfo.InvariantCulture), v.Path))
                .OrderBy(v => v.Item1)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>File names directly inside <paramref name="directory"/>, sorted.</summary>
    public static IReadOnlyList<string> Names(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^visit-(?<n>\d{1,6})$", RegexOptions.CultureInvariant)]
    private static partial Regex VisitPattern();

    [GeneratedRegex(@"^attempt-(?<n>\d{1,6})$", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptPattern();
}

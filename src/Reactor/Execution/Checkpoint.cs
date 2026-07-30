using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Execution;

/// <summary>
/// A completed stage, kept for context carryover (preambles) and audit. <see cref="Metrics"/> (Reactor extension) holds the
/// outcome metrics the stage reported (null when none).
/// </summary>
public sealed record StageRecord(string NodeId, string Label, StageStatus Status, string Notes, string FailureReason, DateTimeOffset CompletedAt, int Attempts, IReadOnlyDictionary<string, double>? Metrics = null)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["node_id"] = NodeId,
            ["label"] = Label,
            ["status"] = Status.ToWire(),
            ["notes"] = Notes,
            ["failure_reason"] = FailureReason,
            ["completed_at"] = CompletedAt.ToString("O"),
            ["attempts"] = Attempts,
        };
        if (Metrics is { Count: > 0 })
        {
            json["metrics"] = new JsonObject(Metrics.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value))));
        }

        return json;
    }

    public static StageRecord FromJson(JsonNode node) => new(
        node["node_id"]?.GetValue<string>() ?? "",
        node["label"]?.GetValue<string>() ?? "",
        StageStatusExtensions.TryParse(node["status"]?.GetValue<string>(), out var status) ? status : StageStatus.Fail,
        node["notes"]?.GetValue<string>() ?? "",
        node["failure_reason"]?.GetValue<string>() ?? "",
        DateTimeOffset.TryParse(node["completed_at"]?.GetValue<string>(), out var at) ? at : DateTimeOffset.MinValue,
        node["attempts"] is JsonValue a && a.TryGetValue<int>(out var attempts) ? attempts : 1,
        node["metrics"] is JsonObject ? Outcome.ParseMetrics(node["metrics"]) : null);
}

/// <summary>
/// Serializable snapshot of execution state, saved after each node completes (spec 5.3). Besides the spec fields it
/// records each node's latest outcome (so goal gates and routing survive a resume), the fidelity used by the last node
/// (to degrade <c>full</c> to <c>summary:high</c> on resume), and the stage history used for context carryover.
/// </summary>
public sealed class Checkpoint
{
    public const string FileName = "checkpoint.json";

    /// <summary>
    /// Subdirectory of a run directory holding per-stage checkpoint snapshots (see <see cref="RunOptions.KeepCheckpointHistory"/>).
    /// </summary>
    public const string SnapshotDirectoryName = "checkpoints";

    /// <summary>The snapshot name for the checkpoint saved after stage execution <paramref name="index"/> (<c>0004-plan</c>).</summary>
    public static string SnapshotName(int index, string nodeId) => $"{index.ToString("D4", CultureInfo.InvariantCulture)}-{nodeId}";

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string RunId { get; set; } = "";

    /// <summary>Id of the last completed node.</summary>
    public string CurrentNode { get; set; } = "";

    public List<string> CompletedNodes { get; set; } = [];

    public Dictionary<string, int> NodeRetries { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, object?> ContextValues { get; set; } = new(StringComparer.Ordinal);

    public List<string> Logs { get; set; } = [];

    /// <summary>Latest outcome per node.</summary>
    public Dictionary<string, Outcome> NodeOutcomes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Fidelity mode used by <see cref="CurrentNode"/>.</summary>
    public string? LastFidelity { get; set; }

    /// <summary>Thread key used by <see cref="CurrentNode"/> (full fidelity only).</summary>
    public string? LastThreadId { get; set; }

    /// <summary>The incoming edge's source for <see cref="CurrentNode"/>, if any.</summary>
    public int LoopRestarts { get; set; }

    public List<StageRecord> History { get; set; } = [];

    /// <summary>Terminal status when the run has finished (<c>success</c>/<c>fail</c>), otherwise null.</summary>
    public string? FinalStatus { get; set; }

    /// <summary>Reactor extension: the run's usage totals so far (see <see cref="UsageLedger"/>).</summary>
    public Dictionary<string, double> Usage { get; set; } = new(StringComparer.Ordinal);

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
        ["timestamp"] = Timestamp.ToString("O"),
        ["run_id"] = RunId,
        ["current_node"] = CurrentNode,
        ["completed_nodes"] = new JsonArray(CompletedNodes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        ["node_retries"] = new JsonObject(NodeRetries.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
        ["context"] = new JsonObject(ContextValues.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Reactor.Execution.ContextValues.ToJson(kv.Value)))),
        ["logs"] = new JsonArray(Logs.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
        ["node_outcomes"] = new JsonObject(NodeOutcomes.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value.ToStatusJson()))),
        ["last_fidelity"] = LastFidelity,
        ["last_thread_id"] = LastThreadId,
        ["loop_restarts"] = LoopRestarts,
        ["history"] = new JsonArray(History.Select(h => (JsonNode?)h.ToJson()).ToArray()),
        ["final_status"] = FinalStatus,
        };
        if (Usage.Count > 0)
        {
            json["usage"] = UsageLedger.ToJson(Usage);
        }

        return json;
    }

    public static Checkpoint FromJson(JsonNode json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var checkpoint = new Checkpoint
        {
            Timestamp = DateTimeOffset.TryParse(Str(json, "timestamp"), out var ts) ? ts : DateTimeOffset.UtcNow,
            RunId = Str(json, "run_id") ?? "",
            CurrentNode = Str(json, "current_node") ?? "",
            LastFidelity = Str(json, "last_fidelity"),
            LastThreadId = Str(json, "last_thread_id"),
            FinalStatus = Str(json, "final_status"),
            LoopRestarts = json["loop_restarts"] is JsonValue lr && lr.TryGetValue<int>(out var restarts) ? restarts : 0,
            Usage = UsageLedger.FromJson(json["usage"]),
        };

        if (json["completed_nodes"] is JsonArray completed)
        {
            checkpoint.CompletedNodes.AddRange(completed.Select(n => n?.GetValue<string>()).Where(n => n is not null)!);
        }

        if (json["node_retries"] is JsonObject retries)
        {
            foreach (var (key, value) in retries)
            {
                if (value is JsonValue v && v.TryGetValue<int>(out var count))
                {
                    checkpoint.NodeRetries[key] = count;
                }
            }
        }

        if (json["context"] is JsonObject context)
        {
            foreach (var (key, value) in context)
            {
                checkpoint.ContextValues[key] = Reactor.Execution.ContextValues.FromJson(value);
            }
        }

        if (json["logs"] is JsonArray logs)
        {
            checkpoint.Logs.AddRange(logs.Select(n => n?.GetValue<string>() ?? ""));
        }

        if (json["node_outcomes"] is JsonObject outcomes)
        {
            foreach (var (key, value) in outcomes)
            {
                if (value is not null)
                {
                    checkpoint.NodeOutcomes[key] = Outcome.FromStatusJson(value);
                }
            }
        }

        if (json["history"] is JsonArray history)
        {
            checkpoint.History.AddRange(history.Where(h => h is not null).Select(h => StageRecord.FromJson(h!)));
        }

        return checkpoint;
    }

    /// <summary>
    /// Writes the checkpoint atomically (temp file + move). Readers may hold the file open at that instant (a server
    /// polling a live run, a supervisor watching its child, an editor), so the replace is retried briefly.
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                Thread.Sleep(10 * attempt);
            }
        }
    }

    /// <summary>Loads a checkpoint, opening it with full sharing so the read never denies a concurrent writer access.</summary>
    public static Checkpoint Load(string path)
    {
        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        var json = JsonNode.Parse(text) ?? throw new InvalidDataException($"Checkpoint '{path}' is empty.");
        return FromJson(json);
    }

    /// <summary>Loads <c>{logsRoot}/checkpoint.json</c>, or null when absent.</summary>
    public static Checkpoint? TryLoadFromRunDirectory(string logsRoot)
    {
        var path = Path.Combine(logsRoot, FileName);
        return File.Exists(path) ? Load(path) : null;
    }

    private static string? Str(JsonNode json, string name) =>
        json[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

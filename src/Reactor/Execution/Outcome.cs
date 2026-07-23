using System.Text.Json.Nodes;

namespace Reactor.Execution;

/// <summary>Result status of executing a node handler.</summary>
public enum StageStatus
{
    /// <summary>Stage completed its work.</summary>
    Success,

    /// <summary>Completed with caveats; treated as success for routing.</summary>
    PartialSuccess,

    /// <summary>Stage requests re-execution.</summary>
    Retry,

    /// <summary>Stage failed permanently.</summary>
    Fail,

    /// <summary>Stage was skipped; proceed without recording an outcome.</summary>
    Skipped,
}

public static class StageStatusExtensions
{
    /// <summary>The lowercase wire value used in context, conditions, and status files.</summary>
    public static string ToWire(this StageStatus status) => status switch
    {
        StageStatus.Success => "success",
        StageStatus.PartialSuccess => "partial_success",
        StageStatus.Retry => "retry",
        StageStatus.Fail => "fail",
        StageStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>True for SUCCESS and PARTIAL_SUCCESS.</summary>
    public static bool IsSuccessful(this StageStatus status) => status is StageStatus.Success or StageStatus.PartialSuccess;

    /// <summary>Parses a status string leniently (case-insensitive; accepts common synonyms).</summary>
    public static bool TryParse(string? value, out StageStatus status)
    {
        switch (value?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_'))
        {
            case "success" or "succeeded" or "ok" or "pass" or "passed":
                status = StageStatus.Success;
                return true;
            case "partial_success" or "partial" or "partialsuccess":
                status = StageStatus.PartialSuccess;
                return true;
            case "retry":
                status = StageStatus.Retry;
                return true;
            case "fail" or "failed" or "failure" or "error":
                status = StageStatus.Fail;
                return true;
            case "skipped" or "skip":
                status = StageStatus.Skipped;
                return true;
            default:
                status = StageStatus.Fail;
                return false;
        }
    }

    public static StageStatus Parse(string value) =>
        TryParse(value, out var status) ? status : throw new FormatException($"Unknown stage status '{value}'.");
}

/// <summary>The result of executing a node handler. Drives routing and context updates.</summary>
public sealed record Outcome
{
    public StageStatus Status { get; init; } = StageStatus.Success;

    /// <summary>Edge label to prefer for the next transition.</summary>
    public string PreferredLabel { get; init; } = "";

    /// <summary>Explicit next node ids (fallback when no label matches).</summary>
    public IReadOnlyList<string> SuggestedNextIds { get; init; } = [];

    /// <summary>Key/value pairs merged into the run context.</summary>
    public IReadOnlyDictionary<string, object?> ContextUpdates { get; init; } = new Dictionary<string, object?>();

    /// <summary>Human-readable execution summary.</summary>
    public string Notes { get; init; } = "";

    /// <summary>Reason for failure (FAIL or RETRY).</summary>
    public string FailureReason { get; init; } = "";

    /// <summary>
    /// Engine extension: when set, the engine continues at this node instead of selecting an outgoing edge.
    /// Used by the parallel handler to continue at the branches' join (fan-in) node.
    /// </summary>
    public string? JumpTo { get; init; }

    /// <summary>
    /// Reactor extension: the artifacts this outcome records (set by <see cref="StageArtifacts"/>; the records themselves are
    /// in <see cref="ContextUpdates"/> as <c>artifacts.&lt;name&gt;</c>). When the engine records the outcome it emits an
    /// <see cref="Events.ArtifactProduced"/> event for each, so attempts that are retried announce nothing. Not part of
    /// the status file.
    /// </summary>
    public IReadOnlyList<ArtifactRecord> Artifacts { get; init; } = [];

    public bool IsSuccessful => Status.IsSuccessful();

    public static Outcome Success(string notes = "", IReadOnlyDictionary<string, object?>? contextUpdates = null) =>
        new() { Status = StageStatus.Success, Notes = notes, ContextUpdates = contextUpdates ?? new Dictionary<string, object?>() };

    public static Outcome PartialSuccess(string notes = "", IReadOnlyDictionary<string, object?>? contextUpdates = null) =>
        new() { Status = StageStatus.PartialSuccess, Notes = notes, ContextUpdates = contextUpdates ?? new Dictionary<string, object?>() };

    public static Outcome Fail(string failureReason, IReadOnlyDictionary<string, object?>? contextUpdates = null) =>
        new() { Status = StageStatus.Fail, FailureReason = failureReason, ContextUpdates = contextUpdates ?? new Dictionary<string, object?>() };

    public static Outcome Retry(string failureReason) => new() { Status = StageStatus.Retry, FailureReason = failureReason };

    public static Outcome Skipped(string notes = "") => new() { Status = StageStatus.Skipped, Notes = notes };

    /// <summary>Serializes to the status-file contract (Appendix C) plus <c>failure_reason</c>.</summary>
    public JsonObject ToStatusJson()
    {
        var json = new JsonObject
        {
            ["outcome"] = Status.ToWire(),
            ["preferred_label"] = PreferredLabel,
            ["suggested_next_ids"] = new JsonArray(SuggestedNextIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["context_updates"] = new JsonObject(ContextUpdates.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ContextValues.ToJson(ContextValues.Normalize(kv.Value))))),
            ["notes"] = Notes,
        };
        if (!string.IsNullOrEmpty(FailureReason))
        {
            json["failure_reason"] = FailureReason;
        }

        if (!string.IsNullOrEmpty(JumpTo))
        {
            json["jump_to"] = JumpTo;
        }

        return json;
    }

    /// <summary>Parses a status file. <c>outcome</c> is required; other fields are optional.</summary>
    /// <exception cref="FormatException">The JSON does not follow the status-file contract.</exception>
    public static Outcome FromStatusJson(JsonNode? json)
    {
        if (json is not JsonObject obj)
        {
            throw new FormatException("status.json must contain a JSON object.");
        }

        var statusText = obj["outcome"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? obj["outcome"]!.GetValue<string>()
            : obj["status"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? obj["status"]!.GetValue<string>() : null;
        if (statusText is null)
        {
            throw new FormatException("status.json requires an 'outcome' field.");
        }

        if (!StageStatusExtensions.TryParse(statusText, out var status))
        {
            throw new FormatException($"status.json has an unknown outcome '{statusText}'.");
        }

        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (obj["context_updates"] is JsonObject contextUpdates)
        {
            foreach (var (key, value) in contextUpdates)
            {
                updates[key] = ContextValues.FromJson(value);
            }
        }

        var suggested = obj["suggested_next_ids"] is JsonArray arr
            ? arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList()
            : [];

        return new Outcome
        {
            Status = status,
            PreferredLabel = StringField(obj, "preferred_label"),
            SuggestedNextIds = suggested,
            ContextUpdates = updates,
            Notes = StringField(obj, "notes"),
            FailureReason = StringField(obj, "failure_reason"),
            JumpTo = obj["jump_to"] is JsonValue jv && jv.TryGetValue<string>(out var jump) && jump.Length > 0 ? jump : null,
        };
    }

    private static string StringField(JsonObject obj, string name) => obj[name] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join("\n", a.Select(n => n?.ToString())),
        null => "",
        var other => other.ToJsonString(),
    };
}

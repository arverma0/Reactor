using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Execution;

/// <summary>
/// Reactor extension: a run's usage totals, summed from the <c>backend.json</c> of every stage attempt (failed and timed-out
/// attempts included). Thread-safe; saved in the checkpoint so totals survive a resume.
/// </summary>
public sealed class UsageLedger
{
    /// <summary>The numeric <c>backend.json</c> fields that are summed.</summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        "cost_usd", "ai_units", "premium_requests", "input_tokens", "output_tokens", "cache_read_tokens", "reasoning_tokens", "model_calls", "tool_calls",
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _totals = new(StringComparer.Ordinal);

    /// <summary>Total recorded cost in US dollars (0 when no stage reported <c>cost_usd</c>).</summary>
    public double CostUsd
    {
        get
        {
            lock (_gate)
            {
                return _totals.GetValueOrDefault("cost_usd");
            }
        }
    }

    /// <summary>Adds one attempt's metadata. Returns false when the attempt recorded usage but no <c>cost_usd</c>.</summary>
    public bool Add(JsonObject metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var any = false;
        var priced = false;
        lock (_gate)
        {
            foreach (var field in Fields)
            {
                if (ContextValues.TryGetNumber(metadata[field], out var number))
                {
                    _totals[field] = _totals.GetValueOrDefault(field) + number;
                    any = true;
                    priced |= field == "cost_usd";
                }
            }
        }

        return !any || priced;
    }

    /// <summary>
    /// Adds the <c>backend.json</c> at <paramref name="path"/> when it was written at or after <paramref name="notBeforeUtc"/>
    /// (so a file left over from an earlier attempt is never counted twice). Returns the parsed metadata, or null.
    /// </summary>
    public JsonObject? TryAddFile(string path, DateTime notBeforeUtc)
    {
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < notBeforeUtc)
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject metadata)
            {
                return null;
            }

            Add(metadata);
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public IReadOnlyDictionary<string, double> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, double>(_totals, StringComparer.Ordinal);
        }
    }

    public void Restore(IReadOnlyDictionary<string, double> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        lock (_gate)
        {
            _totals.Clear();
            foreach (var (key, value) in totals)
            {
                _totals[key] = value;
            }
        }
    }

    /// <summary>The totals as a JSON object (fields in <see cref="Fields"/> order).</summary>
    public static JsonObject ToJson(IReadOnlyDictionary<string, double> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        var json = new JsonObject();
        foreach (var field in Fields.Concat(totals.Keys.Except(Fields, StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            if (totals.TryGetValue(field, out var value))
            {
                json[field] = field.EndsWith("_tokens", StringComparison.Ordinal) || field.EndsWith("_calls", StringComparison.Ordinal)
                    ? JsonValue.Create((long)Math.Round(value))
                    : JsonValue.Create(Math.Round(value, 6));
            }
        }

        return json;
    }

    public static Dictionary<string, double> FromJson(JsonNode? json)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        if (json is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (ContextValues.TryGetNumber(value, out var number))
                {
                    totals[key] = number;
                }
            }
        }

        return totals;
    }

    /// <summary>
    /// US dollars for people: cents for amounts of a cent or more (<c>$4.12</c>), two significant digits below that
    /// (<c>$0.0005</c>), and <c>$0</c> for nothing.
    /// </summary>
    public static string FormatUsd(double usd)
    {
        if (usd == 0 || !double.IsFinite(usd))
        {
            return "$0";
        }

        var magnitude = Math.Abs(usd);
        if (magnitude >= 0.01)
        {
            return string.Create(CultureInfo.InvariantCulture, $"${usd:0.00}");
        }

        var decimals = Math.Clamp(1 - (int)Math.Floor(Math.Log10(magnitude)), 2, 10);
        return "$" + Math.Round(usd, decimals).ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>A one-line summary such as <c>$4.12 · 412 AI units · 1.2M in / 40K out tokens</c> (empty when nothing was recorded).</summary>
    public static string Describe(IReadOnlyDictionary<string, double> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        var parts = new List<string>();
        if (totals.TryGetValue("cost_usd", out var usd))
        {
            parts.Add(FormatUsd(usd));
        }

        if (totals.TryGetValue("ai_units", out var aiu))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{aiu:0.##} AI units"));
        }

        if (totals.TryGetValue("premium_requests", out var requests))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{requests:0.##} premium requests"));
        }

        if (totals.TryGetValue("input_tokens", out var input) | totals.TryGetValue("output_tokens", out var output))
        {
            parts.Add($"{Compact(input)} in / {Compact(output)} out tokens");
        }

        return string.Join(" · ", parts);

        static string Compact(double n) => n switch
        {
            >= 1e9 => string.Create(CultureInfo.InvariantCulture, $"{n / 1e9:0.#}B"),
            >= 1e6 => string.Create(CultureInfo.InvariantCulture, $"{n / 1e6:0.#}M"),
            >= 1e3 => string.Create(CultureInfo.InvariantCulture, $"{n / 1e3:0.#}K"),
            _ => n.ToString("0", CultureInfo.InvariantCulture),
        };
    }
}

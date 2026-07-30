using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Execution;

/// <summary>
/// Reactor extension: outcome metrics from files. After a stage runs, the engine merges metrics from
/// <c>{stage}/metrics.json</c> (e.g. written by a tool command via <c>$REACTOR_STAGE_DIR</c>) and from the node's
/// <c>metrics_file</c> (a report in the working directory, e.g. <c>qa/report.json</c>) into the outcome; metrics in
/// <c>status.json</c> take precedence.
/// </summary>
public static class StageMetrics
{
    /// <summary>File name of a stage's own metrics file in its stage directory.</summary>
    public const string FileName = "metrics.json";

    /// <summary>How deep nested objects are flattened (<c>summary.totals.passed</c> is depth 3).</summary>
    public const int MaxDepth = 3;

    /// <summary>
    /// Flattens a JSON document into metrics: numbers (and booleans as 1/0) become entries; nested objects become dotted
    /// names up to <see cref="MaxDepth"/>; arrays, strings, and nulls are ignored. At most <see cref="Outcome.MaxMetrics"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Flatten(JsonNode? json)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        if (json is JsonObject obj)
        {
            Visit(obj, "", 1, metrics);
        }

        return metrics;
    }

    /// <summary>Reads and flattens a metrics file; throws on unreadable or invalid JSON.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="JsonException">The file is not valid JSON.</exception>
    public static IReadOnlyDictionary<string, double> ReadFile(string path) => Flatten(JsonNode.Parse(File.ReadAllText(path)));

    /// <summary><paramref name="first"/> overlaid with <paramref name="second"/> (second wins), capped at <see cref="Outcome.MaxMetrics"/>.</summary>
    public static IReadOnlyDictionary<string, double> Merge(IReadOnlyDictionary<string, double> first, IReadOnlyDictionary<string, double> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in second.Concat(first))
        {
            if (merged.Count >= Outcome.MaxMetrics)
            {
                break;
            }

            merged.TryAdd(key, value);
        }

        return merged;
    }

    private static void Visit(JsonObject obj, string prefix, int depth, Dictionary<string, double> metrics)
    {
        foreach (var (key, value) in obj)
        {
            if (metrics.Count >= Outcome.MaxMetrics)
            {
                return;
            }

            var name = prefix + key;
            switch (value)
            {
                case JsonObject child when depth < MaxDepth:
                    Visit(child, name + ".", depth + 1, metrics);
                    break;
                case JsonValue v when Outcome.IsMetricName(name):
                    switch (v.GetValueKind())
                    {
                        case JsonValueKind.Number when ContextValues.TryGetNumber(v, out var number):
                            metrics[name] = number;
                            break;
                        case JsonValueKind.True:
                            metrics[name] = 1;
                            break;
                        case JsonValueKind.False:
                            metrics[name] = 0;
                            break;
                    }

                    break;
            }
        }
    }
}

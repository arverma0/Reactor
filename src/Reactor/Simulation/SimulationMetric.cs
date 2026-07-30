using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Execution;

namespace Reactor.Simulation;

/// <summary>
/// How a simulated stage reports one outcome metric (scenario field <c>metrics</c>; see <see cref="Outcome.Metrics"/>).
/// JSON forms:
/// <list type="bullet">
/// <item><c>4</c> — always this value;</item>
/// <item><c>{"min": 1, "max": 5}</c> — uniform; whole numbers when both bounds are written as whole numbers (add
/// <c>"integer": false</c> for a continuous range over whole bounds, or write <c>1.0</c>);</item>
/// <item><c>{"values": {"3": 0.2, "4": 0.5, "5": 0.3}}</c> — weighted values (<c>{"values": [3, 4, 5]}</c> for equal weights);</item>
/// <item><c>{"success": 0, "fail": {"min": 1, "max": 5}, "*": 0}</c> — one of the above per resulting status (wire name, or
/// <c>*</c> for every status without its own entry); a status without an entry reports nothing.</item>
/// </list>
/// Values are drawn per execution from the sample's random stream, so samples replay exactly.
/// </summary>
public abstract record SimulationMetric
{
    private const string Expected =
        "must be a number, {\"min\": a, \"max\": b}, {\"values\": {\"<value>\": <weight>, ...}}, or an object of those keyed by status";

    private SimulationMetric()
    {
    }

    /// <summary>The value reported for an execution that ended with <paramref name="status"/> (null: not reported).</summary>
    /// <param name="status">The execution's resulting status.</param>
    /// <param name="uniform">A uniform draw in [0, 1).</param>
    public abstract double? Draw(StageStatus status, double uniform);

    /// <summary>The canonical JSON form (round-trips through <see cref="SimulationScenario.FromJson"/>).</summary>
    public abstract JsonNode ToJson();

    /// <summary>A constant value.</summary>
    public sealed record Constant(double Value) : SimulationMetric
    {
        public override double? Draw(StageStatus status, double uniform) => Value;

        public override JsonNode ToJson() => JsonValue.Create(Value);
    }

    /// <summary>A uniform draw from [<see cref="Min"/>, <see cref="Max"/>]: whole numbers when <see cref="Integer"/>.</summary>
    public sealed record Uniform(double Min, double Max, bool Integer) : SimulationMetric
    {
        public override double? Draw(StageStatus status, double uniform)
        {
            if (Integer)
            {
                var low = (long)Min;
                var span = (long)Max - low;
                return low + Math.Min(span, (long)Math.Floor(uniform * (span + 1)));
            }

            // Four decimals keep event logs and traces readable; no condition needs more.
            return Math.Round(Min + (uniform * (Max - Min)), 4);
        }

        public override JsonNode ToJson()
        {
            var json = new JsonObject { ["min"] = Min, ["max"] = Max };
            if (!Integer && IsWhole(Min) && IsWhole(Max))
            {
                json["integer"] = false;
            }

            return json;
        }
    }

    /// <summary>A weighted choice among values (weights need not be normalized).</summary>
    public sealed record Weighted(IReadOnlyList<KeyValuePair<double, double>> Values) : SimulationMetric
    {
        public override double? Draw(StageStatus status, double uniform) =>
            Values[SimulatedDecisions.PickIndex(Values.Select(v => v.Value).ToList(), uniform)].Key;

        public override JsonNode ToJson() => new JsonObject
        {
            ["values"] = new JsonObject(Values.Select(v => KeyValuePair.Create(FormatValue(v.Key), (JsonNode?)v.Value))),
        };
    }

    /// <summary>A distribution per resulting status (wire name, or <c>*</c> for every status without an entry).</summary>
    public sealed record PerStatus(IReadOnlyDictionary<string, SimulationMetric> ByStatus) : SimulationMetric
    {
        public override double? Draw(StageStatus status, double uniform) =>
            (ByStatus.GetValueOrDefault(status.ToWire()) ?? ByStatus.GetValueOrDefault(SimulationScenario.AnyStatus))?.Draw(status, uniform);

        public override JsonNode ToJson() =>
            new JsonObject(ByStatus.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.ToJson())));
    }

    /// <summary>Parses the <c>metrics</c> field of a behavior: an object of metric names to distributions.</summary>
    internal static IReadOnlyDictionary<string, SimulationMetric>? ParseAll(JsonNode value, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        if (value is not JsonObject obj)
        {
            errors.Add(new SimulationScenarioError(path, "must be an object of metric names, e.g. {\"qa_score\": {\"min\": 3, \"max\": 5}}") { NodeId = nodeId });
            return null;
        }

        var metrics = new Dictionary<string, SimulationMetric>(StringComparer.Ordinal);
        var valid = true;
        foreach (var (key, entry) in obj)
        {
            var name = key.Trim();
            var entryPath = $"{path}.{key}";
            if (!Outcome.IsMetricName(name))
            {
                errors.Add(new SimulationScenarioError(entryPath, "is not a metric name (letters, digits, '_', '-', '.'; starting with a letter or '_')") { NodeId = nodeId });
                valid = false;
                continue;
            }

            if (Parse(entry, entryPath, nodeId, errors, allowPerStatus: true) is { } metric)
            {
                metrics[name] = metric;
            }
            else
            {
                valid = false;
            }
        }

        if (metrics.Count > Outcome.MaxMetrics)
        {
            errors.Add(new SimulationScenarioError(path, $"at most {Outcome.MaxMetrics} metrics per stage") { NodeId = nodeId });
            return null;
        }

        return valid && metrics.Count > 0 ? metrics : null;
    }

    private static SimulationMetric? Parse(JsonNode? value, string path, string? nodeId, List<SimulationScenarioError> errors, bool allowPerStatus)
    {
        if (TryNumber(value, out var constant, out _))
        {
            return new Constant(constant);
        }

        if (value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return new Constant(flag.GetValueKind() == JsonValueKind.True ? 1 : 0);
        }

        if (value is not JsonObject obj || obj.Count == 0)
        {
            errors.Add(new SimulationScenarioError(path, Expected) { NodeId = nodeId });
            return null;
        }

        var keys = obj.Select(kv => kv.Key.Trim().ToLowerInvariant()).ToList();
        var distributionKeys = keys.Count(k => k is "min" or "max" or "integer" or "values");
        if (distributionKeys == keys.Count)
        {
            return keys.Contains("values")
                ? ParseWeighted(obj, path, nodeId, errors)
                : ParseUniform(obj, path, nodeId, errors);
        }

        if (distributionKeys > 0 || !allowPerStatus)
        {
            errors.Add(new SimulationScenarioError(path, allowPerStatus
                ? "mixes a distribution (min, max, values) with other keys"
                : "per-status entries must be a number, {\"min\": a, \"max\": b}, or {\"values\": {...}}") { NodeId = nodeId });
            return null;
        }

        var byStatus = new Dictionary<string, SimulationMetric>(StringComparer.Ordinal);
        var valid = true;
        foreach (var (key, entry) in obj)
        {
            string statusKey;
            if (key.Trim() is SimulationScenario.AnyStatus or "any" or "all")
            {
                statusKey = SimulationScenario.AnyStatus;
            }
            else if (StageStatusExtensions.TryParse(key, out var status))
            {
                statusKey = status.ToWire();
            }
            else
            {
                errors.Add(new SimulationScenarioError($"{path}.{key}",
                    $"unknown status '{key}' (expected success, partial_success, retry, fail, skipped, or \"*\"), or not a distribution field (min, max, integer, values)") { NodeId = nodeId });
                valid = false;
                continue;
            }

            if (Parse(entry, $"{path}.{key}", nodeId, errors, allowPerStatus: false) is { } metric)
            {
                byStatus[statusKey] = metric;
            }
            else
            {
                valid = false;
            }
        }

        return valid ? new PerStatus(byStatus) : null;
    }

    private static Uniform? ParseUniform(JsonObject obj, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        var fields = obj.ToDictionary(kv => kv.Key.Trim().ToLowerInvariant(), kv => kv.Value, StringComparer.Ordinal);
        if (!TryNumber(fields.GetValueOrDefault("min"), out var min, out var minWhole) || !TryNumber(fields.GetValueOrDefault("max"), out var max, out var maxWhole))
        {
            errors.Add(new SimulationScenarioError(path, "a range needs numeric \"min\" and \"max\"") { NodeId = nodeId });
            return null;
        }

        if (min > max)
        {
            errors.Add(new SimulationScenarioError(path, "\"min\" must not be greater than \"max\"") { NodeId = nodeId });
            return null;
        }

        var integer = minWhole && maxWhole;
        if (fields.TryGetValue("integer", out var explicitInteger) && explicitInteger is not null)
        {
            if (explicitInteger is not JsonValue v || v.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add(new SimulationScenarioError($"{path}.integer", "must be true or false") { NodeId = nodeId });
                return null;
            }

            integer = v.GetValueKind() == JsonValueKind.True;
            if (integer && (!IsWhole(min) || !IsWhole(max)))
            {
                errors.Add(new SimulationScenarioError(path, "a whole-number range needs whole-number bounds") { NodeId = nodeId });
                return null;
            }
        }

        if (integer && (Math.Abs(min) > 1e15 || Math.Abs(max) > 1e15))
        {
            errors.Add(new SimulationScenarioError(path, "whole-number bounds must be within ±1e15") { NodeId = nodeId });
            return null;
        }

        return new Uniform(min, max, integer);
    }

    private static Weighted? ParseWeighted(JsonObject obj, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        var entry = obj.First(kv => kv.Key.Trim().Equals("values", StringComparison.OrdinalIgnoreCase));
        var valuesPath = $"{path}.{entry.Key}";
        if (obj.Count > 1)
        {
            errors.Add(new SimulationScenarioError(path, "\"values\" cannot be combined with \"min\", \"max\", or \"integer\"") { NodeId = nodeId });
            return null;
        }

        var weights = new Dictionary<double, double>();
        var valid = true;
        switch (entry.Value)
        {
            case JsonArray list when list.Count > 0:
                for (var i = 0; i < list.Count; i++)
                {
                    if (TryNumber(list[i], out var item, out _))
                    {
                        weights[item] = weights.GetValueOrDefault(item) + 1;
                    }
                    else
                    {
                        errors.Add(new SimulationScenarioError($"{valuesPath}[{i}]", "must be a number") { NodeId = nodeId });
                        valid = false;
                    }
                }

                break;
            case JsonObject map when map.Count > 0:
                foreach (var (key, weightNode) in map)
                {
                    if (!double.TryParse(key.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    {
                        errors.Add(new SimulationScenarioError($"{valuesPath}.{key}", "the key must be a number (the metric value)") { NodeId = nodeId });
                        valid = false;
                        continue;
                    }

                    if (!TryNumber(weightNode, out var weight, out _) || weight < 0)
                    {
                        errors.Add(new SimulationScenarioError($"{valuesPath}.{key}", "weight must be a non-negative number") { NodeId = nodeId });
                        valid = false;
                        continue;
                    }

                    weights[number] = weights.GetValueOrDefault(number) + weight;
                }

                break;
            default:
                errors.Add(new SimulationScenarioError(valuesPath, "must be an object of values to weights, e.g. {\"3\": 0.2, \"4\": 0.8}, or a list of values") { NodeId = nodeId });
                return null;
        }

        if (!valid)
        {
            return null;
        }

        if (weights.Values.Sum() <= 0)
        {
            errors.Add(new SimulationScenarioError(valuesPath, "weights must not all be zero") { NodeId = nodeId });
            return null;
        }

        return new Weighted(weights.OrderBy(kv => kv.Key).ToList());
    }

    /// <summary>Reads a finite number from a JSON number or numeric string; <paramref name="whole"/> is true when written without a fraction or exponent.</summary>
    private static bool TryNumber(JsonNode? node, out double value, out bool whole)
    {
        value = 0;
        whole = false;
        if (node is not JsonValue v)
        {
            return false;
        }

        var text = v.GetValueKind() switch
        {
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.String => v.GetValue<string>().Trim(),
            _ => null,
        };
        if (text is null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            return false;
        }

        whole = text.IndexOfAny(['.', 'e', 'E']) < 0 && IsWhole(value);
        return true;
    }

    private static bool IsWhole(double value) => double.IsFinite(value) && value == Math.Floor(value);

    private static string FormatValue(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

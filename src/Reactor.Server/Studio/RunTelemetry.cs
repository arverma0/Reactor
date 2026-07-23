using System.Text.Json.Nodes;

namespace Reactor.Server.Studio;

/// <summary>
/// Aggregates run records into per-pipeline telemetry: outcomes, durations, and costs per run, and per node (stage)
/// visits, outcomes, retries, durations, cost per visit, models, and top failure reasons; selected edges; and a per-run
/// series for sparklines. Values that no run reported are omitted rather than shown as zero.
/// </summary>
internal static class RunTelemetry
{
    private const int TopFailures = 5;

    /// <summary>Telemetry over <paramref name="runs"/> (newest first) of the pipeline named <paramref name="name"/>.</summary>
    public static JsonObject Compute(string name, IReadOnlyList<RunRecord> runs)
    {
        var oldestFirst = runs.Reverse().ToList();
        var json = new JsonObject { ["name"] = name, ["runs"] = runs.Count };
        if (runs.Count > 0)
        {
            json["window"] = new JsonObject
            {
                ["from"] = HistoryJson.Iso(runs.Min(r => r.StartedAt)),
                ["to"] = HistoryJson.Iso(runs.Max(r => r.FinishedAt ?? r.StartedAt)),
            };
        }

        json["outcomes"] = new JsonObject
        {
            [RunStatus.Success] = runs.Count(r => r.Status == RunStatus.Success),
            [RunStatus.Fail] = runs.Count(r => r.Status == RunStatus.Fail),
            [RunStatus.Cancelled] = runs.Count(r => r.Status == RunStatus.Cancelled),
            [RunStatus.PartialSuccess] = runs.Count(r => r.Status == RunStatus.PartialSuccess),
        };

        if (Distribution(runs.Select(r => r.DurationMs).OfType<long>().Select(d => (double)d).ToList(), withMax: false) is { } durations)
        {
            json["duration_ms"] = durations;
        }

        var cost = new JsonObject();
        for (var i = 0; i < CostTotals.Fields.Length; i++)
        {
            var reported = runs.Select(r => r.Cost[i]).OfType<double>().ToList();
            if (reported.Count > 0)
            {
                cost[CostTotals.Fields[i]] = new JsonObject { ["mean"] = Number(reported.Average(), 6), ["total"] = Number(reported.Sum(), 6) };
            }
        }

        json["cost"] = cost;
        json["nodes"] = Nodes(oldestFirst);
        json["edges"] = Edges(oldestFirst);
        json["series"] = new JsonArray(oldestFirst.Select(r => (JsonNode?)SeriesPoint(r)).ToArray());
        return json;
    }

    private static JsonObject Nodes(IReadOnlyList<RunRecord> runs)
    {
        var nodes = new OrderedDictionary<string, List<NodeStats>>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            foreach (var (id, stats) in run.Nodes)
            {
                if (stats.Visits == 0 && stats.Statuses.Count == 0 && stats.Usage.Count == 0)
                {
                    continue;
                }

                if (!nodes.TryGetValue(id, out var list))
                {
                    nodes[id] = list = [];
                }

                list.Add(stats);
            }
        }

        var json = new JsonObject();
        foreach (var (id, perRun) in nodes)
        {
            var visits = perRun.Sum(s => s.Visits);
            var outcomes = new JsonObject();
            foreach (var group in perRun.SelectMany(s => s.Statuses).Where(s => s.Length > 0).GroupBy(s => s, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                outcomes[group.Key] = group.Count();
            }

            var node = new JsonObject
            {
                ["runs"] = perRun.Count,
                ["visits"] = visits,
                ["mean_visits"] = Number((double)visits / perRun.Count, 3),
                ["outcomes"] = outcomes,
            };

            var completed = perRun.Sum(s => s.Statuses.Count);
            if (completed > 0)
            {
                node["fail_rate"] = Number((double)perRun.Sum(s => s.Statuses.Count(status => status == RunStatus.Fail)) / completed, 4);
            }

            node["retries"] = perRun.Sum(s => s.Retries);
            if (Distribution(perRun.SelectMany(s => s.DurationsMs).ToList(), withMax: true) is { } durations)
            {
                node["duration_ms"] = durations;
            }

            var usage = perRun.SelectMany(s => s.Usage).ToList();
            var cost = new JsonObject();
            for (var i = 0; i < CostTotals.Fields.Length; i++)
            {
                var reported = usage.Select(u => u.Values[i]).OfType<double>().ToList();
                if (reported.Count > 0)
                {
                    cost[CostTotals.Fields[i]] = Number(reported.Average(), 6);
                }
            }

            node["cost"] = cost;
            var models = new JsonObject();
            foreach (var group in usage.Where(u => !string.IsNullOrEmpty(u.Model)).GroupBy(u => u.Model!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                models[group.Key] = group.Count();
            }

            node["models"] = models;
            var failures = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (reason, count) in perRun.SelectMany(s => s.Failures))
            {
                failures[reason] = failures.GetValueOrDefault(reason) + count;
            }

            node["top_failures"] = new JsonArray(failures
                .OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal)
                .Take(TopFailures)
                .Select(f => (JsonNode?)new JsonObject { ["reason"] = f.Key, ["count"] = f.Value })
                .ToArray());
            json[id] = node;
        }

        return json;
    }

    private static JsonArray Edges(IReadOnlyList<RunRecord> runs)
    {
        var edges = new Dictionary<(string From, string To), (int Count, int Runs)>();
        foreach (var run in runs)
        {
            foreach (var (edge, count) in run.Edges)
            {
                var (total, inRuns) = edges.GetValueOrDefault(edge);
                edges[edge] = (total + count, inRuns + 1);
            }
        }

        return new JsonArray(edges
            .OrderByDescending(e => e.Value.Count).ThenBy(e => e.Key.From, StringComparer.Ordinal).ThenBy(e => e.Key.To, StringComparer.Ordinal)
            .Select(e => (JsonNode?)new JsonObject { ["from"] = e.Key.From, ["to"] = e.Key.To, ["count"] = e.Value.Count, ["runs"] = e.Value.Runs })
            .ToArray());
    }

    private static JsonObject SeriesPoint(RunRecord run)
    {
        var point = new JsonObject
        {
            ["id"] = run.Id,
            ["started_at"] = HistoryJson.Iso(run.StartedAt),
            ["status"] = run.Status,
        };
        if (run.DurationMs is { } duration)
        {
            point["duration_ms"] = duration;
        }

        point["stage_executions"] = run.StageExecutions;
        foreach (var field in new[] { "ai_units", "premium_requests", "cost_usd" })
        {
            if (run.Cost.Get(field) is { } value)
            {
                point[field] = CostTotals.Number(value);
            }
        }

        return point;
    }

    /// <summary>Mean, median, and 95th percentile (linear interpolation between ranks), plus the maximum; null when empty.</summary>
    private static JsonObject? Distribution(List<double> values, bool withMax)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var json = new JsonObject
        {
            ["mean"] = Number(values.Average(), 3),
            ["p50"] = Number(Percentile(values, 0.50), 3),
            ["p95"] = Number(Percentile(values, 0.95), 3),
        };
        if (withMax)
        {
            json["max"] = Number(values[^1], 3);
        }

        return json;
    }

    /// <summary>The <paramref name="p"/> quantile of sorted <paramref name="sorted"/>, interpolating between neighbors.</summary>
    internal static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        var position = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    private static JsonNode Number(double value, int digits) => CostTotals.Number(Math.Round(value, digits));
}

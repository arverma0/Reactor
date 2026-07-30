using System.Globalization;
using System.Text;

namespace Reactor.Simulation;

/// <summary>Options for <see cref="SimulationReportText.Format"/>.</summary>
public sealed record SimulationTextOptions
{
    /// <summary>What was flown (pipeline name or file), for the header.</summary>
    public string? Pipeline { get; init; }

    /// <summary>The scenario (preset name or file), for the header.</summary>
    public string? Scenario { get; init; }

    /// <summary>The required clean landing rate; adds a verdict line.</summary>
    public double? MinSuccess { get; init; }

    /// <summary>
    /// A command that replays one flight, with <c>{run}</c> where the run index goes (e.g.
    /// <c>reactor simulate f.dot --seed 42 --trace {run}</c>); printed once under the report.
    /// </summary>
    public string? ReplayCommand { get; init; }

    /// <summary>Most rows per list (failure groups, hot spots, loops, nodes).</summary>
    public int MaxRows { get; init; } = 8;
}

/// <summary>A plain-text rendering of a <see cref="SimulationReport"/> for terminals (<c>reactor simulate</c>).</summary>
public static class SimulationReportText
{
    /// <summary>Display names of outcome kinds (as in the Studio).</summary>
    public static IReadOnlyDictionary<string, string> KindLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SimulationOutcomeKinds.Success] = "landed",
        [SimulationOutcomeKinds.Fail] = "failed",
        [SimulationOutcomeKinds.StepLimit] = "never converged",
        [SimulationOutcomeKinds.GoalGate] = "goal gate unmet",
        [SimulationOutcomeKinds.DeadEnd] = "dead end",
        [SimulationOutcomeKinds.Error] = "error",
    };

    /// <summary>Renders the report: readiness (clean landings first), outcomes, failure groups, failed-stage landings, hot spots, loops, time and cost, metrics at landing, and notes.</summary>
    public static string Format(SimulationReport report, SimulationTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new SimulationTextOptions();
        var rows = Math.Max(1, options.MaxRows);
        var text = new StringBuilder();
        var runs = Math.Max(1, report.Runs);

        var header = new List<string>();
        if (options.Pipeline is { Length: > 0 } pipeline)
        {
            header.Add(pipeline);
        }

        if (options.Scenario is { Length: > 0 } scenario)
        {
            header.Add($"{scenario} scenario");
        }

        header.Add($"{Count(report.Runs)} flights");
        header.Add($"seed {report.Seed.ToString(CultureInfo.InvariantCulture)}");
        header.Add($"flown in {Duration(report.DurationMs)}");
        text.AppendLine("Wind tunnel: " + string.Join(" · ", header));
        text.AppendLine();

        var landed = report.Outcomes.GetValueOrDefault(SimulationOutcomeKinds.Success);
        var clean = landed - report.FailedStageLandings;
        var down = report.Runs - landed;
        text.AppendLine("Readiness");
        Table(text, [
            ["Landed clean", Percent(report.CleanSuccessRate), $"95% CI {Percent(report.CleanSuccessInterval.Low)}–{Percent(report.CleanSuccessInterval.High)}", $"{Count(clean)} of {Count(report.Runs)}"],
            ["Landed with failed stages", Percent(report.FailedStageLandings / (double)runs), $"{Count(report.FailedStageLandings)} flight{Plural(report.FailedStageLandings)}", FailedStageSummary(report, 3)],
            ["Did not land", Percent(down / (double)runs), $"{Count(down)} flight{Plural(down)}", ""],
        ]);
        text.AppendLine();

        text.AppendLine("Outcomes");
        var outcomeRows = new List<string[]>();
        foreach (var kind in SimulationOutcomeKinds.All.Where(k => report.Outcomes.GetValueOrDefault(k) > 0))
        {
            var count = report.Outcomes[kind];
            var detail = kind == SimulationOutcomeKinds.Success && report.FailedStageLandings > 0
                ? $"{Count(clean)} clean, {Count(report.FailedStageLandings)} with failed stages"
                : "";
            outcomeRows.Add([KindLabels.GetValueOrDefault(kind, kind), Count(count), Percent(count / (double)runs), detail]);
        }

        Table(text, outcomeRows);

        if (report.Failures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("How flights go down");
            foreach (var group in report.Failures.Take(rows))
            {
                text.AppendLine($"  {KindLabels.GetValueOrDefault(group.Kind, group.Kind)} · {Percent(group.Count / (double)runs)} ({Count(group.Count)}): {group.Reason}");
                var where = group.Nodes.Count switch
                {
                    0 => "",
                    1 => $"at {group.Nodes.Keys.First()}",
                    _ => "last stages " + string.Join(", ", group.Nodes.Take(5).Select(kv => $"{kv.Key} {Count(kv.Value)}")) + (group.Nodes.Count > 5 ? ", …" : ""),
                };
                text.AppendLine($"      {(where.Length > 0 ? where + " · " : "")}replay run {group.Sample.RunIndex.ToString(CultureInfo.InvariantCulture)}");
            }

            if (report.Failures.Count > rows)
            {
                text.AppendLine($"  … and {report.Failures.Count - rows} more");
            }
        }

        if (report.FailedStageLandings > 0)
        {
            text.AppendLine();
            text.AppendLine("Landed with failed stages (a failure flowed on along an unconditional edge)");
            var nodeRows = report.Nodes
                .Where(kv => kv.Value.FailedStageLandings > 0)
                .OrderByDescending(kv => kv.Value.FailedStageLandings)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(rows)
                .Select(kv => new[] { kv.Key, $"{Count(kv.Value.FailedStageLandings)} flight{Plural(kv.Value.FailedStageLandings)}", Percent(kv.Value.FailedStageLandings / (double)runs) })
                .ToList();
            Table(text, nodeRows);
            if (report.FirstFailedStageLanding is { } sample)
            {
                text.AppendLine($"  replay run {sample.RunIndex.ToString(CultureInfo.InvariantCulture)} · route failures explicitly (condition=\"outcome=fail\") or condition the onward edge on outcome=success");
            }
        }

        text.AppendLine();
        text.AppendLine("Hot spots (visits per flight)");
        var hot = report.Nodes
            .Where(kv => kv.Value.MeanVisits >= 1.05)
            .OrderByDescending(kv => kv.Value.MeanVisits)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(rows)
            .Select(kv => new[] { kv.Key, "×" + kv.Value.MeanVisits.ToString("0.00", CultureInfo.InvariantCulture), $"max {kv.Value.MaxVisits.ToString(CultureInfo.InvariantCulture)}", kv.Value.Retries > 0 ? $"{Count(kv.Value.Retries)} retries" : "" })
            .ToList();
        if (hot.Count == 0)
        {
            text.AppendLine("  none: no stage runs more than once per flight on average");
        }
        else
        {
            Table(text, hot);
        }

        var loops = report.Edges
            .Where(e => e.Count > e.Runs && e.Runs > 0)
            .OrderByDescending(e => e.Count / (double)e.Runs)
            .ThenByDescending(e => e.Count)
            .Take(Math.Min(rows, 5))
            .Select(e => new[] { $"{e.From} → {e.To}", "×" + (e.Count / (double)e.Runs).ToString("0.0", CultureInfo.InvariantCulture), $"per flight that took it ({Count(e.Runs)} flights)" })
            .ToList();
        if (loops.Count > 0)
        {
            text.AppendLine("Loops");
            Table(text, loops);
        }

        var steps = report.Steps;
        text.AppendLine();
        Table(text, [
            ["Stage executions per flight", string.Create(CultureInfo.InvariantCulture, $"mean {steps.Mean:0.0} · p50 {steps.P50} · p90 {steps.P90} · max {steps.Max}")],
            ["Simulated time per flight", report.SimulatedDuration.Max > 0
                ? $"mean {Duration(report.SimulatedDuration.Mean)} · p50 {Duration(report.SimulatedDuration.P50)} · p90 {Duration(report.SimulatedDuration.P90)} · max {Duration(report.SimulatedDuration.Max)}"
                : "not modeled (no durations in the scenario)"],
            ["Simulated cost per flight", report.SimulatedCost.Max > 0
                ? $"mean {Money(report.SimulatedCost.Mean)} · p50 {Money(report.SimulatedCost.P50)} · p90 {Money(report.SimulatedCost.P90)} · max {Money(report.SimulatedCost.Max)}"
                : "not modeled (set \"cost\" per stage in a scenario file)"],
        ], indent: "");

        if (report.LandedMetrics.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Metrics at landing");
            Table(text, report.LandedMetrics.Take(rows * 2).Select(kv =>
            {
                var m = kv.Value;
                var spread = m.Values is { } values
                    ? string.Join(", ", values.Select(v => $"{Number(v.Key)}: {Percent(v.Value / (double)Math.Max(1, m.Flights))}"))
                    : $"p10 {Number(m.P10)} · p50 {Number(m.P50)} · p90 {Number(m.P90)}";
                return new[] { kv.Key, $"mean {Number(m.Mean)}", $"min {Number(m.Min)} · max {Number(m.Max)}", spread, $"({Count(m.Flights)} flights)" };
            }).ToList());
        }

        if (report.Warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Scenario notes");
            foreach (var warning in report.Warnings)
            {
                text.AppendLine("  ⚠ " + warning);
            }
        }

        if (options.ReplayCommand is { Length: > 0 } replay)
        {
            text.AppendLine();
            text.AppendLine("Replay a flight: " + replay.Replace("{run}", "<run>", StringComparison.Ordinal));
        }

        if (options.MinSuccess is { } min)
        {
            text.AppendLine();
            text.AppendLine(report.CleanSuccessRate >= min
                ? $"✔ Clean landing rate {Percent(report.CleanSuccessRate)} meets the required {Percent(min)}."
                : $"✖ Clean landing rate {Percent(report.CleanSuccessRate)} is below the required {Percent(min)}.");
        }

        return text.ToString();
    }

    /// <summary>A duration for people: <c>850ms</c>, <c>4.2s</c>, <c>12m 05s</c>, <c>5h 02m</c>.</summary>
    public static string Duration(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0)
        {
            return "0s";
        }

        if (milliseconds < 1000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(milliseconds):0}ms");
        }

        var seconds = milliseconds / 1000;
        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#}s");
        }

        var total = (long)Math.Round(seconds);
        if (total < 3600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{total / 60}m {total % 60:00}s");
        }

        var minutes = (long)Math.Round(seconds / 60);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes / 60}h {minutes % 60:00}m");
    }

    /// <summary>An amount in USD: <c>$1,234</c>, <c>$12.34</c>, <c>$0.042</c>.</summary>
    public static string Money(double usd) => usd switch
    {
        >= 1000 => "$" + usd.ToString("N0", CultureInfo.InvariantCulture),
        >= 1 => "$" + usd.ToString("N2", CultureInfo.InvariantCulture),
        > 0 => "$" + usd.ToString("0.###", CultureInfo.InvariantCulture),
        _ => "$0",
    };

    private static string FailedStageSummary(SimulationReport report, int max)
    {
        var nodes = report.Nodes
            .Where(kv => kv.Value.FailedStageLandings > 0)
            .OrderByDescending(kv => kv.Value.FailedStageLandings)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        if (nodes.Count == 0)
        {
            return "";
        }

        return "failures flowed on at " + string.Join(", ", nodes.Take(max).Select(kv => $"{kv.Key} {Count(kv.Value.FailedStageLandings)}")) + (nodes.Count > max ? ", …" : "");
    }

    private static void Table(StringBuilder text, IReadOnlyList<string[]> rows, string indent = "  ")
    {
        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(r => r.Length);
        var widths = Enumerable.Range(0, columns).Select(c => rows.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
        foreach (var row in rows)
        {
            var cells = row.Select((cell, c) => c == row.Length - 1 ? cell : cell.PadRight(widths[c])).ToList();
            text.AppendLine((indent + string.Join("   ", cells)).TrimEnd());
        }
    }

    private static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

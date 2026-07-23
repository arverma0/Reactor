using System.Globalization;
using System.Text;
using Reactor.Graphs;

namespace Reactor.Execution;

/// <summary>
/// Synthesizes context-carryover text for stages that start a fresh LLM session (every fidelity except <c>full</c>).
/// Budgets follow spec 5.4: <c>truncate</c> minimal, <c>compact</c> moderate bullets, <c>summary:low</c> ≈600 tokens,
/// <c>summary:medium</c> ≈1500 tokens, <c>summary:high</c> ≈3000 tokens (1 token ≈ 4 characters).
/// </summary>
public static class PreambleBuilder
{
    private sealed record Budget(int MaxChars, int MaxStages, int NoteChars, int MaxContextKeys, int ValueChars, int LogLines);

    private static readonly Dictionary<string, Budget> Budgets = new(StringComparer.Ordinal)
    {
        [FidelityModes.Compact] = new(4_000, 20, 160, 20, 200, 0),
        [FidelityModes.SummaryLow] = new(2_400, 3, 120, 8, 120, 0),
        [FidelityModes.SummaryMedium] = new(6_000, 10, 300, 30, 300, 10),
        [FidelityModes.SummaryHigh] = new(12_000, 40, 600, 60, 800, 30),
    };

    /// <summary>Returns the preamble for <paramref name="fidelity"/>, or an empty string for <c>full</c>.</summary>
    public static string Build(string fidelity, Graph graph, PipelineContext context, IReadOnlyList<StageRecord> history, string runId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(history);
        if (fidelity == FidelityModes.Full)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Pipeline context");
        if (graph.Goal.Length > 0)
        {
            sb.Append("Goal: ").AppendLine(graph.Goal);
        }

        sb.Append("Run: ").AppendLine(runId);
        if (fidelity == FidelityModes.Truncate || !Budgets.TryGetValue(fidelity, out var budget))
        {
            return sb.ToString().ReplaceLineEndings("\n").TrimEnd();
        }

        if (history.Count == 0)
        {
            sb.AppendLine("This is the first stage of the run.");
            return sb.ToString().ReplaceLineEndings("\n").TrimEnd();
        }

        var failures = history.Count(h => h.Status == StageStatus.Fail);
        var retried = history.Count(h => h.Attempts > 1);
        if (fidelity == FidelityModes.SummaryLow)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Progress: {history.Count} stage(s) completed");
            if (failures > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", {failures} failed");
            }

            if (retried > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", {retried} needed retries");
            }

            sb.AppendLine(".");
        }

        var stages = history.Skip(Math.Max(0, history.Count - budget.MaxStages)).ToList();
        sb.AppendLine(fidelity == FidelityModes.Compact ? "Completed stages:" : $"Recent stages ({stages.Count} of {history.Count}):");
        foreach (var stage in stages)
        {
            sb.Append("- ").Append(stage.NodeId);
            if (stage.Label.Length > 0 && stage.Label != stage.NodeId)
            {
                sb.Append(" (").Append(stage.Label).Append(')');
            }

            sb.Append(": ").Append(stage.Status.ToWire());
            if (stage.Attempts > 1)
            {
                sb.Append(CultureInfo.InvariantCulture, $" after {stage.Attempts} attempts");
            }

            var detail = stage.Status == StageStatus.Fail && stage.FailureReason.Length > 0 ? stage.FailureReason : stage.Notes;
            if (detail.Length > 0)
            {
                sb.Append(" — ").Append(Clip(OneLine(detail), budget.NoteChars));
            }

            sb.AppendLine();
        }

        var values = context.Snapshot()
            .Where(kv => !IsInternalKey(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(budget.MaxContextKeys)
            .ToList();
        if (values.Count > 0)
        {
            sb.AppendLine(fidelity == FidelityModes.Compact ? "Key context:" : "Context values:");
            foreach (var (key, value) in values)
            {
                sb.Append("- ").Append(key).Append(": ").AppendLine(Clip(OneLine(ContextValues.ToDisplayString(value)), budget.ValueChars));
            }
        }

        if (budget.LogLines > 0)
        {
            var notable = history.Where(h => h.Status != StageStatus.Success || h.Attempts > 1).TakeLast(budget.LogLines).ToList();
            if (notable.Count > 0)
            {
                sb.AppendLine("Notable events:");
                foreach (var stage in notable)
                {
                    sb.Append("- ").Append(stage.NodeId).Append(' ').Append(stage.Status.ToWire());
                    if (stage.Attempts > 1)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $" (attempts: {stage.Attempts})");
                    }

                    if (stage.FailureReason.Length > 0)
                    {
                        sb.Append(": ").Append(Clip(OneLine(stage.FailureReason), budget.NoteChars));
                    }

                    sb.AppendLine();
                }
            }

            var logs = context.Logs.TakeLast(budget.LogLines).ToList();
            if (logs.Count > 0 && fidelity == FidelityModes.SummaryHigh)
            {
                sb.AppendLine("Recent log entries:");
                foreach (var entry in logs)
                {
                    sb.Append("- ").AppendLine(Clip(OneLine(entry), budget.NoteChars));
                }
            }
        }

        var text = sb.ToString().ReplaceLineEndings("\n").TrimEnd();
        const string marker = "\n[... truncated]";
        return text.Length <= budget.MaxChars ? text : text[..(budget.MaxChars - marker.Length)] + marker;
    }

    /// <summary>
    /// Engine bookkeeping and bulky values that should not be carried into prompts. Artifact records (<c>artifacts.*</c>)
    /// are excluded too: a stage sees the artifacts it declares as <c>inputs</c> in its prompt's Inputs section.
    /// </summary>
    public static bool IsInternalKey(string key) =>
        key.StartsWith("internal.", StringComparison.Ordinal) ||
        key.StartsWith("graph.", StringComparison.Ordinal) ||
        key.StartsWith(ArtifactPins.ContextPrefix, StringComparison.Ordinal) ||
        key is "parallel.results" or "outcome" or "preferred_label" or "current_node";

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
}

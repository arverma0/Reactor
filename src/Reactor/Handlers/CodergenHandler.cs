using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Transforms;

namespace Reactor.Handlers;

/// <summary>
/// The default LLM stage handler (spec 4.5): builds the prompt (falling back to the label, expanding <c>$goal</c>,
/// prepending a context-carryover preamble for non-<c>full</c> fidelity), writes <c>prompt.md</c>, calls the
/// <see cref="ICodergenBackend"/> (or simulates a response), writes <c>response.md</c> and <c>status.json</c>,
/// and honors the status-file contract: if the backend's agent wrote <c>status.json</c> during the stage, that outcome wins.
/// </summary>
public sealed class CodergenHandler : INodeHandler
{
    /// <summary>Maximum length of <c>last_response</c> in the context.</summary>
    public const int LastResponseLength = 200;

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);

        var run = context.Run;
        var backend = run?.Backend;
        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);

        var prompt = BuildPrompt(node, context, graph, run, backend, stageDir);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);

        string responseText;
        IReadOnlyDictionary<string, object?> metadata = new Dictionary<string, object?>();
        if (backend is not null)
        {
            var result = await backend.RunAsync(node, prompt, context, cancellationToken).ConfigureAwait(false);
            metadata = result.Metadata;
            if (metadata.Count > 0)
            {
                await WriteJsonAsync(Path.Combine(stageDir, "backend.json"), ContextValues.ToJson(ContextValues.Normalize(metadata)), cancellationToken).ConfigureAwait(false);
            }

            if (result.Outcome is { } explicitOutcome)
            {
                if (result.Text is { Length: > 0 } outcomeText)
                {
                    await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), outcomeText, cancellationToken).ConfigureAwait(false);
                }

                await WriteStatusAsync(stageDir, explicitOutcome, cancellationToken).ConfigureAwait(false);
                return explicitOutcome;
            }

            responseText = result.Text ?? "";
        }
        else
        {
            responseText = "[Simulated] Response for stage: " + node.Id;
        }

        await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), responseText, cancellationToken).ConfigureAwait(false);

        var defaults = new Dictionary<string, object?>
        {
            ["last_stage"] = node.Id,
            ["last_response"] = Truncate(responseText, LastResponseLength),
        };

        // Status-file contract: an agent (or external tool) may have written status.json during this stage.
        var statusPath = Path.Combine(stageDir, "status.json");
        if (File.Exists(statusPath))
        {
            Outcome written;
            try
            {
                written = Outcome.FromStatusJson(JsonNode.Parse(await File.ReadAllTextAsync(statusPath, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                written = Outcome.Fail($"The agent wrote an invalid status.json: {ex.Message}", defaults);
                await WriteStatusAsync(stageDir, written, cancellationToken).ConfigureAwait(false);
                return written;
            }

            var merged = new Dictionary<string, object?>(defaults);
            foreach (var (key, value) in written.ContextUpdates)
            {
                merged[key] = value;
            }

            written = written with
            {
                ContextUpdates = merged,
                Notes = written.Notes.Length > 0 ? written.Notes : $"Stage completed: {node.Id} (status reported by agent)",
            };
            await WriteStatusAsync(stageDir, written, cancellationToken).ConfigureAwait(false);
            return written;
        }

        var outcome = Outcome.Success(
            node.AutoStatus ? "auto-status: handler completed without writing status" : "Stage completed: " + node.Id,
            defaults);
        await WriteStatusAsync(stageDir, outcome, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Builds the full prompt sent to the backend.</summary>
    public static string BuildPrompt(Node node, PipelineContext context, Graph graph, RunServices? run, ICodergenBackend? backend, string stageDir)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);

        var task = string.IsNullOrWhiteSpace(node.Prompt) ? node.Label : node.Prompt;
        task = VariableExpansionTransform.Expand(task, graph.Goal);

        var fidelity = context.GetString("internal.fidelity", FidelityModes.Default);
        var preamble = PreambleBuilder.Build(fidelity, graph, context, run?.History ?? [], run?.RunId ?? context.GetString("internal.run_id"));

        var sb = new StringBuilder();
        if (preamble.Length > 0)
        {
            sb.AppendLine(preamble).AppendLine().AppendLine("---").AppendLine();
        }

        sb.Append(task);

        if (backend is { SupportsStatusFile: true } && (run?.RunnerOptions.IncludeStatusProtocol ?? true))
        {
            sb.AppendLine().AppendLine().Append(StatusProtocol(node, graph, stageDir));
        }

        return sb.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>Instructions telling an agent how to report a non-default outcome via <c>status.json</c>.</summary>
    public static string StatusProtocol(Node node, Graph graph, string stageDir)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("## Reporting the stage outcome");
        sb.AppendLine("This task is one stage of an automated pipeline. If the stage did not succeed, or you want to choose");
        sb.AppendLine("the next step, write a JSON file before you finish:");
        sb.Append("  ").AppendLine(Path.Combine(Path.GetFullPath(stageDir), "status.json"));
        sb.AppendLine("Format: {\"outcome\": \"success\" | \"fail\" | \"retry\" | \"partial_success\", \"notes\": \"short summary\",");
        sb.AppendLine("         \"failure_reason\": \"why it failed\", \"preferred_label\": \"next step label\", \"context_updates\": {\"key\": \"value\"}}");
        var labels = graph.OutgoingEdges(node.Id).Select(e => e.Label).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (labels.Count > 0)
        {
            sb.Append("Next-step labels you may use as preferred_label: ").AppendLine(string.Join(", ", labels.Select(l => $"\"{l}\"")));
        }

        sb.Append("If you do not write the file, the stage is recorded as a success.");
        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static Task WriteStatusAsync(string stageDir, Outcome outcome, CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.Combine(stageDir, "status.json"), outcome.ToStatusJson(), cancellationToken);

    private static Task WriteJsonAsync(string path, JsonNode? json, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, json?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", cancellationToken);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

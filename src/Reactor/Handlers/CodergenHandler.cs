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
/// Declared artifact <c>outputs</c> are then checked and recorded (see <see cref="StageArtifacts.CompleteAsync"/>); in
/// simulation mode file outputs are recorded without being written.
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

                var completed = await StageArtifacts.CompleteAsync(node, explicitOutcome, context, result.Text, cancellationToken: cancellationToken).ConfigureAwait(false);
                await WriteStatusAsync(stageDir, completed, cancellationToken).ConfigureAwait(false);
                return completed;
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
            written = await StageArtifacts.CompleteAsync(node, written, context, responseText, simulated: backend is null, cancellationToken).ConfigureAwait(false);
            await WriteStatusAsync(stageDir, written, cancellationToken).ConfigureAwait(false);
            return written;
        }

        var outcome = Outcome.Success(
            node.AutoStatus ? "auto-status: handler completed without writing status" : "Stage completed: " + node.Id,
            defaults);
        outcome = await StageArtifacts.CompleteAsync(node, outcome, context, responseText, simulated: backend is null, cancellationToken).ConfigureAwait(false);
        await WriteStatusAsync(stageDir, outcome, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Builds the full prompt sent to the backend.</summary>
    public static string BuildPrompt(Node node, PipelineContext context, Graph graph, RunServices? run, ICodergenBackend? backend, string stageDir)
    {
        ArgumentNullException.ThrowIfNull(context);
        return BuildPrompt(
            node,
            context,
            graph,
            run?.History ?? [],
            run?.RunId ?? context.GetString("internal.run_id"),
            backend,
            stageDir,
            run?.RunnerOptions.IncludeStatusProtocol ?? true);
    }

    /// <summary>
    /// Builds the full prompt sent to the backend from explicit run state (used by editors to preview what a stage will see).
    /// The prompt is the preamble, the task, the stage's artifact <c>## Inputs</c>/<c>## Outputs</c> sections (inputs resolve
    /// from <c>artifacts.*</c> records in <paramref name="context"/>), and the status-file protocol.
    /// </summary>
    public static string BuildPrompt(
        Node node,
        PipelineContext context,
        Graph graph,
        IReadOnlyList<StageRecord> history,
        string runId,
        ICodergenBackend? backend,
        string stageDir,
        bool includeStatusProtocol)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(history);

        var task = string.IsNullOrWhiteSpace(node.Prompt) ? node.Label : node.Prompt;
        task = VariableExpansionTransform.Expand(task, graph.Goal);

        var fidelity = context.GetString("internal.fidelity", FidelityModes.Default);
        var preamble = PreambleBuilder.Build(fidelity, graph, context, history, runId);

        var sb = new StringBuilder();
        if (preamble.Length > 0)
        {
            sb.AppendLine(preamble).AppendLine().AppendLine("---").AppendLine();
        }

        sb.Append(task);

        // Reactor extension: declared artifact inputs and outputs (nothing is added for nodes without them).
        var artifacts = StageArtifacts.PromptSections(node, context);
        if (artifacts.Length > 0)
        {
            sb.AppendLine().AppendLine().Append(artifacts);
        }

        if (backend is not null && backend.SupportsStatusFileFor(node) && includeStatusProtocol)
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
        sb.AppendLine("## Reporting the stage outcome (optional)");
        sb.AppendLine("Do the task above first, using your tools to make the actual changes. This task is one stage of an automated");
        sb.AppendLine("pipeline, and success is recorded automatically when you finish. Only if the task could not be completed, or");
        sb.AppendLine("you need to choose the next step, write a small JSON status file as your final action:");
        sb.Append("  ").AppendLine(Path.Combine(Path.GetFullPath(stageDir), "status.json"));
        sb.AppendLine("Format: {\"outcome\": \"success\" | \"fail\" | \"retry\" | \"partial_success\", \"notes\": \"short summary\",");
        sb.AppendLine("         \"failure_reason\": \"why it failed\", \"preferred_label\": \"next step label\", \"context_updates\": {\"key\": \"value\"}}");
        var labels = graph.OutgoingEdges(node.Id).Select(e => e.Label).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (labels.Count > 0)
        {
            sb.Append("Next-step labels you may use as preferred_label: ").AppendLine(string.Join(", ", labels.Select(l => $"\"{l}\"")));
        }

        sb.Append("Writing this file does not complete the task, and the task's own output never goes in it or its directory.");
        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static Task WriteStatusAsync(string stageDir, Outcome outcome, CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.Combine(stageDir, "status.json"), outcome.ToStatusJson(), cancellationToken);

    private static Task WriteJsonAsync(string path, JsonNode? json, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, json?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", cancellationToken);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

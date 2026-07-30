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
/// simulation mode file outputs are recorded without being written. Reactor extensions: the prompt states the stage's time
/// limit, a retried stage gets a "Previous attempt" hand-off (see <see cref="PreviousAttempt"/>), and when the backend
/// throws, the usage it attached (<see cref="BackendMetadata"/>) is still written to <c>backend.json</c>.
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
            CodergenResult result;
            try
            {
                result = await backend.RunAsync(node, prompt, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failed, timed-out, or cancelled stage still records what it spent (and the agent's last words).
                await RecordFailedAttemptAsync(stageDir, ex, cancellationToken.IsCancellationRequested).ConfigureAwait(false);
                throw;
            }

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

        // Reactor extension: a retried stage is handed what its previous attempt did and how it ended.
        if (PreviousAttempt.From(context, node.Id) is { } previous)
        {
            sb.AppendLine().AppendLine().Append(previous.PromptSection());
        }

        // Reactor extension: agents can pace themselves when they know the stage's time limit.
        if (node.Timeout is { } limit && limit > TimeSpan.Zero)
        {
            sb.AppendLine().AppendLine().Append(StageTiming.PromptLine(limit));
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
        sb.AppendLine("pipeline, and success is recorded automatically when you finish. Only if the task could not be completed,");
        sb.AppendLine("you need to choose the next step, or the task asks you to report metrics, write a small JSON status file as");
        sb.AppendLine("your final action (write only this one file; there is no need to look at or list its directory):");
        sb.Append("  ").AppendLine(Path.Combine(Path.GetFullPath(stageDir), "status.json"));
        sb.AppendLine("Format: {\"outcome\": \"success\" | \"fail\" | \"retry\" | \"partial_success\", \"notes\": \"short summary\",");
        sb.AppendLine("         \"failure_reason\": \"why it failed\", \"preferred_label\": \"next step label\", \"context_updates\": {\"key\": \"value\"},");
        sb.AppendLine("         \"metrics\": {\"name\": 42}}   (metrics: numbers that measure the result, only when the task asks for them)");
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

    /// <summary>File name for the last assistant message of an attempt that ended without a response.</summary>
    public const string PartialResponseFileName = "response.partial.md";

    /// <summary>
    /// Writes <c>backend.json</c> for an attempt that threw: the metadata the backend attached (see <see cref="BackendMetadata"/>)
    /// plus <c>outcome</c> (<c>cancelled</c> when the stage's token was cancelled — the engine marks timeouts — else
    /// <c>error</c>) and <c>error</c>; the agent's last message, if any, goes to <see cref="PartialResponseFileName"/>.
    /// Best effort: never masks the original exception.
    /// </summary>
    public static async Task RecordFailedAttemptAsync(string stageDir, Exception error, bool cancelled)
    {
        try
        {
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (BackendMetadata.TryGet(error, out var attached))
            {
                foreach (var (key, value) in attached)
                {
                    record[key] = value;
                }
            }

            var lastText = record.GetValueOrDefault(BackendMetadata.LastAssistantText) as string;
            record.Remove(BackendMetadata.LastAssistantText);
            record["outcome"] = cancelled || error is OperationCanceledException ? "cancelled" : "error";
            record["error"] = error.GetBaseException().Message;
            record["ended_at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(stageDir);
            await WriteJsonAsync(Path.Combine(stageDir, "backend.json"), ContextValues.ToJson(ContextValues.Normalize(record)), CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(lastText))
            {
                await File.WriteAllTextAsync(Path.Combine(stageDir, PartialResponseFileName), lastText.Trim() + "\n", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recording is best effort; the stage's own failure is what matters.
        }
    }

    private static Task WriteJsonAsync(string path, JsonNode? json, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, json?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", cancellationToken);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

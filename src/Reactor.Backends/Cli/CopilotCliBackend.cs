using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Backends.Cli;

/// <summary>Options for <see cref="CopilotCliBackend"/>.</summary>
public sealed record CopilotCliOptions : CliAgentOptions
{
    /// <summary>Pass <c>--no-ask-user</c> so the agent works autonomously (default true).</summary>
    public bool NoAskUser { get; init; } = true;

    /// <summary>Grant access to every path and URL (<c>--allow-all</c>) instead of only <c>--allow-all-tools</c>.</summary>
    public bool AllowAll { get; init; }

    /// <summary>Optional AI-credit cap per stage (<c>--max-ai-credits</c>).</summary>
    public double? MaxAiCredits { get; init; }

    /// <summary>
    /// The Copilot CLI's data directory (default: <c>COPILOT_HOME</c> from <see cref="CliAgentOptions.Environment"/> or the
    /// process environment, else <c>~/.copilot</c>). A run that is killed before it reports usage is accounted from the
    /// session's usage records under <c>session-state/&lt;session id&gt;/events.jsonl</c> there.
    /// </summary>
    public string? CopilotHome { get; init; }
}

/// <summary>
/// Runs stages with the GitHub Copilot CLI (<c>copilot</c>) in non-interactive JSON mode:
/// <c>--output-format json --allow-all-tools --no-ask-user --session-id &lt;uuid&gt; [--model m] [--reasoning-effort e]</c>,
/// with the prompt on stdin. The executable can be set with <c>REACTOR_COPILOT_CLI</c>. The CLI has no option for
/// system-level instructions, so the stage's standing instructions open the prompt (once per session). Usage (AI units,
/// premium requests, tokens, model calls) comes from the CLI's cumulative usage checkpoints; a killed run is accounted
/// from the session's on-disk usage records, or estimated from the price table.
/// </summary>
public sealed class CopilotCliBackend(CopilotCliOptions? options = null)
    : CliAgentBackend(options ?? new CopilotCliOptions(), "copilot", "REACTOR_COPILOT_CLI")
{
    private CopilotCliOptions CopilotOptions => (CopilotCliOptions)Options;

    public override string Name => "copilot-cli";

    protected internal override IReadOnlyList<string> BuildArguments(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = new List<string> { "--output-format", "json", CopilotOptions.AllowAll ? "--allow-all" : "--allow-all-tools" };
        if (CopilotOptions.NoAskUser)
        {
            args.Add("--no-ask-user");
        }

        args.AddRange(["--session-id", invocation.SessionId]);
        if (invocation.Model is { } model)
        {
            args.AddRange(["--model", model]);
        }

        if (invocation.ReasoningEffort is { } effort)
        {
            args.AddRange(["--reasoning-effort", effort]);
        }

        if (invocation.Stage.StageDirectory is { } stageDir && !IsWithin(stageDir, invocation.Stage.WorkingDirectory))
        {
            args.AddRange(["--add-dir", stageDir]);
        }

        if (CopilotOptions.MaxAiCredits is { } credits)
        {
            args.AddRange(["--max-ai-credits", credits.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return args;
    }

    protected internal override ICliOutputParser CreateParser() => new CopilotOutputParser(Options.LogAssistantText);

    /// <summary>Copilot names Claude models with a dotted version (<c>claude-sonnet-4.5</c>); dated ids are shortened.</summary>
    protected internal override string? NormalizeModel(string? model, Action<string, string> log) => CopilotModelNames.Normalize(model);

    protected internal override string? MapReasoningEffort(string? effort) => CopilotModelNames.CliReasoningEffort(effort);

    protected override void AddInterruptedUsageMetadata(InterruptedRun run, CliThread thread, Dictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(metadata);

        // The CLI reports usage only at the end; a killed run's calls are in the session's on-disk usage records.
        if (CopilotSessionState.ReadUsage(SessionStateRoot(), run.SessionId, run.StartedAt) is { } usage)
        {
            foreach (var (field, value) in usage.Fields)
            {
                metadata[field] = value;

                // A resumed session's next checkpoint includes these calls; they are attributed here, not to the next stage.
                thread.Cumulative[field] = thread.Cumulative.GetValueOrDefault(field) + value;
            }

            metadata["usage_source"] = "session-state";
            return;
        }

        // Nothing reported: estimate the premium requests of the one prompt from the model's (legacy) multiplier.
        var model = metadata.GetValueOrDefault("model") as string;
        if (Reactor.Llm.PriceTable.Copilot.Find(model)?.LegacyPremiumMultiplier is { } multiplier)
        {
            metadata["premium_requests"] = multiplier;
            metadata["premium_requests_estimated"] = true;
        }
    }

    protected override void AddCostMetadata(Dictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        StageCost.AddCopilot(metadata, metadata.GetValueOrDefault("model") as string, premiumRequestsEstimated: metadata.GetValueOrDefault("premium_requests_estimated") is true);
    }

    private string SessionStateRoot()
    {
        var home = CopilotOptions.CopilotHome
                   ?? (Options.Environment?.TryGetValue("COPILOT_HOME", out var configured) == true ? configured : null)
                   ?? Environment.GetEnvironmentVariable("COPILOT_HOME");
        return Path.Combine(
            string.IsNullOrWhiteSpace(home) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot") : home,
            "session-state");
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}

/// <summary>Parses <c>copilot --output-format json</c> JSON lines.</summary>
internal sealed class CopilotOutputParser(bool logAssistantText) : ICliOutputParser
{
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

    public CliRunSummary Summary { get; } = new();

    public bool ShouldRecord(string line) =>
        !line.Contains("\"type\":\"assistant.message_delta\"", StringComparison.Ordinal) &&
        !line.Contains("\"type\":\"assistant.tool_call_delta\"", StringComparison.Ordinal) &&
        !line.Contains("\"type\":\"assistant.reasoning_delta\"", StringComparison.Ordinal);

    public void ProcessLine(string line, Action<string, string> log)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return;
        }

        JsonObject? evt;
        try
        {
            evt = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (evt is null)
        {
            return;
        }

        var data = evt["data"] as JsonObject;
        switch (Str(evt, "type"))
        {
            case "assistant.message":
                var content = Str(data, "content");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    Summary.Text = content.Trim();
                    Summary.LastAssistantText = Summary.Text;
                    if (logAssistantText)
                    {
                        log("assistant: " + LogText.Clip(content, 300), "debug");
                    }
                }

                Summary.Model = Str(data, "model") ?? Summary.Model;
                break;

            case "model.call_final_result":
                Summary.ModelCalls++;
                break;

            case "tool.execution_start":
                Summary.ToolCalls++;
                var name = Str(data, "toolName") ?? "tool";
                if (Str(data, "toolCallId") is { } id)
                {
                    _toolNames[id] = name;
                }

                log($"→ {name} {LogText.SummarizeArguments(data?["arguments"])}", "info");
                break;

            case "tool.execution_complete":
                if (data?["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok)
                {
                    Summary.ToolErrors++;
                    var toolName = Str(data, "toolCallId") is { } callId && _toolNames.TryGetValue(callId, out var n) ? n : "tool";
                    var error = Str(data["error"] as JsonObject, "message") ?? Str(data, "error") ?? Str(data["result"] as JsonObject, "content") ?? "failed";
                    log($"✗ {toolName}: {LogText.Clip(error, 300)}", "warn");
                    if (OutputLimit.IsTruncation(error))
                    {
                        Summary.OutputLimitTruncations++;
                        log(OutputLimit.Warning(toolName), "warn");
                    }
                }

                break;

            case "session.error":
                Summary.IsError = true;
                Summary.ErrorMessage = Str(data, "message") ?? Str(data, "error") ?? data?.ToJsonString() ?? "session error";
                log("copilot error: " + Summary.ErrorMessage, "error");
                break;

            case "session.warning":
                log("copilot warning: " + (Str(data, "message") ?? data?.ToJsonString()), "warn");
                break;

            case "session.usage_checkpoint":
                ReadCheckpoint(data);
                break;

            case "result":
                Summary.Completed = true;
                Summary.SessionId = Str(evt, "sessionId") ?? Summary.SessionId;
                if (evt["exitCode"] is JsonValue exit && TryNumber(exit, out var exitCode) && exitCode != 0)
                {
                    Summary.IsError = true;
                    Summary.ErrorMessage ??= $"copilot reported exit code {exitCode}";
                }

                if (evt["usage"] is JsonObject usage)
                {
                    if (usage["premiumRequests"] is JsonValue premium && TryNumber(premium, out var requests))
                    {
                        Summary.Metadata["premium_requests_cumulative"] = requests;
                        Summary.Cumulative["premium_requests"] = requests;
                    }

                    if (usage["totalApiDurationMs"] is JsonValue api && TryNumber(api, out var apiMs))
                    {
                        Summary.Metadata["api_duration_ms_cumulative"] = apiMs;
                    }

                    if (usage["codeChanges"] is JsonObject changes)
                    {
                        Summary.Metadata["lines_added_cumulative"] = changes["linesAdded"]?.DeepClone();
                        Summary.Metadata["lines_removed_cumulative"] = changes["linesRemoved"]?.DeepClone();
                        Summary.Metadata["files_modified_cumulative"] = (changes["filesModified"] as JsonArray)?.Count;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// A usage checkpoint carries the session's cumulative accounting: premium requests, AI units (nano AIU), and per-model
    /// request counts and token usage (in <c>accountingSnapshot.modelMetrics</c>).
    /// </summary>
    private void ReadCheckpoint(JsonObject? data)
    {
        if (data is null)
        {
            return;
        }

        if (data["totalPremiumRequests"] is JsonValue premium && TryNumber(premium, out var requests))
        {
            Summary.Metadata["premium_requests_cumulative"] = requests;
            Summary.Cumulative["premium_requests"] = requests;
        }

        if (data["totalNanoAiu"] is JsonValue nano && TryNumber(nano, out var nanoAiu))
        {
            Summary.Cumulative["ai_units"] = nanoAiu / 1e9;
        }

        if ((data["accountingSnapshot"] as JsonObject)?["modelMetrics"] is JsonObject models && models.Count > 0)
        {
            double calls = 0, input = 0, output = 0, cacheRead = 0, cacheWrite = 0, reasoning = 0;
            foreach (var (_, entry) in models)
            {
                if (entry is not JsonObject metrics)
                {
                    continue;
                }

                calls += Num((metrics["requests"] as JsonObject)?["count"]);
                if (metrics["usage"] is JsonObject usage)
                {
                    input += Num(usage["inputTokens"]);
                    output += Num(usage["outputTokens"]);
                    cacheRead += Num(usage["cacheReadTokens"]);
                    cacheWrite += Num(usage["cacheWriteTokens"]);
                    reasoning += Num(usage["reasoningTokens"]);
                }
            }

            Summary.Cumulative["model_calls"] = calls;
            Summary.Cumulative["input_tokens"] = input;
            Summary.Cumulative["output_tokens"] = output;
            Summary.Cumulative["cache_read_tokens"] = cacheRead;
            Summary.Cumulative["cache_write_tokens"] = cacheWrite;
            Summary.Cumulative["reasoning_tokens"] = reasoning;
        }
    }

    private static double Num(JsonNode? node) => node is JsonValue value && TryNumber(value, out var number) ? number : 0;

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool TryNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue<double>(out number))
        {
            return true;
        }

        if (value.TryGetValue<long>(out var l))
        {
            number = l;
            return true;
        }

        if (value.TryGetValue<int>(out var i))
        {
            number = i;
            return true;
        }

        return false;
    }
}

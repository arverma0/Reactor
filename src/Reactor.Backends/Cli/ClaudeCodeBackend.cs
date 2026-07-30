using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reactor.Backends.Cli;

/// <summary>Options for <see cref="ClaudeCodeBackend"/>.</summary>
public sealed record ClaudeCodeOptions : CliAgentOptions
{
    /// <summary>Pass <c>--dangerously-skip-permissions</c> (default true); otherwise <c>--permission-mode acceptEdits</c>.</summary>
    public bool SkipPermissions { get; init; } = true;

    /// <summary>Optional spending cap per stage (<c>--max-budget-usd</c>).</summary>
    public decimal? MaxBudgetUsd { get; init; }
}

/// <summary>
/// Runs stages with Claude Code (<c>claude</c>) in print mode with streaming JSON output:
/// <c>-p --output-format stream-json --verbose --dangerously-skip-permissions [--model m] [--effort e]</c> plus
/// <c>--session-id</c> for a new session or <c>--resume</c> to continue a thread, and the stage's standing instructions
/// with <c>--append-system-prompt</c>. The prompt goes on stdin. The executable can be set with <c>REACTOR_CLAUDE_CLI</c>.
/// </summary>
public sealed partial class ClaudeCodeBackend(ClaudeCodeOptions? options = null)
    : CliAgentBackend(options ?? new ClaudeCodeOptions(), "claude", "REACTOR_CLAUDE_CLI")
{
    private ClaudeCodeOptions ClaudeOptions => (ClaudeCodeOptions)Options;

    public override string Name => "claude-code";

    protected internal override IReadOnlyList<string> BuildArguments(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        if (ClaudeOptions.SkipPermissions)
        {
            args.Add("--dangerously-skip-permissions");
        }
        else
        {
            args.AddRange(["--permission-mode", "acceptEdits"]);
        }

        args.AddRange(invocation.Resume ? ["--resume", invocation.SessionId] : ["--session-id", invocation.SessionId]);
        if (invocation.Model is { } model)
        {
            args.AddRange(["--model", model]);
        }

        if (invocation.ReasoningEffort is { } effort)
        {
            args.AddRange(["--effort", effort]);
        }

        if (invocation.Stage.StageDirectory is { } stageDir)
        {
            args.AddRange(["--add-dir", stageDir]);
        }

        if (ClaudeOptions.MaxBudgetUsd is { } budget)
        {
            args.AddRange(["--max-budget-usd", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (invocation.Instructions.Length > 0)
        {
            // cmd.exe cannot carry line breaks inside an argument; a batch-file wrapper gets the instructions on one line.
            var instructions = invocation.ViaShell ? string.Join(" ", invocation.Instructions.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : invocation.Instructions;
            args.AddRange(["--append-system-prompt", instructions]);
        }

        return args;
    }

    /// <summary>Claude Code appends the stage's standing instructions to its system prompt (<c>--append-system-prompt</c>).</summary>
    protected internal override bool TakesInstructionsAsArgument(string instructions) =>
        instructions is not null && instructions.Length <= MaxInstructionArgumentLength;

    protected override void AddCostMetadata(Dictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Claude Code reports total_cost_usd when a run completes; a killed run is priced from the usage parsed so far.
        StageCost.AddApi(metadata, metadata.GetValueOrDefault("model") as string, inputIncludesCache: false);
    }

    protected internal override ICliOutputParser CreateParser() => new ClaudeOutputParser(Options.LogAssistantText);

    /// <summary>
    /// Claude Code accepts aliases (<c>sonnet</c>, <c>opus</c>, <c>haiku</c>) and Anthropic ids; dotted versions are converted
    /// (<c>claude-sonnet-4.5</c> → <c>claude-sonnet-4-5</c>). Non-Claude models fall back to the CLI default with a warning.
    /// </summary>
    protected internal override string? NormalizeModel(string? model, Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var lower = model.ToLowerInvariant();
        if (lower is "sonnet" or "opus" or "haiku" or "opusplan" or "default" || lower.StartsWith("claude-", StringComparison.Ordinal))
        {
            return DottedVersion().Replace(model, "${prefix}-${minor}");
        }

        log($"claude-code: model '{model}' is not a Claude model; using the CLI default", "warn");
        return null;
    }

    /// <summary>Claude Code supports low/medium/high.</summary>
    protected internal override string? MapReasoningEffort(string? effort) => effort?.ToLowerInvariant() switch
    {
        null => null,
        "none" or "minimal" or "low" => "low",
        "medium" => "medium",
        "high" or "xhigh" or "max" => "high",
        _ => null,
    };

    [GeneratedRegex(@"(?<prefix>claude-(?:opus|sonnet|haiku)-\d+)\.(?<minor>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DottedVersion();
}

/// <summary>Parses <c>claude -p --output-format stream-json --verbose</c> JSON lines.</summary>
internal sealed class ClaudeOutputParser(bool logAssistantText) : ICliOutputParser
{
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Input, long Output, long CacheRead, long CacheWrite)> _usageByMessage = new(StringComparer.Ordinal);

    public CliRunSummary Summary { get; } = new();

    public bool ShouldRecord(string line) => !line.Contains("\"type\":\"stream_event\"", StringComparison.Ordinal);

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

        switch (Str(evt, "type"))
        {
            case "system" when Str(evt, "subtype") == "init":
                Summary.SessionId = Str(evt, "session_id") ?? Summary.SessionId;
                Summary.Model = Str(evt, "model") ?? Summary.Model;
                break;

            case "assistant":
                var message = evt["message"] as JsonObject;
                if (Str(message, "model") is { } model && model != "<synthetic>")
                {
                    Summary.Model = model;
                }

                TrackUsage(message);
                foreach (var part in (message?["content"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    switch (Str(part, "type"))
                    {
                        case "text" when !string.IsNullOrWhiteSpace(Str(part, "text")):
                            var text = Str(part, "text")!;
                            Summary.LastAssistantText = text.Trim();
                            if (logAssistantText)
                            {
                                log("assistant: " + LogText.Clip(text, 300), "debug");
                            }

                            break;
                        case "tool_use":
                            Summary.ToolCalls++;
                            var name = Str(part, "name") ?? "tool";
                            if (Str(part, "id") is { } id)
                            {
                                _toolNames[id] = name;
                            }

                            log($"→ {name} {LogText.SummarizeArguments(part["input"])}", "info");
                            break;
                    }
                }

                break;

            case "user":
                foreach (var part in ((evt["message"] as JsonObject)?["content"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (Str(part, "type") == "tool_result" && part["is_error"] is JsonValue isError && isError.TryGetValue<bool>(out var failed) && failed)
                    {
                        Summary.ToolErrors++;
                        var toolName = Str(part, "tool_use_id") is { } useId && _toolNames.TryGetValue(useId, out var n) ? n : "tool";
                        var content = part["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : part["content"]?.ToJsonString();
                        log($"✗ {toolName}: {LogText.Clip(content, 300)}", "warn");
                        if (OutputLimit.IsTruncation(content))
                        {
                            Summary.OutputLimitTruncations++;
                            log(OutputLimit.Warning(toolName), "warn");
                        }
                    }
                }

                break;

            case "result":
                Summary.Completed = true;
                Summary.SessionId = Str(evt, "session_id") ?? Summary.SessionId;
                var resultText = Str(evt, "result") ?? "";
                var isErrorResult = (evt["is_error"] is JsonValue e && e.TryGetValue<bool>(out var err) && err) ||
                                    (Str(evt, "subtype") is { } subtype && subtype != "success");
                if (isErrorResult)
                {
                    Summary.IsError = true;
                    Summary.ErrorMessage = resultText.Length > 0 ? resultText : Str(evt, "subtype") ?? "error";
                }
                else
                {
                    Summary.Text = resultText.Trim();
                }

                Summary.Metadata["cost_usd"] = evt["total_cost_usd"]?.DeepClone();
                Summary.Metadata["num_turns"] = evt["num_turns"]?.DeepClone();
                Summary.Metadata["api_duration_ms"] = evt["duration_api_ms"]?.DeepClone();
                if (evt["usage"] is JsonObject usage)
                {
                    Summary.Metadata["input_tokens"] = usage["input_tokens"]?.DeepClone();
                    Summary.Metadata["output_tokens"] = usage["output_tokens"]?.DeepClone();
                    Summary.Metadata["cache_read_tokens"] = usage["cache_read_input_tokens"]?.DeepClone();
                    Summary.Metadata["cache_write_tokens"] = usage["cache_creation_input_tokens"]?.DeepClone();
                }

                break;
        }
    }

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Keeps running token totals from the per-message usage of assistant events (one model call per message id; the
    /// events of one message repeat its usage), so a run killed before its <c>result</c> still records what it used.
    /// The <c>result</c> event's totals replace them.
    /// </summary>
    private void TrackUsage(JsonObject? message)
    {
        if (Summary.Completed || message?["usage"] is not JsonObject usage)
        {
            return;
        }

        var id = Str(message, "id") ?? $"#{_usageByMessage.Count}";
        _usageByMessage[id] = (Long(usage["input_tokens"]), Long(usage["output_tokens"]), Long(usage["cache_read_input_tokens"]), Long(usage["cache_creation_input_tokens"]));
        Summary.ModelCalls = _usageByMessage.Count;
        Summary.Metadata["input_tokens"] = _usageByMessage.Values.Sum(u => u.Input);
        Summary.Metadata["output_tokens"] = _usageByMessage.Values.Sum(u => u.Output);
        Summary.Metadata["cache_read_tokens"] = _usageByMessage.Values.Sum(u => u.CacheRead);
        Summary.Metadata["cache_write_tokens"] = _usageByMessage.Values.Sum(u => u.CacheWrite);
    }

    private static long Long(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<long>(out var whole))
        {
            return whole;
        }

        return value.TryGetValue<double>(out var number) && double.IsFinite(number) ? (long)number : 0;
    }
}

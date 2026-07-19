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
/// <c>--session-id</c> for a new session or <c>--resume</c> to continue a thread. The prompt goes on stdin. The executable
/// can be set with <c>REACTOR_CLAUDE_CLI</c>.
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

        return args;
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

                foreach (var part in (message?["content"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    switch (Str(part, "type"))
                    {
                        case "text" when !string.IsNullOrWhiteSpace(Str(part, "text")):
                            var text = Str(part, "text")!;
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
}

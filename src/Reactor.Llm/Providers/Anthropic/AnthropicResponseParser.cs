using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Anthropic;

/// <summary>
/// Parses an Anthropic Messages API message object. Used for non-streaming responses and for the message accumulated
/// from a stream, so both paths build identical results.
/// </summary>
internal static class AnthropicResponseParser
{
    public static Response Parse(
        JsonObject root,
        Request request,
        string provider,
        IReadOnlyList<Warning> warnings,
        RateLimitInfo? rateLimit)
    {
        var parts = new List<ContentPart>();
        var hasThinking = false;
        var thinkingChars = 0L;
        var hasToolCalls = false;

        foreach (var block in AdapterSupport.Objects(root.Arr("content")))
        {
            switch (block.Str("type"))
            {
                case "text":
                    parts.Add(ContentPart.TextPart(block.Str("text") ?? ""));
                    break;
                case "tool_use":
                    parts.Add(ContentPart.ToolCallPart(block.Str("id") ?? "", block.Str("name") ?? "", ToolInput(block["input"])));
                    hasToolCalls = true;
                    break;
                case "thinking":
                    var thinking = block.Str("thinking") ?? "";
                    hasThinking = true;
                    thinkingChars += thinking.Length;
                    parts.Add(ContentPart.ThinkingPart(thinking, block.Str("signature")));
                    break;
                case "redacted_thinking":
                    parts.Add(ContentPart.RedactedThinkingPart(block.Str("data") ?? ""));
                    break;
            }
        }

        var usage = root.Obj("usage");
        var input = usage.Int("input_tokens") ?? 0;
        var output = usage.Int("output_tokens") ?? 0;
        return new Response
        {
            Id = root.Str("id") ?? "",
            Model = root.Str("model") ?? request.Model,
            Provider = provider,
            Message = new Message { Role = Role.Assistant, Content = parts },
            FinishReason = MapStopReason(root.Str("stop_reason"), hasToolCalls),
            Usage = new Usage
            {
                InputTokens = input,
                OutputTokens = output,
                TotalTokens = input + output,
                // Anthropic reports no separate reasoning count; estimate ~4 characters per token of visible thinking.
                ReasoningTokens = hasThinking ? (int)Math.Min(int.MaxValue, (thinkingChars + 3) / 4) : null,
                CacheReadTokens = usage.Int("cache_read_input_tokens"),
                CacheWriteTokens = usage.Int("cache_creation_input_tokens"),
                Raw = usage?.DeepClone(),
            },
            Raw = root,
            Warnings = warnings,
            RateLimit = rateLimit,
        };
    }

    internal static FinishReason MapStopReason(string? stopReason, bool hasToolCalls) => stopReason switch
    {
        "end_turn" or "stop_sequence" => new FinishReason(FinishReason.StopReason, stopReason),
        "max_tokens" or "model_context_window_exceeded" => new FinishReason(FinishReason.LengthReason, stopReason),
        "tool_use" => new FinishReason(FinishReason.ToolCallsReason, stopReason),
        "refusal" => new FinishReason(FinishReason.ContentFilterReason, stopReason),
        null when hasToolCalls => new FinishReason(FinishReason.ToolCallsReason),
        _ => new FinishReason(FinishReason.OtherReason, stopReason),
    };

    /// <summary>Tool input as returned (an object), or the raw string accumulated from a stream when it is not valid JSON.</summary>
    private static JsonNode ToolInput(JsonNode? input) => input switch
    {
        JsonObject obj => obj.DeepClone(),
        JsonValue value when value.TryGetValue<string>(out var raw) => AdapterSupport.ParseToolArguments(raw),
        null => new JsonObject(),
        _ => JsonValue.Create(input.ToJsonString()),
    };
}

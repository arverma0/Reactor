using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.OpenAICompatible;

/// <summary>
/// Parses a Chat Completions response. Used for non-streaming responses and for the completion accumulated from a
/// stream, so both paths build identical results.
/// </summary>
internal static class ChatCompletionsResponseParser
{
    public static Response Parse(
        JsonObject root,
        Request request,
        string provider,
        IReadOnlyList<Warning> warnings,
        RateLimitInfo? rateLimit)
    {
        var choice = AdapterSupport.Objects(root.Arr("choices")).FirstOrDefault();
        var message = choice.Obj("message");
        var parts = new List<ContentPart>();

        // Reasoning precedes the answer; servers use reasoning_content (vLLM, DeepSeek) or reasoning (Ollama, OpenRouter).
        var reasoning = message.Str("reasoning_content") ?? message.Str("reasoning");
        if (!string.IsNullOrEmpty(reasoning))
        {
            parts.Add(ContentPart.ThinkingPart(reasoning));
        }

        if (ContentText(message?["content"]) is { Length: > 0 } text)
        {
            parts.Add(ContentPart.TextPart(text));
        }

        var refusal = message.Str("refusal");
        if (!string.IsNullOrEmpty(refusal))
        {
            parts.Add(ContentPart.TextPart(refusal));
        }

        foreach (var call in AdapterSupport.Objects(message.Arr("tool_calls")))
        {
            var function = call.Obj("function");
            var arguments = function?["arguments"] switch
            {
                JsonObject obj => obj.DeepClone(),
                JsonValue value when value.TryGetValue<string>(out var raw) => AdapterSupport.ParseToolArguments(raw),
                _ => new JsonObject(),
            };
            parts.Add(ContentPart.ToolCallPart(
                call.Str("id") is { Length: > 0 } id ? id : AdapterSupport.NewToolCallId(),
                function.Str("name") ?? "",
                arguments));
        }

        var hasToolCalls = parts.Any(p => p.Kind == ContentKind.ToolCall);
        return new Response
        {
            Id = root.Str("id") ?? "",
            Model = root.Str("model") ?? request.Model,
            Provider = provider,
            Message = new Message { Role = Role.Assistant, Content = parts },
            FinishReason = MapFinishReason(choice.Str("finish_reason"), hasToolCalls, !string.IsNullOrEmpty(refusal)),
            Usage = ParseUsage(root.Obj("usage")),
            Raw = root,
            Warnings = warnings,
            RateLimit = rateLimit,
        };
    }

    internal static FinishReason MapFinishReason(string? finishReason, bool hasToolCalls, bool refused)
    {
        if (refused && finishReason is null or "stop")
        {
            return new FinishReason(FinishReason.ContentFilterReason, finishReason);
        }

        return finishReason switch
        {
            // Some servers report "stop" even when the message carries tool calls.
            "stop" or "eos" or "end_turn" or "stop_sequence" or null when hasToolCalls => new FinishReason(FinishReason.ToolCallsReason, finishReason),
            "stop" or "eos" or "end_turn" or "stop_sequence" => new FinishReason(FinishReason.StopReason, finishReason),
            "length" or "max_tokens" or "model_length" => new FinishReason(FinishReason.LengthReason, finishReason),
            "tool_calls" or "function_call" or "tool_use" => new FinishReason(FinishReason.ToolCallsReason, finishReason),
            "content_filter" => new FinishReason(FinishReason.ContentFilterReason, finishReason),
            "error" => new FinishReason(FinishReason.ErrorReason, finishReason),
            _ => new FinishReason(FinishReason.OtherReason, finishReason),
        };
    }

    internal static Usage ParseUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return Usage.Empty;
        }

        var input = usage.Int("prompt_tokens") ?? 0;
        var output = usage.Int("completion_tokens") ?? 0;
        return new Usage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = usage.Int("total_tokens") ?? input + output,
            ReasoningTokens = usage.Obj("completion_tokens_details").Int("reasoning_tokens"),
            CacheReadTokens = usage.Obj("prompt_tokens_details").Int("cached_tokens") ?? usage.Int("prompt_cache_hit_tokens"),
            Raw = usage.DeepClone(),
        };
    }

    /// <summary>Message content as text: a string, or the text parts of a content array.</summary>
    private static string? ContentText(JsonNode? content)
    {
        switch (content)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;
            case JsonArray array:
                var sb = new StringBuilder();
                foreach (var part in AdapterSupport.Objects(array))
                {
                    if (part.Str("type") is null or "text" or "output_text")
                    {
                        sb.Append(part.Str("text"));
                    }
                }

                return sb.ToString();
            default:
                return null;
        }
    }
}

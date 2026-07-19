using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.OpenAI;

/// <summary>
/// Parses an OpenAI Responses API response object. Used for non-streaming responses and for the response embedded in
/// the terminal <c>response.completed</c>/<c>response.incomplete</c> stream event, so both paths build identical results.
/// </summary>
internal static class OpenAIResponseParser
{
    /// <summary>Separator used to join multiple reasoning summary parts into one THINKING text.</summary>
    internal const string SummarySeparator = "\n\n";

    public static Response Parse(
        JsonObject root,
        Request request,
        string provider,
        IReadOnlyList<Warning> requestWarnings,
        RateLimitInfo? rateLimit)
    {
        var warnings = new List<Warning>(requestWarnings);
        var parts = new List<ContentPart>();
        var hasToolCalls = false;
        var refused = false;

        foreach (var item in AdapterSupport.Objects(root.Arr("output")))
        {
            switch (item.Str("type"))
            {
                case "message":
                    foreach (var content in AdapterSupport.Objects(item.Arr("content")))
                    {
                        switch (content.Str("type"))
                        {
                            case "output_text":
                                parts.Add(ContentPart.TextPart(content.Str("text") ?? ""));
                                break;
                            case "refusal":
                                parts.Add(ContentPart.TextPart(content.Str("refusal") ?? ""));
                                refused = true;
                                break;
                        }
                    }

                    break;
                case "function_call":
                    parts.Add(ContentPart.ToolCallPart(
                        item.Str("call_id") ?? item.Str("id") ?? "",
                        item.Str("name") ?? "",
                        AdapterSupport.ParseToolArguments(item.Str("arguments"))));
                    hasToolCalls = true;
                    break;
                case "custom_tool_call":
                    parts.Add(ContentPart.ToolCallPart(
                        item.Str("call_id") ?? item.Str("id") ?? "",
                        item.Str("name") ?? "",
                        JsonValue.Create(item.Str("input") ?? ""),
                        type: "custom"));
                    hasToolCalls = true;
                    break;
                case "reasoning":
                    parts.Add(ReasoningPart(item));
                    break;
            }
        }

        var status = root.Str("status");
        if (status == "failed" && root.Obj("error") is { } error)
        {
            warnings.Add(new Warning(error.Str("message") ?? "The response failed.", error.Str("code")));
        }

        return new Response
        {
            Id = root.Str("id") ?? "",
            Model = root.Str("model") ?? request.Model,
            Provider = provider,
            Message = new Message { Role = Role.Assistant, Content = parts },
            FinishReason = MapFinishReason(status, root.Obj("incomplete_details").Str("reason"), hasToolCalls, refused),
            Usage = ParseUsage(root.Obj("usage")),
            Raw = root,
            Warnings = warnings,
            RateLimit = rateLimit,
        };
    }

    internal static FinishReason MapFinishReason(string? status, string? incompleteReason, bool hasToolCalls, bool refused) => status switch
    {
        "incomplete" => incompleteReason switch
        {
            "max_output_tokens" => new FinishReason(FinishReason.LengthReason, incompleteReason),
            "content_filter" => new FinishReason(FinishReason.ContentFilterReason, incompleteReason),
            _ => new FinishReason(FinishReason.OtherReason, incompleteReason ?? status),
        },
        "failed" => new FinishReason(FinishReason.ErrorReason, status),
        null or "completed" when refused => new FinishReason(FinishReason.ContentFilterReason, "refusal"),
        null or "completed" when hasToolCalls => new FinishReason(FinishReason.ToolCallsReason, status ?? "completed"),
        null or "completed" => new FinishReason(FinishReason.StopReason, status ?? "completed"),
        _ => new FinishReason(FinishReason.OtherReason, status),
    };

    internal static Usage ParseUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return Usage.Empty;
        }

        var input = usage.Int("input_tokens") ?? 0;
        var output = usage.Int("output_tokens") ?? 0;
        return new Usage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = usage.Int("total_tokens") ?? input + output,
            ReasoningTokens = usage.Obj("output_tokens_details").Int("reasoning_tokens"),
            CacheReadTokens = usage.Obj("input_tokens_details").Int("cached_tokens"),
            Raw = usage.DeepClone(),
        };
    }

    /// <summary>
    /// A reasoning item becomes a THINKING part whose text is the joined summary (often empty) and whose provider metadata
    /// carries the item id and encrypted content needed to replay it in a later stateless request.
    /// </summary>
    private static ContentPart ReasoningPart(JsonObject item)
    {
        var summaries = AdapterSupport.Objects(item.Arr("summary"))
            .Select(s => s.Str("text"))
            .Where(t => !string.IsNullOrEmpty(t));
        var metadata = new JsonObject();
        if (item.Str("encrypted_content") is { } encrypted)
        {
            metadata["encrypted_content"] = encrypted;
        }

        if (item.Str("id") is { } id)
        {
            metadata["item_id"] = id;
        }

        return new ContentPart
        {
            Kind = ContentKind.Thinking,
            Thinking = new ThinkingData { Text = string.Join(SummarySeparator, summaries) },
            ProviderMetadata = metadata.Count > 0 ? new JsonObject { [OpenAIRequestBuilder.ProviderName] = metadata } : null,
        };
    }
}

using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Gemini;

/// <summary>
/// Parses a Gemini <c>GenerateContentResponse</c>. Used for non-streaming responses and for the response accumulated from
/// a stream, so both paths build identical results.
/// </summary>
internal static class GeminiResponseParser
{
    public static Response Parse(
        JsonObject root,
        Request request,
        string provider,
        IReadOnlyList<Warning> requestWarnings,
        RateLimitInfo? rateLimit)
    {
        var warnings = new List<Warning>(requestWarnings);
        var candidate = AdapterSupport.Objects(root.Arr("candidates")).FirstOrDefault();
        var parts = new List<ContentPart>();
        var hasToolCalls = false;

        foreach (var part in AdapterSupport.Objects(candidate.Obj("content").Arr("parts")))
        {
            ContentPart? translated = null;
            if (part.Obj("functionCall") is { } call)
            {
                // Gemini usually assigns no call ids; synthesize one so results can be linked back (by name) later.
                var id = call.Str("id") is { Length: > 0 } providerId ? providerId : AdapterSupport.NewToolCallId();
                translated = ContentPart.ToolCallPart(id, call.Str("name") ?? "", call["args"] is JsonObject args ? args.DeepClone() : new JsonObject());
                hasToolCalls = true;
            }
            else if (part.Str("text") is { } text)
            {
                translated = part.Bool("thought") == true ? ContentPart.ThinkingPart(text) : ContentPart.TextPart(text);
            }
            else if (part.Obj("inlineData") is { } inline)
            {
                translated = InlineDataPart(inline, warnings);
            }
            else if (part.Obj("fileData") is { } file && file.Str("fileUri") is { } uri)
            {
                translated = MediaPart(file.Str("mimeType") ?? "application/octet-stream", url: uri, data: null);
            }

            if (translated is null)
            {
                continue;
            }

            if ((part.Str("thoughtSignature") ?? part.Str("thought_signature")) is { } signature)
            {
                translated = translated with
                {
                    ProviderMetadata = new JsonObject
                    {
                        [GeminiRequestBuilder.ProviderName] = new JsonObject { ["thoughtSignature"] = signature },
                    },
                };
            }

            parts.Add(translated);
        }

        var blockReason = root.Obj("promptFeedback").Str("blockReason");
        if (candidate is null && blockReason is not null)
        {
            warnings.Add(new Warning($"The prompt was blocked: {blockReason}.", "prompt_blocked"));
        }

        return new Response
        {
            Id = root.Str("responseId") ?? "",
            Model = root.Str("modelVersion") ?? request.Model,
            Provider = provider,
            Message = new Message { Role = Role.Assistant, Content = parts },
            FinishReason = MapFinishReason(candidate.Str("finishReason"), hasToolCalls, candidate is null ? blockReason : null),
            Usage = ParseUsage(root.Obj("usageMetadata")),
            Raw = root,
            Warnings = warnings,
            RateLimit = rateLimit,
        };
    }

    internal static FinishReason MapFinishReason(string? finishReason, bool hasToolCalls, string? blockReason)
    {
        if (finishReason is null && blockReason is not null)
        {
            return new FinishReason(FinishReason.ContentFilterReason, blockReason);
        }

        return finishReason switch
        {
            // Gemini has no dedicated tool-call reason; it is inferred from functionCall parts.
            "STOP" or null when hasToolCalls => new FinishReason(FinishReason.ToolCallsReason, finishReason),
            "STOP" => new FinishReason(FinishReason.StopReason, finishReason),
            "MAX_TOKENS" => new FinishReason(FinishReason.LengthReason, finishReason),
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" or
                "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION" => new FinishReason(FinishReason.ContentFilterReason, finishReason),
            "MALFORMED_FUNCTION_CALL" => new FinishReason(FinishReason.ErrorReason, finishReason),
            _ => new FinishReason(FinishReason.OtherReason, finishReason),
        };
    }

    internal static Usage ParseUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return Usage.Empty;
        }

        var input = usage.Int("promptTokenCount") ?? 0;
        var output = usage.Int("candidatesTokenCount") ?? 0;
        var reasoning = usage.Int("thoughtsTokenCount");
        return new Usage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = usage.Int("totalTokenCount") ?? input + output + (reasoning ?? 0),
            ReasoningTokens = reasoning,
            CacheReadTokens = usage.Int("cachedContentTokenCount"),
            Raw = usage.DeepClone(),
        };
    }

    private static ContentPart? InlineDataPart(JsonObject inline, List<Warning> warnings)
    {
        var mediaType = inline.Str("mimeType") ?? "application/octet-stream";
        try
        {
            return MediaPart(mediaType, url: null, data: Convert.FromBase64String(inline.Str("data") ?? ""));
        }
        catch (FormatException)
        {
            warnings.Add(new Warning($"Gemini returned invalid base64 data for a '{mediaType}' part; the part was dropped.", "invalid_content"));
            return null;
        }
    }

    private static ContentPart MediaPart(string mediaType, string? url, byte[]? data)
    {
        if (MediaTypes.IsImage(mediaType))
        {
            return ContentPart.ImagePart(new ImageData { Url = url, Data = data, MediaType = mediaType });
        }

        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return ContentPart.AudioPart(new AudioData { Url = url, Data = data, MediaType = mediaType });
        }

        return ContentPart.DocumentPart(new DocumentData { Url = url, Data = data, MediaType = mediaType });
    }
}

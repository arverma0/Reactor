using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Anthropic;

/// <summary>The translated Anthropic request: body, warnings, computed <c>anthropic-beta</c> values, and thinking state.</summary>
internal sealed record AnthropicPreparedRequest(JsonObject Body, List<Warning> Warnings, IReadOnlyList<string> BetaHeaders, bool ThinkingEnabled);

/// <summary>Translates a unified <see cref="Request"/> into an Anthropic Messages API (<c>POST /v1/messages</c>) body.</summary>
internal static class AnthropicRequestBuilder
{
    internal const string ProviderName = "anthropic";
    internal const string PromptCachingBeta = "prompt-caching-2024-07-31";
    internal const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    internal const int MaxCacheBreakpoints = 4;

    private static readonly HashSet<string> CacheableBlockTypes = new(StringComparer.Ordinal)
    {
        "text", "image", "tool_use", "tool_result", "document",
    };

    public static AnthropicPreparedRequest Build(Request request, AnthropicAdapterOptions options, bool stream)
    {
        var warnings = new List<Warning>();
        var providerOptions = request.GetProviderOptions(ProviderName);
        var choice = AdapterSupport.ResolveToolChoice(request, ProviderName);
        var hasTools = request.Tools is { Count: > 0 };

        var thinking = ResolveThinking(request, providerOptions, warnings);
        var thinkingEnabled = IsThinkingEnabled(thinking);
        if (thinkingEnabled && hasTools && choice?.Mode is ToolChoice.RequiredMode or ToolChoice.NamedMode)
        {
            warnings.Add(new Warning(
                $"Extended thinking is incompatible with tool_choice '{choice.Mode}'; thinking was disabled for this request.",
                "thinking_disabled"));
            thinking = null;
            thinkingEnabled = false;
        }

        var body = new JsonObject { ["model"] = request.Model };

        var maxTokens = request.MaxTokens ?? options.DefaultMaxTokens;
        if (thinkingEnabled && AdapterSupport.GetInt(thinking, "budget_tokens") is { } budget && maxTokens <= budget)
        {
            var raised = budget + options.DefaultMaxTokens;
            warnings.Add(new Warning(
                $"max_tokens ({maxTokens}) must exceed the thinking budget ({budget}); it was raised to {raised}.", "max_tokens_adjusted"));
            maxTokens = raised;
        }

        body["max_tokens"] = maxTokens;

        var system = BuildSystem(request, warnings);
        if (system.Count > 0)
        {
            body["system"] = system;
        }

        var messages = BuildMessages(request, thinkingEnabled, warnings, out var hasToolHistory);
        body["messages"] = messages;

        if (hasTools)
        {
            // Anthropic has no "none" mode when no tool blocks exist yet, so tools are omitted; once the conversation
            // contains tool_use/tool_result blocks the definitions are required, so they stay with {"type": "none"}.
            if (choice?.Mode != ToolChoice.NoneMode || hasToolHistory)
            {
                var tools = new JsonArray();
                foreach (var tool in request.Tools!)
                {
                    var definition = new JsonObject { ["name"] = tool.Name };
                    if (!string.IsNullOrEmpty(tool.Description))
                    {
                        definition["description"] = tool.Description;
                    }

                    definition["input_schema"] = tool.Parameters.DeepClone();
                    tools.Add(definition);
                }

                body["tools"] = tools;
                if (choice is not null)
                {
                    body["tool_choice"] = choice.Mode switch
                    {
                        ToolChoice.RequiredMode => new JsonObject { ["type"] = "any" },
                        ToolChoice.NamedMode => new JsonObject { ["type"] = "tool", ["name"] = choice.ToolName },
                        ToolChoice.NoneMode => new JsonObject { ["type"] = "none" },
                        _ => new JsonObject { ["type"] = "auto" },
                    };
                }
            }
        }
        else
        {
            AdapterSupport.WarnToolChoiceWithoutTools(choice, warnings);
        }

        if (thinking is not null)
        {
            body["thinking"] = thinking;
        }

        if (request.Temperature is { } temperature)
        {
            if (thinkingEnabled)
            {
                warnings.Add(new Warning("temperature is not supported with extended thinking and was ignored.", "unsupported_parameter"));
            }
            else
            {
                body["temperature"] = temperature;
            }
        }

        if (request.TopP is { } topP)
        {
            if (thinkingEnabled)
            {
                warnings.Add(new Warning("top_p is not supported with extended thinking and was ignored.", "unsupported_parameter"));
            }
            else
            {
                body["top_p"] = topP;
            }
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            var array = new JsonArray();
            foreach (var stop in stops)
            {
                array.Add(stop);
            }

            body["stop_sequences"] = array;
        }

        if (request.Metadata is { Count: > 0 } metadata)
        {
            if (metadata.TryGetValue("user_id", out var userId))
            {
                body["metadata"] = new JsonObject { ["user_id"] = userId };
            }

            var ignored = metadata.Keys.Where(k => k != "user_id").ToList();
            if (ignored.Count > 0)
            {
                warnings.Add(new Warning(
                    $"Anthropic supports only metadata.user_id; ignored keys: {string.Join(", ", ignored)}.", "unsupported_parameter"));
            }
        }

        if (stream)
        {
            body["stream"] = true;
        }

        if (AdapterSupport.CloneWithout(providerOptions, "beta_headers", "auto_cache", "thinking") is { } rest)
        {
            JsonHelpers.DeepMerge(body, rest);
        }

        if (options.AutoCache && providerOptions.Bool("auto_cache") != false)
        {
            ApplyAutoCache(body);
        }

        var betas = new List<string>();
        AddBetaValues(betas, providerOptions?["beta_headers"]);
        if (CountCacheControl(body) > 0)
        {
            AddBetaValues(betas, JsonValue.Create(PromptCachingBeta));
        }

        if (thinkingEnabled && body["tools"] is JsonArray { Count: > 0 })
        {
            AddBetaValues(betas, JsonValue.Create(InterleavedThinkingBeta));
        }

        return new AnthropicPreparedRequest(body, warnings, betas, thinkingEnabled);
    }

    /// <summary>Adds comma-separated or array beta values to <paramref name="betas"/>, de-duplicated, preserving order.</summary>
    internal static void AddBetaValues(List<string> betas, JsonNode? values)
    {
        IEnumerable<string> candidates = values switch
        {
            JsonArray array => array.Select(JsonHelpers.NodeToText),
            JsonValue => JsonHelpers.NodeToText(values).Split(','),
            _ => [],
        };

        foreach (var candidate in candidates)
        {
            var value = candidate.Trim();
            if (value.Length > 0 && !betas.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                betas.Add(value);
            }
        }
    }

    /// <summary>Counts <c>cache_control</c> markers on tools, system blocks, and message blocks (including nested tool_result content).</summary>
    internal static int CountCacheControl(JsonObject body)
    {
        var count = 0;
        foreach (var tool in AdapterSupport.Objects(body.Arr("tools")))
        {
            count += tool.ContainsKey("cache_control") ? 1 : 0;
        }

        foreach (var block in AdapterSupport.Objects(body.Arr("system")))
        {
            count += block.ContainsKey("cache_control") ? 1 : 0;
        }

        foreach (var message in AdapterSupport.Objects(body.Arr("messages")))
        {
            foreach (var block in AdapterSupport.Objects(message.Arr("content")))
            {
                count += block.ContainsKey("cache_control") ? 1 : 0;
                foreach (var nested in AdapterSupport.Objects(block.Arr("content")))
                {
                    count += nested.ContainsKey("cache_control") ? 1 : 0;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Automatic prompt-caching heuristic: marks the last tool definition, the last system block, and the last cacheable
    /// block of the final message with <c>{"type": "ephemeral"}</c>, never exceeding the provider's four breakpoints
    /// (explicit breakpoints are counted first).
    /// </summary>
    internal static void ApplyAutoCache(JsonObject body)
    {
        var remaining = MaxCacheBreakpoints - CountCacheControl(body);
        JsonObject?[] candidates =
        [
            AdapterSupport.Objects(body.Arr("tools")).LastOrDefault(),
            AdapterSupport.Objects(body.Arr("system")).LastOrDefault(),
            LastCacheableBlock(body.Arr("messages")),
        ];

        foreach (var candidate in candidates)
        {
            if (remaining <= 0)
            {
                break;
            }

            if (candidate is null || candidate.ContainsKey("cache_control"))
            {
                continue;
            }

            candidate["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            remaining--;
        }
    }

    private static JsonObject? LastCacheableBlock(JsonArray? messages)
    {
        if (messages is not { Count: > 0 } || messages[^1].Arr("content") is not { } content)
        {
            return null;
        }

        for (var i = content.Count - 1; i >= 0; i--)
        {
            if (content[i] is JsonObject block && block.Str("type") is { } type && CacheableBlockTypes.Contains(type) &&
                !(type == "text" && string.IsNullOrEmpty(block.Str("text"))))
            {
                return block;
            }
        }

        return null;
    }

    private static JsonObject? ResolveThinking(Request request, JsonObject? providerOptions, List<Warning> warnings)
    {
        if (providerOptions?["thinking"] is JsonObject explicitThinking)
        {
            return (JsonObject)explicitThinking.DeepClone();
        }

        if (string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            return null;
        }

        int? budget = request.ReasoningEffort.Trim().ToLowerInvariant() switch
        {
            "minimal" => 1024,
            "low" => 2048,
            "medium" => 8192,
            "high" => 16384,
            "xhigh" or "max" => 32768,
            "none" or "off" or "disabled" => null,
            _ => -1,
        };

        if (budget == -1)
        {
            warnings.Add(new Warning(
                $"Unsupported reasoning_effort '{request.ReasoningEffort}' was ignored.", "unsupported_parameter"));
            return null;
        }

        return budget is null ? null : new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget.Value };
    }

    private static bool IsThinkingEnabled(JsonObject? thinking) =>
        thinking is not null && !string.Equals(thinking.Str("type"), "disabled", StringComparison.OrdinalIgnoreCase);

    private static JsonArray BuildSystem(Request request, List<Warning> warnings)
    {
        var system = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role is not (Role.System or Role.Developer))
            {
                continue;
            }

            foreach (var part in message.Content)
            {
                if (part.Kind != ContentKind.Text)
                {
                    AdapterSupport.WarnDroppedPart(warnings, ProviderName, message.Role, part.Kind);
                }
                else if (!string.IsNullOrEmpty(part.Text))
                {
                    system.Add(WithExplicitCache(new JsonObject { ["type"] = "text", ["text"] = part.Text }, part));
                }
            }
        }

        if (StructuredOutputInstruction(request.ResponseFormat, warnings) is { } instruction)
        {
            system.Add(new JsonObject { ["type"] = "text", ["text"] = instruction });
        }

        return system;
    }

    /// <summary>
    /// The Messages API has no native JSON mode, so JSON response formats are requested through a system instruction
    /// (spec 7.8: prompt engineering).
    /// </summary>
    private static string? StructuredOutputInstruction(ResponseFormat? format, List<Warning> warnings)
    {
        switch (format?.Type)
        {
            case null:
            case ResponseFormat.TextType:
                return null;
            case ResponseFormat.JsonType:
                return "Respond with a single valid JSON value only, with no surrounding text or code fences.";
            case ResponseFormat.JsonSchemaType when format.JsonSchema is not null:
                return "Respond with a single valid JSON value only, with no surrounding text or code fences. " +
                       "The JSON must conform to this JSON Schema:\n" + format.JsonSchema.ToJsonString();
            case ResponseFormat.JsonSchemaType:
                warnings.Add(new Warning("response_format json_schema has no schema; JSON mode was used instead.", "invalid_response_format"));
                return "Respond with a single valid JSON value only, with no surrounding text or code fences.";
            default:
                warnings.Add(new Warning($"Unsupported response_format type '{format.Type}' was ignored.", "unsupported_response_format"));
                return null;
        }
    }

    private static JsonArray BuildMessages(Request request, bool thinkingEnabled, List<Warning> warnings, out bool hasToolHistory)
    {
        var turns = new List<(string Role, List<JsonObject> Blocks)>();
        foreach (var message in request.Messages)
        {
            List<JsonObject> blocks;
            string role;
            switch (message.Role)
            {
                case Role.User:
                    role = "user";
                    blocks = UserBlocks(message, warnings);
                    break;
                case Role.Assistant:
                    role = "assistant";
                    blocks = AssistantBlocks(message, thinkingEnabled, warnings);
                    break;
                case Role.Tool:
                    role = "user";
                    blocks = ToolBlocks(message, warnings);
                    break;
                default:
                    continue;
            }

            if (blocks.Count == 0)
            {
                continue;
            }

            // Strict alternation: consecutive same-role messages are merged.
            if (turns.Count > 0 && turns[^1].Role == role)
            {
                turns[^1].Blocks.AddRange(blocks);
            }
            else
            {
                turns.Add((role, blocks));
            }
        }

        if (turns.Count > 0 && turns[0].Role != "user")
        {
            turns.Insert(0, ("user", [new JsonObject { ["type"] = "text", ["text"] = "Continue." }]));
        }

        hasToolHistory = turns.Any(t => t.Blocks.Any(b => b.Str("type") is "tool_use" or "tool_result"));
        var messages = new JsonArray();
        foreach (var (role, blocks) in turns)
        {
            // In user turns, tool_result blocks must precede any other content.
            var ordered = role == "user"
                ? blocks.Where(b => b.Str("type") == "tool_result").Concat(blocks.Where(b => b.Str("type") != "tool_result"))
                : blocks;
            var content = new JsonArray();
            foreach (var block in ordered)
            {
                content.Add(block);
            }

            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        return messages;
    }

    private static List<JsonObject> UserBlocks(Message message, List<Warning> warnings)
    {
        var blocks = new List<JsonObject>();
        foreach (var part in message.Content)
        {
            JsonObject? block = null;
            if (part.Kind == ContentKind.Text)
            {
                block = string.IsNullOrEmpty(part.Text) ? null : new JsonObject { ["type"] = "text", ["text"] = part.Text };
            }
            else if (part.Kind == ContentKind.Image && part.Image is not null)
            {
                block = ImageBlock(AdapterSupport.ResolveImage(part.Image));
            }
            else if (part.Kind == ContentKind.Document && part.Document is not null)
            {
                block = DocumentBlock(part.Document, warnings);
            }
            else if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
            {
                block = ToolResultBlock(part.ToolResult);
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, ProviderName, message.Role, part.Kind);
            }

            if (block is not null)
            {
                blocks.Add(WithExplicitCache(block, part));
            }
        }

        return blocks;
    }

    private static List<JsonObject> AssistantBlocks(Message message, bool thinkingEnabled, List<Warning> warnings)
    {
        var blocks = new List<JsonObject>();
        foreach (var part in message.Content)
        {
            JsonObject? block = null;
            if (part.Kind == ContentKind.Text)
            {
                block = string.IsNullOrEmpty(part.Text) ? null : new JsonObject { ["type"] = "text", ["text"] = part.Text };
            }
            else if (part.Kind == ContentKind.ToolCall && part.ToolCall is { } call)
            {
                block = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["input"] = call.ArgumentsObject,
                };
            }
            else if (part.Kind == ContentKind.RedactedThinking || part is { Kind.Value: "thinking", Thinking.Redacted: true })
            {
                // Opaque redacted reasoning is only valid while thinking is enabled for the request.
                if (thinkingEnabled && part.Thinking is { Text.Length: > 0 } redacted)
                {
                    block = new JsonObject { ["type"] = "redacted_thinking", ["data"] = redacted.Text };
                }
            }
            else if (part.Kind == ContentKind.Thinking && part.Thinking is { } thinking)
            {
                // Thinking blocks round-trip only with their signature (other providers' reasoning has none) and only
                // when thinking is enabled for this request.
                if (thinkingEnabled && !string.IsNullOrEmpty(thinking.Signature))
                {
                    block = new JsonObject { ["type"] = "thinking", ["thinking"] = thinking.Text, ["signature"] = thinking.Signature };
                }
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, ProviderName, message.Role, part.Kind);
            }

            if (block is not null)
            {
                blocks.Add(block["type"]!.GetValue<string>() is "thinking" or "redacted_thinking" ? block : WithExplicitCache(block, part));
            }
        }

        return blocks;
    }

    private static List<JsonObject> ToolBlocks(Message message, List<Warning> warnings)
    {
        var results = AdapterSupport.ToolResults(message);
        if (results.Count == 0)
        {
            warnings.Add(new Warning("A tool message without a tool result or tool_call_id was dropped.", "invalid_tool_message"));
        }

        var blocks = new List<JsonObject>();
        foreach (var result in results)
        {
            var part = message.Content.FirstOrDefault(p => ReferenceEquals(p.ToolResult, result));
            blocks.Add(part is null ? ToolResultBlock(result) : WithExplicitCache(ToolResultBlock(result), part));
        }

        return blocks;
    }

    private static JsonObject ToolResultBlock(ToolResultData result)
    {
        var text = result.ContentText;
        var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.ToolCallId };
        if (result.ImageData is { Length: > 0 } image)
        {
            var content = new JsonArray();
            if (text.Length > 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }

            content.Add(ImageBlock(new ResolvedMedia(result.ImageMediaType ?? "image/png", Convert.ToBase64String(image), null)));
            block["content"] = content;
        }
        else
        {
            // The API rejects empty content on error results.
            block["content"] = text.Length == 0 && result.IsError ? "The tool call failed." : text;
        }

        block["is_error"] = result.IsError;
        return block;
    }

    private static JsonObject ImageBlock(ResolvedMedia media) => new()
    {
        ["type"] = "image",
        ["source"] = media.IsInline
            ? new JsonObject { ["type"] = "base64", ["media_type"] = media.MediaType, ["data"] = media.Base64 }
            : new JsonObject { ["type"] = "url", ["url"] = media.Url },
    };

    private static JsonObject? DocumentBlock(DocumentData document, List<Warning> warnings)
    {
        var media = AdapterSupport.ResolveDocument(document);
        JsonObject source;
        if (!media.IsInline)
        {
            source = new JsonObject { ["type"] = "url", ["url"] = media.Url };
        }
        else if (string.Equals(media.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            source = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = media.Base64 };
        }
        else if (media.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(media.Base64!));
            source = new JsonObject { ["type"] = "text", ["media_type"] = "text/plain", ["data"] = text };
        }
        else
        {
            warnings.Add(new Warning(
                $"Anthropic does not support '{media.MediaType}' documents; the document was dropped.", "unsupported_content"));
            return null;
        }

        var block = new JsonObject { ["type"] = "document", ["source"] = source };
        if (!string.IsNullOrWhiteSpace(document.FileName))
        {
            block["title"] = document.FileName;
        }

        return block;
    }

    /// <summary>Applies an explicit breakpoint from <c>ProviderMetadata.anthropic.cache_control</c>.</summary>
    private static JsonObject WithExplicitCache(JsonObject block, ContentPart part)
    {
        if (part.GetProviderMetadata(ProviderName)?["cache_control"] is JsonObject cacheControl)
        {
            block["cache_control"] = cacheControl.DeepClone();
        }

        return block;
    }
}

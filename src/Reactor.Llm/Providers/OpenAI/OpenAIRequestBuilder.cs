using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.OpenAI;

/// <summary>Translates a unified <see cref="Request"/> into an OpenAI Responses API (<c>POST /v1/responses</c>) body.</summary>
internal static partial class OpenAIRequestBuilder
{
    internal const string ProviderName = "openai";

    /// <summary>Builds the request body. Non-fatal translation issues are appended to <paramref name="warnings"/>.</summary>
    public static JsonObject Build(Request request, bool stream, List<Warning> warnings)
    {
        var instructions = new List<string>();
        var input = new JsonArray();
        var customCallIds = new HashSet<string>(StringComparer.Ordinal);
        var pendingToolImages = new List<(string CallId, JsonObject Image)>();

        foreach (var message in request.Messages)
        {
            if (message.Role != Role.Tool)
            {
                FlushToolImages(input, pendingToolImages);
            }

            switch (message.Role)
            {
                case Role.System:
                case Role.Developer:
                    if (AdapterSupport.InstructionText(message, ProviderName, warnings) is { } text)
                    {
                        instructions.Add(text);
                    }

                    break;
                case Role.User:
                    AddUserMessage(message, input, customCallIds, warnings);
                    break;
                case Role.Assistant:
                    AddAssistantMessage(message, input, customCallIds, warnings);
                    break;
                case Role.Tool:
                    AddToolMessage(message, input, customCallIds, pendingToolImages, warnings);
                    break;
            }
        }

        FlushToolImages(input, pendingToolImages);

        var providerOptions = request.GetProviderOptions(ProviderName);
        var body = new JsonObject { ["model"] = request.Model };
        if (instructions.Count > 0)
        {
            body["instructions"] = string.Join("\n\n", instructions);
        }

        body["input"] = input;

        var tools = new JsonArray();
        foreach (var tool in request.Tools ?? [])
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone(),
                // The Responses API defaults function tools to strict mode; send non-strict so arbitrary JSON Schemas
                // (optional properties, open objects) behave as on every other provider.
                ["strict"] = false,
            });
        }

        var extraTools = providerOptions?["tools"] as JsonArray;
        var choice = AdapterSupport.ResolveToolChoice(request, ProviderName);
        if (tools.Count > 0 || extraTools is { Count: > 0 })
        {
            if (tools.Count > 0)
            {
                body["tools"] = tools;
            }

            if (extraTools is not null)
            {
                AdapterSupport.AppendToArray(body, "tools", extraTools);
            }

            if (choice is not null)
            {
                body["tool_choice"] = choice.Mode == ToolChoice.NamedMode
                    ? new JsonObject { ["type"] = "function", ["name"] = choice.ToolName }
                    : choice.Mode;
            }
        }
        else
        {
            AdapterSupport.WarnToolChoiceWithoutTools(choice, warnings);
        }

        if (TranslateResponseFormat(request.ResponseFormat, warnings) is { } format)
        {
            body["text"] = new JsonObject { ["format"] = format };
        }

        var reasoningModel = IsReasoningModel(request.Model);
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            if (ModelCatalog.GetModelInfo(request.Model) is { SupportsReasoning: false })
            {
                warnings.Add(new Warning(
                    $"Model '{request.Model}' does not support reasoning; reasoning_effort was ignored.", "unsupported_parameter"));
            }
            else
            {
                body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort };
            }
        }

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_output_tokens"] = maxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (request.StopSequences is { Count: > 0 })
        {
            warnings.Add(new Warning(
                "The OpenAI Responses API does not support stop sequences; stop_sequences was ignored.", "unsupported_parameter"));
        }

        if (request.Metadata is { Count: > 0 } metadata)
        {
            var md = new JsonObject();
            foreach (var (key, value) in metadata)
            {
                md[key] = value;
            }

            body["metadata"] = md;
        }

        if (reasoningModel)
        {
            // Stateless reasoning continuity (as Codex does): nothing is persisted server-side, and the encrypted
            // reasoning items are returned so they can be replayed in the next request's input.
            body["store"] = false;
            body["include"] = new JsonArray("reasoning.encrypted_content");
        }

        if (stream)
        {
            body["stream"] = true;
        }

        ApplyProviderOptions(body, providerOptions);
        return body;
    }

    /// <summary>
    /// True for models that produce reasoning items: catalog entries flagged as reasoning, GPT-5+ and o-series ids, and
    /// Codex models. Non-reasoning <c>-chat</c> variants (e.g. <c>gpt-5-chat-latest</c>) are excluded from the heuristic
    /// because they reject <c>reasoning.encrypted_content</c>.
    /// </summary>
    internal static bool IsReasoningModel(string model)
    {
        if (ModelCatalog.GetModelInfo(model) is { SupportsReasoning: true })
        {
            return true;
        }

        var id = model.Trim().ToLowerInvariant();
        if (id.Contains("-chat", StringComparison.Ordinal))
        {
            return false;
        }

        return ReasoningModelPattern().IsMatch(id) || id.Contains("codex", StringComparison.Ordinal);
    }

    private static void ApplyProviderOptions(JsonObject body, JsonObject? providerOptions)
    {
        if (providerOptions is null)
        {
            return;
        }

        if (providerOptions["include"] is JsonArray include)
        {
            if (body["include"] is not JsonArray target)
            {
                target = new JsonArray();
                body["include"] = target;
            }

            var existing = target.Select(JsonHelpers.NodeToText).ToHashSet(StringComparer.Ordinal);
            foreach (var item in include)
            {
                if (item is not null && existing.Add(JsonHelpers.NodeToText(item)))
                {
                    target.Add(item.DeepClone());
                }
            }
        }

        if (AdapterSupport.CloneWithout(providerOptions, "tools", "include") is { } rest)
        {
            JsonHelpers.DeepMerge(body, rest);
        }
    }

    private static JsonObject? TranslateResponseFormat(ResponseFormat? format, List<Warning> warnings)
    {
        switch (format?.Type)
        {
            case null:
            case ResponseFormat.TextType:
                return null;
            case ResponseFormat.JsonType:
                return new JsonObject { ["type"] = "json_object" };
            case ResponseFormat.JsonSchemaType when format.JsonSchema is not null:
                return new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = format.SchemaName,
                    ["schema"] = format.JsonSchema.DeepClone(),
                    ["strict"] = format.Strict,
                };
            case ResponseFormat.JsonSchemaType:
                warnings.Add(new Warning("response_format json_schema has no schema; JSON mode was used instead.", "invalid_response_format"));
                return new JsonObject { ["type"] = "json_object" };
            default:
                warnings.Add(new Warning($"Unsupported response_format type '{format.Type}' was ignored.", "unsupported_response_format"));
                return null;
        }
    }

    private static void AddUserMessage(Message message, JsonArray input, HashSet<string> customCallIds, List<Warning> warnings)
    {
        var content = new JsonArray();
        foreach (var part in message.Content)
        {
            if (part.Kind == ContentKind.Text)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    content.Add(new JsonObject { ["type"] = "input_text", ["text"] = part.Text });
                }
            }
            else if (part.Kind == ContentKind.Image && part.Image is not null)
            {
                content.Add(InputImage(AdapterSupport.ResolveImage(part.Image), part.Image.Detail));
            }
            else if (part.Kind == ContentKind.Document && part.Document is not null)
            {
                content.Add(InputFile(part.Document));
            }
            else if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
            {
                input.Add(FunctionCallOutput(part.ToolResult, customCallIds));
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, ProviderName, message.Role, part.Kind);
            }
        }

        if (content.Count > 0)
        {
            input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = content });
        }
    }

    private static void AddAssistantMessage(Message message, JsonArray input, HashSet<string> customCallIds, List<Warning> warnings)
    {
        JsonArray? content = null;

        void FlushText()
        {
            if (content is { Count: > 0 })
            {
                input.Add(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = content });
            }

            content = null;
        }

        foreach (var part in message.Content)
        {
            if (part.Kind == ContentKind.Text)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    (content ??= new JsonArray()).Add(new JsonObject { ["type"] = "output_text", ["text"] = part.Text });
                }
            }
            else if (part.Kind == ContentKind.ToolCall && part.ToolCall is { } call)
            {
                FlushText();
                if (string.Equals(call.Type, "custom", StringComparison.OrdinalIgnoreCase))
                {
                    customCallIds.Add(call.Id);
                    input.Add(new JsonObject
                    {
                        ["type"] = "custom_tool_call",
                        ["call_id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = AdapterSupport.RawArgumentText(call),
                    });
                }
                else
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.Id,
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    });
                }
            }
            else if (part.Kind == ContentKind.Thinking && part.Thinking is { } thinking)
            {
                // Only reasoning items produced by this provider (carrying encrypted content) can be replayed; ids are
                // omitted because items are not persisted when store=false.
                if (part.GetProviderMetadata(ProviderName).Str("encrypted_content") is not { } encrypted)
                {
                    continue;
                }

                FlushText();
                var summary = new JsonArray();
                if (thinking.Text.Length > 0)
                {
                    summary.Add(new JsonObject { ["type"] = "summary_text", ["text"] = thinking.Text });
                }

                input.Add(new JsonObject { ["type"] = "reasoning", ["summary"] = summary, ["encrypted_content"] = encrypted });
            }
            else if (part.Kind == ContentKind.RedactedThinking)
            {
                // Anthropic-only opaque reasoning; not replayable here.
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, ProviderName, message.Role, part.Kind);
            }
        }

        FlushText();
    }

    private static void AddToolMessage(
        Message message,
        JsonArray input,
        HashSet<string> customCallIds,
        List<(string CallId, JsonObject Image)> pendingToolImages,
        List<Warning> warnings)
    {
        var results = AdapterSupport.ToolResults(message);
        if (results.Count == 0)
        {
            warnings.Add(new Warning("A tool message without a tool result or tool_call_id was dropped.", "invalid_tool_message"));
            return;
        }

        foreach (var result in results)
        {
            input.Add(FunctionCallOutput(result, customCallIds));
            if (result.ImageData is { Length: > 0 } image)
            {
                var media = new ResolvedMedia(result.ImageMediaType ?? "image/png", Convert.ToBase64String(image), null);
                pendingToolImages.Add((result.ToolCallId, InputImage(media, detail: null)));
            }
        }
    }

    private static JsonObject FunctionCallOutput(ToolResultData result, HashSet<string> customCallIds) => new()
    {
        ["type"] = customCallIds.Contains(result.ToolCallId) ? "custom_tool_call_output" : "function_call_output",
        ["call_id"] = result.ToolCallId,
        ["output"] = result.ContentText,
    };

    /// <summary>
    /// Function call outputs are text-only, so images returned by tools are delivered in a user message that follows
    /// the run of tool outputs.
    /// </summary>
    private static void FlushToolImages(JsonArray input, List<(string CallId, JsonObject Image)> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var content = new JsonArray();
        foreach (var (callId, image) in pending)
        {
            content.Add(new JsonObject { ["type"] = "input_text", ["text"] = $"Image returned by tool call {callId}:" });
            content.Add(image);
        }

        input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = content });
        pending.Clear();
    }

    private static JsonObject InputImage(ResolvedMedia media, string? detail) => new()
    {
        ["type"] = "input_image",
        ["image_url"] = media.IsInline ? media.ToDataUri() : media.Url,
        ["detail"] = string.IsNullOrWhiteSpace(detail) ? "auto" : detail,
    };

    private static JsonObject InputFile(DocumentData document)
    {
        var media = AdapterSupport.ResolveDocument(document);
        if (!media.IsInline)
        {
            return new JsonObject { ["type"] = "input_file", ["file_url"] = media.Url };
        }

        return new JsonObject
        {
            ["type"] = "input_file",
            ["filename"] = document.FileName ?? DefaultFileName(document, media.MediaType),
            ["file_data"] = media.ToDataUri(),
        };
    }

    private static string DefaultFileName(DocumentData document, string mediaType)
    {
        if (document.Url is { } url && !AdapterSupport.TryParseDataUri(url, out _, out _))
        {
            var name = Path.GetFileName(MediaTypes.ExpandPath(url));
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return mediaType switch
        {
            "application/pdf" => "document.pdf",
            "text/plain" => "document.txt",
            "text/markdown" => "document.md",
            "text/csv" => "document.csv",
            "application/json" => "document.json",
            _ => "document",
        };
    }

    [GeneratedRegex(@"^(gpt-([5-9]|\d{2,})|o\d)")]
    private static partial Regex ReasoningModelPattern();
}

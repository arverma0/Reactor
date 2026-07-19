using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.OpenAICompatible;

/// <summary>Translates a unified <see cref="Request"/> into a Chat Completions (<c>POST /chat/completions</c>) body.</summary>
internal static class ChatCompletionsRequestBuilder
{
    public static JsonObject Build(Request request, string provider, bool stream, List<Warning> warnings)
    {
        var messages = new JsonArray();
        var pendingToolImages = new List<(string CallId, JsonObject Image)>();
        foreach (var message in request.Messages)
        {
            if (message.Role != Role.Tool)
            {
                FlushToolImages(messages, pendingToolImages);
            }

            switch (message.Role)
            {
                case Role.System:
                case Role.Developer:
                    if (AdapterSupport.InstructionText(message, provider, warnings) is { } instructions)
                    {
                        messages.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });
                    }

                    break;
                case Role.User:
                    AddUserMessage(message, messages, provider, warnings);
                    break;
                case Role.Assistant:
                    AddAssistantMessage(message, messages, provider, warnings);
                    break;
                case Role.Tool:
                    var results = AdapterSupport.ToolResults(message);
                    if (results.Count == 0)
                    {
                        warnings.Add(new Warning("A tool message without a tool result or tool_call_id was dropped.", "invalid_tool_message"));
                    }

                    foreach (var result in results)
                    {
                        AddToolResult(result, messages, pendingToolImages);
                    }

                    break;
            }
        }

        FlushToolImages(messages, pendingToolImages);

        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages };
        var choice = AdapterSupport.ResolveToolChoice(request, provider);
        if (request.Tools is { Count: > 0 } tools)
        {
            var array = new JsonArray();
            foreach (var tool in tools)
            {
                array.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.Parameters.DeepClone(),
                    },
                });
            }

            body["tools"] = array;
            if (choice is not null)
            {
                body["tool_choice"] = choice.Mode == ToolChoice.NamedMode
                    ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = choice.ToolName } }
                    : choice.Mode;
            }
        }
        else
        {
            AdapterSupport.WarnToolChoiceWithoutTools(choice, warnings);
        }

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            var array = new JsonArray();
            foreach (var stop in stops)
            {
                array.Add(stop);
            }

            body["stop"] = array;
        }

        switch (request.ResponseFormat?.Type)
        {
            case null:
            case ResponseFormat.TextType:
                break;
            case ResponseFormat.JsonType:
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;
            case ResponseFormat.JsonSchemaType when request.ResponseFormat.JsonSchema is { } schema:
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = request.ResponseFormat.SchemaName,
                        ["schema"] = schema.DeepClone(),
                        ["strict"] = request.ResponseFormat.Strict,
                    },
                };
                break;
            case ResponseFormat.JsonSchemaType:
                warnings.Add(new Warning("response_format json_schema has no schema; JSON mode was used instead.", "invalid_response_format"));
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;
            default:
                warnings.Add(new Warning($"Unsupported response_format type '{request.ResponseFormat.Type}' was ignored.", "unsupported_response_format"));
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            body["reasoning_effort"] = request.ReasoningEffort;
        }

        if (request.Metadata is { Count: > 0 })
        {
            warnings.Add(new Warning(
                "metadata is not sent to OpenAI-compatible endpoints; pass it through provider_options if the server supports it.",
                "unsupported_parameter"));
        }

        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.GetProviderOptions(provider) is { } providerOptions)
        {
            JsonHelpers.DeepMerge(body, providerOptions);
        }

        return body;
    }

    private static void AddUserMessage(Message message, JsonArray messages, string provider, List<Warning> warnings)
    {
        var parts = new List<JsonObject>();
        var textOnly = true;
        foreach (var part in message.Content)
        {
            if (part.Kind == ContentKind.Text)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = part.Text });
                }

                continue;
            }

            JsonObject? translated = null;
            if (part.Kind == ContentKind.Image && part.Image is not null)
            {
                var media = AdapterSupport.ResolveImage(part.Image);
                var imageUrl = new JsonObject { ["url"] = media.IsInline ? media.ToDataUri() : media.Url };
                if (!string.IsNullOrWhiteSpace(part.Image.Detail))
                {
                    imageUrl["detail"] = part.Image.Detail;
                }

                translated = new JsonObject { ["type"] = "image_url", ["image_url"] = imageUrl };
            }
            else if (part.Kind == ContentKind.Audio && part.Audio is not null)
            {
                translated = AudioPart(AdapterSupport.ResolveAudio(part.Audio), provider, warnings);
            }
            else if (part.Kind == ContentKind.Document && part.Document is not null)
            {
                translated = FilePart(part.Document, provider, warnings);
            }
            else if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
            {
                messages.Add(ToolMessage(part.ToolResult));
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, provider, message.Role, part.Kind);
            }

            if (translated is not null)
            {
                textOnly = false;
                parts.Add(translated);
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        // Plain string content for the common single-text case maximizes compatibility with simpler servers.
        JsonNode content = textOnly && parts.Count == 1 ? parts[0]["text"]!.DeepClone() : new JsonArray(parts.ToArray<JsonNode?>());
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
    }

    private static void AddAssistantMessage(Message message, JsonArray messages, string provider, List<Warning> warnings)
    {
        var text = new System.Text.StringBuilder();
        var toolCalls = new JsonArray();
        foreach (var part in message.Content)
        {
            if (part.Kind == ContentKind.Text)
            {
                text.Append(part.Text);
            }
            else if (part.Kind == ContentKind.ToolCall && part.ToolCall is { } call)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                });
            }
            else if (part.Kind == ContentKind.Thinking || part.Kind == ContentKind.RedactedThinking)
            {
                // Reasoning is not replayed: several servers reject reasoning_content in requests.
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, provider, message.Role, part.Kind);
            }
        }

        if (text.Length == 0 && toolCalls.Count == 0)
        {
            return;
        }

        var translated = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text.ToString() : null };
        if (toolCalls.Count > 0)
        {
            translated["tool_calls"] = toolCalls;
        }

        messages.Add(translated);
    }

    private static void AddToolResult(ToolResultData result, JsonArray messages, List<(string CallId, JsonObject Image)> pendingToolImages)
    {
        messages.Add(ToolMessage(result));
        if (result.ImageData is { Length: > 0 } image)
        {
            var media = new ResolvedMedia(result.ImageMediaType ?? "image/png", Convert.ToBase64String(image), null);
            pendingToolImages.Add((result.ToolCallId, new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = media.ToDataUri() },
            }));
        }
    }

    private static JsonObject ToolMessage(ToolResultData result) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = result.ToolCallId,
        ["content"] = result.ContentText,
    };

    /// <summary>Tool messages are text-only, so tool images follow the run of tool messages as a user message.</summary>
    private static void FlushToolImages(JsonArray messages, List<(string CallId, JsonObject Image)> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var content = new JsonArray();
        foreach (var (callId, image) in pending)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = $"Image returned by tool call {callId}:" });
            content.Add(image);
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        pending.Clear();
    }

    private static JsonObject? AudioPart(ResolvedMedia media, string provider, List<Warning> warnings)
    {
        var format = media.MediaType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/mpeg" or "audio/mp3" => "mp3",
            _ => null,
        };

        if (!media.IsInline || format is null)
        {
            warnings.Add(new Warning(
                $"{provider} accepts only inline wav/mp3 audio; the '{media.MediaType}' audio part was dropped.", "unsupported_content"));
            return null;
        }

        return new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject { ["data"] = media.Base64, ["format"] = format },
        };
    }

    private static JsonObject? FilePart(DocumentData document, string provider, List<Warning> warnings)
    {
        var media = AdapterSupport.ResolveDocument(document);
        if (!media.IsInline)
        {
            warnings.Add(new Warning($"{provider} does not accept document URLs; the document was dropped.", "unsupported_content"));
            return null;
        }

        return new JsonObject
        {
            ["type"] = "file",
            ["file"] = new JsonObject
            {
                ["filename"] = document.FileName ?? (media.MediaType == "application/pdf" ? "document.pdf" : "document"),
                ["file_data"] = media.ToDataUri(),
            },
        };
    }
}

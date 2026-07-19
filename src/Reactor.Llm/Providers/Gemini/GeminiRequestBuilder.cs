using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Gemini;

/// <summary>Translates a unified <see cref="Request"/> into a Gemini <c>generateContent</c> body.</summary>
internal static partial class GeminiRequestBuilder
{
    internal const string ProviderName = "gemini";

    /// <summary>
    /// Documented placeholder signature for function calls that Gemini did not generate (e.g. history from another
    /// provider); Gemini 3+ rejects unsigned function calls in the current turn without it.
    /// </summary>
    internal const string SkipSignatureValidator = "skip_thought_signature_validator";

    public static JsonObject Build(Request request, List<Warning> warnings)
    {
        var systemTexts = new List<string>();
        var contents = new List<(string Role, List<JsonObject> Parts)>();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case Role.System:
                case Role.Developer:
                    if (AdapterSupport.InstructionText(message, ProviderName, warnings) is { } text)
                    {
                        systemTexts.Add(text);
                    }

                    break;
                case Role.User:
                    Append(contents, "user", UserParts(message, callNames, warnings));
                    break;
                case Role.Assistant:
                    Append(contents, "model", ModelParts(message, callNames, warnings));
                    break;
                case Role.Tool:
                    Append(contents, "user", ToolParts(message, callNames, warnings));
                    break;
            }
        }

        var signatureRequired = (ModelVersion(request.Model)?.Major ?? 0) >= 3;
        var contentArray = new JsonArray();
        foreach (var (role, parts) in contents)
        {
            if (role == "model" && signatureRequired)
            {
                EnsureFunctionCallSignature(parts);
            }

            // Function responses lead their user turn so they directly answer the preceding function calls.
            var ordered = role == "user"
                ? parts.Where(p => p.ContainsKey("functionResponse")).Concat(parts.Where(p => !p.ContainsKey("functionResponse")))
                : parts;
            var partArray = new JsonArray();
            foreach (var part in ordered)
            {
                partArray.Add(part);
            }

            contentArray.Add(new JsonObject { ["role"] = role, ["parts"] = partArray });
        }

        var body = new JsonObject { ["contents"] = contentArray };
        if (systemTexts.Count > 0)
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = string.Join("\n\n", systemTexts) }),
            };
        }

        var choice = AdapterSupport.ResolveToolChoice(request, ProviderName);
        if (request.Tools is { Count: > 0 } tools)
        {
            var declarations = new JsonArray();
            foreach (var tool in tools)
            {
                var declaration = new JsonObject { ["name"] = tool.Name };
                if (!string.IsNullOrEmpty(tool.Description))
                {
                    declaration["description"] = tool.Description;
                }

                if (GeminiSchemaSanitizer.SanitizeParameters(tool.Parameters) is { } parameters)
                {
                    declaration["parameters"] = parameters;
                }

                declarations.Add(declaration);
            }

            body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
            if (choice is not null)
            {
                var config = new JsonObject
                {
                    ["mode"] = choice.Mode switch
                    {
                        ToolChoice.NoneMode => "NONE",
                        ToolChoice.RequiredMode or ToolChoice.NamedMode => "ANY",
                        _ => "AUTO",
                    },
                };
                if (choice.Mode == ToolChoice.NamedMode)
                {
                    config["allowedFunctionNames"] = new JsonArray(JsonValue.Create(choice.ToolName));
                }

                body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
            }
        }
        else
        {
            AdapterSupport.WarnToolChoiceWithoutTools(choice, warnings);
        }

        var generationConfig = BuildGenerationConfig(request, warnings);
        if (generationConfig.Count > 0)
        {
            body["generationConfig"] = generationConfig;
        }

        ApplyProviderOptions(body, request.GetProviderOptions(ProviderName));
        return body;
    }

    /// <summary>Parses <c>gemini-MAJOR[.MINOR]</c> (optionally prefixed with <c>models/</c>) from a model id.</summary>
    internal static (int Major, int Minor)? ModelVersion(string model)
    {
        var match = ModelVersionPattern().Match(model.Trim().ToLowerInvariant());
        if (!match.Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return (major, minor);
    }

    private static JsonObject BuildGenerationConfig(Request request, List<Warning> warnings)
    {
        var config = new JsonObject();
        if (request.Temperature is { } temperature)
        {
            config["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            config["topP"] = topP;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            config["maxOutputTokens"] = maxTokens;
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            var array = new JsonArray();
            foreach (var stop in stops)
            {
                array.Add(stop);
            }

            config["stopSequences"] = array;
        }

        switch (request.ResponseFormat?.Type)
        {
            case null:
            case ResponseFormat.TextType:
                break;
            case ResponseFormat.JsonType:
                config["responseMimeType"] = "application/json";
                break;
            case ResponseFormat.JsonSchemaType:
                config["responseMimeType"] = "application/json";
                if (request.ResponseFormat.JsonSchema is { } schema)
                {
                    config["responseSchema"] = GeminiSchemaSanitizer.Sanitize(schema);
                }
                else
                {
                    warnings.Add(new Warning("response_format json_schema has no schema; JSON mode was used instead.", "invalid_response_format"));
                }

                break;
            default:
                warnings.Add(new Warning($"Unsupported response_format type '{request.ResponseFormat.Type}' was ignored.", "unsupported_response_format"));
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort) && ThinkingConfig(request.Model, request.ReasoningEffort, warnings) is { } thinking)
        {
            config["thinkingConfig"] = thinking;
        }

        return config;
    }

    /// <summary>Gemini 3+ uses <c>thinkingLevel</c>; Gemini 2.5 uses <c>thinkingBudget</c>; older models do not think.</summary>
    private static JsonObject? ThinkingConfig(string model, string effort, List<Warning> warnings)
    {
        var level = effort.Trim().ToLowerInvariant();
        var version = ModelVersion(model);
        if (version is { Major: >= 3 })
        {
            string? thinkingLevel = level switch
            {
                "minimal" => "minimal",
                "low" => "low",
                "medium" => "medium",
                "high" or "xhigh" or "max" => "high",
                _ => null,
            };
            if (thinkingLevel is not null)
            {
                return new JsonObject { ["thinkingLevel"] = thinkingLevel, ["includeThoughts"] = true };
            }
        }
        else if (version is { Major: 2, Minor: 5 })
        {
            int? budget = level switch
            {
                "minimal" => 512,
                "low" => 1024,
                "medium" => 8192,
                "high" or "xhigh" or "max" => 24576,
                _ => null,
            };
            if (budget is not null)
            {
                return new JsonObject { ["thinkingBudget"] = budget.Value, ["includeThoughts"] = true };
            }
        }
        else
        {
            warnings.Add(new Warning(
                $"Model '{model}' does not support thinking configuration; reasoning_effort was ignored.", "unsupported_parameter"));
            return null;
        }

        warnings.Add(new Warning($"Unsupported reasoning_effort '{effort}' was ignored.", "unsupported_parameter"));
        return null;
    }

    private static void ApplyProviderOptions(JsonObject body, JsonObject? providerOptions)
    {
        if (providerOptions is null)
        {
            return;
        }

        // Extra tools (googleSearch, codeExecution, ...) are appended to the function declarations rather than replacing them.
        if (providerOptions["tools"] is JsonArray extraTools)
        {
            AdapterSupport.AppendToArray(body, "tools", extraTools);
        }

        if (providerOptions["thinkingConfig"] is JsonObject thinkingConfig)
        {
            JsonHelpers.DeepMerge(body, new JsonObject { ["generationConfig"] = new JsonObject { ["thinkingConfig"] = thinkingConfig.DeepClone() } });
        }

        if (AdapterSupport.CloneWithout(providerOptions, "tools", "thinkingConfig") is { } rest)
        {
            JsonHelpers.DeepMerge(body, rest);
        }
    }

    private static void Append(List<(string Role, List<JsonObject> Parts)> contents, string role, List<JsonObject> parts)
    {
        if (parts.Count == 0)
        {
            return;
        }

        if (contents.Count > 0 && contents[^1].Role == role)
        {
            contents[^1].Parts.AddRange(parts);
        }
        else
        {
            contents.Add((role, parts));
        }
    }

    private static void EnsureFunctionCallSignature(List<JsonObject> parts)
    {
        var calls = parts.Where(p => p.ContainsKey("functionCall")).ToList();
        if (calls.Count > 0 && !calls.Any(p => p.ContainsKey("thoughtSignature")))
        {
            calls[0]["thoughtSignature"] = SkipSignatureValidator;
        }
    }

    private static List<JsonObject> UserParts(Message message, Dictionary<string, string> callNames, List<Warning> warnings)
    {
        var parts = new List<JsonObject>();
        foreach (var part in message.Content)
        {
            if (part.Kind == ContentKind.Text)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    parts.Add(new JsonObject { ["text"] = part.Text });
                }
            }
            else if (part.Kind == ContentKind.Image && part.Image is not null)
            {
                parts.Add(MediaPart(AdapterSupport.ResolveImage(part.Image)));
            }
            else if (part.Kind == ContentKind.Audio && part.Audio is not null)
            {
                parts.Add(MediaPart(AdapterSupport.ResolveAudio(part.Audio)));
            }
            else if (part.Kind == ContentKind.Document && part.Document is not null)
            {
                parts.Add(MediaPart(AdapterSupport.ResolveDocument(part.Document)));
            }
            else if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
            {
                parts.AddRange(FunctionResponseParts(part.ToolResult, message.Name, callNames, warnings));
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, ProviderName, message.Role, part.Kind);
            }
        }

        return parts;
    }

    private static List<JsonObject> ModelParts(Message message, Dictionary<string, string> callNames, List<Warning> warnings)
    {
        var parts = new List<JsonObject>();
        foreach (var part in message.Content)
        {
            var signature = part.GetProviderMetadata(ProviderName).Str("thoughtSignature");
            JsonObject? translated = null;
            if (part.Kind == ContentKind.Text)
            {
                // An empty text part can carry the response's thought signature (streaming); keep it for round-tripping.
                if (!string.IsNullOrEmpty(part.Text) || signature is not null)
                {
                    translated = new JsonObject { ["text"] = part.Text ?? "" };
                }
            }
            else if (part.Kind == ContentKind.ToolCall && part.ToolCall is { } call)
            {
                callNames[call.Id] = call.Name;
                translated = new JsonObject
                {
                    ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = call.ArgumentsObject },
                };
            }
            else if (part.Kind == ContentKind.Image && part.Image is not null)
            {
                translated = MediaPart(AdapterSupport.ResolveImage(part.Image));
            }
            else if (part.Kind == ContentKind.Thinking || part.Kind == ContentKind.RedactedThinking)
            {
                // Thought summaries are not sent back.
            }
            else
            {
                AdapterSupport.WarnDroppedPart(warnings, ProviderName, message.Role, part.Kind);
            }

            if (translated is not null)
            {
                if (signature is not null)
                {
                    translated["thoughtSignature"] = signature;
                }

                parts.Add(translated);
            }
        }

        return parts;
    }

    private static List<JsonObject> ToolParts(Message message, Dictionary<string, string> callNames, List<Warning> warnings)
    {
        var results = AdapterSupport.ToolResults(message);
        if (results.Count == 0)
        {
            warnings.Add(new Warning("A tool message without a tool result or tool_call_id was dropped.", "invalid_tool_message"));
        }

        var parts = new List<JsonObject>();
        foreach (var result in results)
        {
            parts.AddRange(FunctionResponseParts(result, message.Name, callNames, warnings));
        }

        return parts;
    }

    /// <summary>
    /// A <c>functionResponse</c> addressed by function name (Gemini has no call ids in requests), plus an inline image
    /// part when the tool returned one.
    /// </summary>
    private static IEnumerable<JsonObject> FunctionResponseParts(
        ToolResultData result,
        string? messageName,
        Dictionary<string, string> callNames,
        List<Warning> warnings)
    {
        if (!callNames.TryGetValue(result.ToolCallId, out var name))
        {
            name = messageName;
            if (string.IsNullOrEmpty(name))
            {
                warnings.Add(new Warning(
                    $"No function name found for tool call '{result.ToolCallId}'; the call id was used as the function name.",
                    "unknown_tool_call"));
                name = result.ToolCallId;
            }
        }

        yield return new JsonObject
        {
            ["functionResponse"] = new JsonObject { ["name"] = name, ["response"] = FunctionResponse(result) },
        };

        if (result.ImageData is { Length: > 0 } image)
        {
            yield return MediaPart(new ResolvedMedia(result.ImageMediaType ?? "image/png", Convert.ToBase64String(image), null));
        }
    }

    /// <summary>The <c>response</c> must be an object: objects pass through, other content is wrapped.</summary>
    private static JsonObject FunctionResponse(ToolResultData result)
    {
        JsonNode content = result.Content switch
        {
            null => JsonValue.Create(""),
            JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(text),
            _ => result.Content.DeepClone(),
        };

        if (result.IsError)
        {
            return new JsonObject { ["error"] = content };
        }

        return content as JsonObject ?? new JsonObject { ["result"] = content };
    }

    private static JsonObject MediaPart(ResolvedMedia media) => media.IsInline
        ? new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = media.MediaType, ["data"] = media.Base64 } }
        : new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = media.MediaType, ["fileUri"] = media.Url } };

    [GeneratedRegex(@"^(?:models/)?gemini-(\d+)(?:\.(\d+))?")]
    private static partial Regex ModelVersionPattern();
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Common;

/// <summary>Inline (base64) or remote media resolved from an <see cref="ImageData"/>, <see cref="AudioData"/>, or <see cref="DocumentData"/>.</summary>
/// <param name="MediaType">The MIME type to send.</param>
/// <param name="Base64">Base64 payload when the media is inline; null for remote URLs.</param>
/// <param name="Url">The remote URL when the media is not inline.</param>
internal readonly record struct ResolvedMedia(string MediaType, string? Base64, string? Url)
{
    public bool IsInline => Base64 is not null;

    public string ToDataUri() => $"data:{MediaType};base64,{Base64}";
}

/// <summary>Request/response translation helpers shared by all provider adapters.</summary>
internal static class AdapterSupport
{
    /// <summary>Returns the configured base URL (or the default) without a trailing slash.</summary>
    public static string NormalizeBaseUrl(string? configured, string fallback) =>
        (string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim()).TrimEnd('/');

    /// <summary>
    /// Builds the header set for one request: adapter headers first, then <c>Accept: text/event-stream</c> for streams,
    /// then <see cref="ProviderAdapterOptions.DefaultHeaders"/> (which win on conflicts).
    /// </summary>
    public static Dictionary<string, string> BuildHeaders(
        ProviderAdapterOptions options,
        IEnumerable<KeyValuePair<string, string>> adapterHeaders,
        bool streaming)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in adapterHeaders)
        {
            headers[name] = value;
        }

        if (streaming)
        {
            headers["Accept"] = "text/event-stream";
        }

        if (options.DefaultHeaders is not null)
        {
            foreach (var (name, value) in options.DefaultHeaders)
            {
                headers[name] = value;
            }
        }

        return headers;
    }

    /// <summary>True for the four unified tool choice modes.</summary>
    public static bool IsSupportedToolChoice(string? mode) =>
        mode?.Trim().ToLowerInvariant() is ToolChoice.AutoMode or ToolChoice.NoneMode or ToolChoice.RequiredMode or ToolChoice.NamedMode;

    /// <summary>
    /// Returns the request's effective tool choice with a normalized (lowercase) mode, or null when none applies.
    /// Throws <see cref="UnsupportedToolChoiceError"/> for unknown modes and <see cref="InvalidRequestError"/> when a
    /// named choice lacks a tool name.
    /// </summary>
    public static ToolChoice? ResolveToolChoice(Request request, string provider)
    {
        var choice = request.EffectiveToolChoice;
        if (choice is null)
        {
            return null;
        }

        var mode = choice.Mode?.Trim().ToLowerInvariant() ?? "";
        if (!IsSupportedToolChoice(mode))
        {
            throw new UnsupportedToolChoiceError($"{provider} does not support tool_choice mode '{choice.Mode}'.");
        }

        if (mode == ToolChoice.NamedMode && string.IsNullOrWhiteSpace(choice.ToolName))
        {
            throw new InvalidRequestError("tool_choice mode 'named' requires a tool name.", provider);
        }

        return mode == choice.Mode ? choice : choice with { Mode = mode };
    }

    /// <summary>Adds the standard warning for a tool choice that cannot apply because the request has no tools.</summary>
    public static void WarnToolChoiceWithoutTools(ToolChoice? choice, List<Warning> warnings)
    {
        if (choice?.Mode is ToolChoice.RequiredMode or ToolChoice.NamedMode)
        {
            warnings.Add(new Warning(
                $"tool_choice '{choice.Mode}' was ignored because the request defines no tools.", "tool_choice_ignored"));
        }
    }

    /// <summary>
    /// Parses a provider's serialized tool arguments: a <see cref="JsonObject"/> when valid JSON object text, otherwise the
    /// raw string as a <see cref="JsonValue"/> (so callers can report the parse error). Empty text yields <c>{}</c>.
    /// </summary>
    public static JsonNode ParseToolArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new JsonObject();
        }

        if (JsonHelpers.ParseOrNull(raw) is JsonObject obj)
        {
            return obj;
        }

        return JsonValue.Create(raw);
    }

    /// <summary>Builds the complete <see cref="ToolCall"/> carried by TOOL_CALL_END, preserving the exact raw argument text.</summary>
    public static ToolCall CompletedToolCall(string id, string name, string rawArguments)
    {
        var raw = string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments;
        var data = new ToolCallData { Id = id, Name = name, Arguments = JsonValue.Create(raw) };
        var ok = data.TryParseArguments(out var arguments, out var error);
        return new ToolCall(id, name, arguments, raw) { ArgumentsError = ok ? null : error };
    }

    /// <summary>The arguments of a tool call as provider-ready text (raw strings verbatim, objects serialized).</summary>
    public static string RawArgumentText(ToolCallData call) => call.Arguments switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => call.Arguments.ToJsonString(),
    };

    /// <summary>Synthesizes a unique tool call id for providers that do not assign one.</summary>
    public static string NewToolCallId() => "call_" + Guid.NewGuid().ToString("N");

    /// <summary>Resolves an image to inline base64 (raw bytes, local files, <c>data:</c> URIs) or a remote URL.</summary>
    public static ResolvedMedia ResolveImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();
        if (image.ResolveInline() is { } inline)
        {
            return new ResolvedMedia(inline.MediaType, Convert.ToBase64String(inline.Data), null);
        }

        return FromUrl(image.Url!, image.MediaType, image.EffectiveMediaType);
    }

    /// <summary>Resolves a document to inline base64 (raw bytes, local files, <c>data:</c> URIs) or a remote URL.</summary>
    public static ResolvedMedia ResolveDocument(DocumentData document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if ((document.Url is null) == (document.Data is null))
        {
            throw new InvalidRequestError("DocumentData requires exactly one of 'url' or 'data'.", provider: null);
        }

        if (document.ResolveInline() is { } inline)
        {
            return new ResolvedMedia(inline.MediaType, Convert.ToBase64String(inline.Data), null);
        }

        return FromUrl(
            document.Url!,
            document.MediaType,
            document.MediaType ?? MediaTypes.FromExtension(document.Url) ?? "application/pdf");
    }

    /// <summary>Resolves audio to inline base64 (raw bytes, local files, <c>data:</c> URIs) or a remote URL.</summary>
    public static ResolvedMedia ResolveAudio(AudioData audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if ((audio.Url is null) == (audio.Data is null))
        {
            throw new InvalidRequestError("AudioData requires exactly one of 'url' or 'data'.", provider: null);
        }

        if (audio.Data is not null)
        {
            return new ResolvedMedia(audio.MediaType ?? "audio/wav", Convert.ToBase64String(audio.Data), null);
        }

        if (MediaTypes.IsLocalPath(audio.Url))
        {
            var (bytes, mediaType) = MediaTypes.LoadLocalFile(audio.Url!, audio.MediaType ?? "audio/wav");
            return new ResolvedMedia(audio.MediaType ?? mediaType, Convert.ToBase64String(bytes), null);
        }

        return FromUrl(audio.Url!, audio.MediaType, audio.MediaType ?? MediaTypes.FromExtension(audio.Url) ?? "audio/wav");
    }

    /// <summary>Parses <c>data:[mediatype][;params];base64,payload</c> URIs.</summary>
    public static bool TryParseDataUri(string? url, out string mediaType, out string base64)
    {
        mediaType = "";
        base64 = "";
        if (url is null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return false;
        }

        var meta = url[5..comma];
        var segments = meta.Split(';');
        if (!segments.Skip(1).Any(s => s.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        mediaType = string.IsNullOrWhiteSpace(segments[0]) ? "application/octet-stream" : segments[0].Trim();
        base64 = url[(comma + 1)..];
        return true;
    }

    /// <summary>Reads an integer property regardless of how the number is backed (parsed JSON or in-memory CLR value).</summary>
    public static int? GetInt(JsonNode? node, string property)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(property, out var value) || value is not JsonValue v)
        {
            return null;
        }

        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                if (double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                    !double.IsNaN(d) && !double.IsInfinity(d))
                {
                    return (int)Math.Clamp(Math.Truncate(d), int.MinValue, int.MaxValue);
                }

                return null;
            case JsonValueKind.String:
                return int.TryParse(v.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    /// <summary>The JSON objects in <paramref name="array"/>, skipping nulls and non-object entries.</summary>
    public static IEnumerable<JsonObject> Objects(JsonArray? array) =>
        array is null ? [] : array.OfType<JsonObject>();

    /// <summary>
    /// Clones a provider-options object without the keys an adapter consumes itself, ready to deep-merge into a body.
    /// </summary>
    public static JsonObject? CloneWithout(JsonObject? source, params string[] consumedKeys)
    {
        if (source is null)
        {
            return null;
        }

        var clone = (JsonObject)source.DeepClone();
        foreach (var key in consumedKeys)
        {
            clone.Remove(key);
        }

        return clone.Count == 0 ? null : clone;
    }

    /// <summary>Appends deep clones of <paramref name="extra"/> to the array at <paramref name="body"/>[<paramref name="key"/>].</summary>
    public static void AppendToArray(JsonObject body, string key, JsonArray extra)
    {
        if (body[key] is not JsonArray target)
        {
            target = new JsonArray();
            body[key] = target;
        }

        foreach (var item in extra)
        {
            target.Add(item?.DeepClone());
        }
    }

    /// <summary>Warns that a content part kind is not supported in a role by a provider and was dropped.</summary>
    public static void WarnDroppedPart(List<Warning> warnings, string provider, Role role, ContentKind kind) =>
        warnings.Add(new Warning(
            $"{provider} does not support '{kind}' content in {role.ToWireName()} messages; the part was dropped.",
            "unsupported_content"));

    /// <summary>The text of system/developer messages; non-text parts produce a warning.</summary>
    public static string? InstructionText(Message message, string provider, List<Warning> warnings)
    {
        var texts = new List<string>();
        foreach (var part in message.Content)
        {
            if (part.Kind == ContentKind.Text)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    texts.Add(part.Text);
                }
            }
            else
            {
                WarnDroppedPart(warnings, provider, message.Role, part.Kind);
            }
        }

        return texts.Count == 0 ? null : string.Concat(texts);
    }

    /// <summary>
    /// The tool results carried by a TOOL message. A tool message without <see cref="ContentKind.ToolResult"/> parts but
    /// with <see cref="Message.ToolCallId"/> is treated as a single result whose content is the message text.
    /// </summary>
    public static List<ToolResultData> ToolResults(Message message)
    {
        var results = new List<ToolResultData>();
        foreach (var part in message.Content)
        {
            if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
            {
                results.Add(part.ToolResult);
            }
        }

        if (results.Count == 0 && !string.IsNullOrEmpty(message.ToolCallId))
        {
            results.Add(new ToolResultData { ToolCallId = message.ToolCallId, Content = JsonValue.Create(message.Text) });
        }

        return results;
    }

    private static ResolvedMedia FromUrl(string url, string? explicitMediaType, string fallbackMediaType) =>
        TryParseDataUri(url, out var mediaType, out var base64)
            ? new ResolvedMedia(explicitMediaType ?? mediaType, base64, null)
            : new ResolvedMedia(fallbackMediaType, null, url);
}

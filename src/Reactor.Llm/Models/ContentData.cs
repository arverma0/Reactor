using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm;

/// <summary>Image content: a URL (remote or local file path) or raw bytes.</summary>
public sealed record ImageData
{
    /// <summary>URL pointing to the image. Local file paths (<c>/</c>, <c>./</c>, <c>~</c>, drive letters) are read and inlined by adapters.</summary>
    public string? Url { get; init; }

    /// <summary>Raw image bytes.</summary>
    public byte[]? Data { get; init; }

    /// <summary>MIME type, e.g. <c>image/png</c>. Defaults to <c>image/png</c> when <see cref="Data"/> is set.</summary>
    public string? MediaType { get; init; }

    /// <summary>Processing fidelity hint: <c>auto</c>, <c>low</c>, <c>high</c> (OpenAI only).</summary>
    public string? Detail { get; init; }

    public static ImageData FromUrl(string url, string? detail = null) => new() { Url = url, Detail = detail };

    public static ImageData FromBytes(byte[] data, string mediaType = "image/png") => new() { Data = data, MediaType = mediaType };

    /// <summary>Reads a local image file eagerly.</summary>
    public static ImageData FromFile(string path)
    {
        var (bytes, mediaType) = MediaTypes.LoadLocalFile(path, "image/png");
        return new ImageData { Data = bytes, MediaType = mediaType };
    }

    /// <summary>The media type to send, inferring it from <see cref="Url"/> when not set.</summary>
    public string EffectiveMediaType =>
        MediaType ?? (Data is not null ? "image/png" : MediaTypes.FromExtension(Url) ?? "image/png");

    /// <summary>Throws when neither or both of <see cref="Url"/> and <see cref="Data"/> are set.</summary>
    public void Validate()
    {
        if ((Url is null) == (Data is null))
        {
            throw new InvalidRequestError("ImageData requires exactly one of 'url' or 'data'.", provider: null);
        }
    }

    /// <summary>
    /// Resolves the image to inline bytes when it is raw data or a local file path; returns null for remote URLs.
    /// </summary>
    public (byte[] Data, string MediaType)? ResolveInline()
    {
        if (Data is not null)
        {
            return (Data, EffectiveMediaType);
        }

        if (Url is not null && MediaTypes.IsLocalPath(Url))
        {
            return MediaTypes.LoadLocalFile(Url, MediaType ?? "image/png");
        }

        return null;
    }
}

/// <summary>Audio content as a URL or raw bytes.</summary>
public sealed record AudioData
{
    public string? Url { get; init; }
    public byte[]? Data { get; init; }

    /// <summary>MIME type, e.g. <c>audio/wav</c>.</summary>
    public string? MediaType { get; init; }
}

/// <summary>Document content (PDF, etc.) as a URL or raw bytes.</summary>
public sealed record DocumentData
{
    public string? Url { get; init; }
    public byte[]? Data { get; init; }

    /// <summary>MIME type, e.g. <c>application/pdf</c>.</summary>
    public string? MediaType { get; init; }

    /// <summary>Optional display name.</summary>
    public string? FileName { get; init; }

    public (byte[] Data, string MediaType)? ResolveInline()
    {
        if (Data is not null)
        {
            return (Data, MediaType ?? MediaTypes.FromExtension(FileName) ?? "application/pdf");
        }

        if (Url is not null && MediaTypes.IsLocalPath(Url))
        {
            return MediaTypes.LoadLocalFile(Url, MediaType ?? "application/pdf");
        }

        return null;
    }
}

/// <summary>A model-initiated tool invocation inside an assistant message.</summary>
public sealed record ToolCallData
{
    /// <summary>Provider-assigned (or adapter-synthesized) unique id used to link tool results back to the call.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Parsed arguments (<see cref="JsonObject"/>) or the raw argument string (<see cref="JsonValue"/>).</summary>
    public JsonNode? Arguments { get; init; }

    /// <summary><c>function</c> (default) or <c>custom</c>.</summary>
    public string Type { get; init; } = "function";

    /// <summary>The arguments as a JSON string, suitable for providers that expect serialized arguments.</summary>
    public string ArgumentsJson => Arguments switch
    {
        null => "{}",
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? "{}" : s,
        _ => Arguments.ToJsonString(),
    };

    /// <summary>The arguments parsed as an object. Unparseable raw strings yield an empty object.</summary>
    public JsonObject ArgumentsObject => TryParseArguments(out var obj, out _) ? obj : new JsonObject();

    public bool TryParseArguments(out JsonObject arguments, out string? error)
    {
        switch (Arguments)
        {
            case null:
                arguments = new JsonObject();
                error = null;
                return true;
            case JsonObject o:
                arguments = (JsonObject)o.DeepClone();
                error = null;
                return true;
            case JsonValue v when v.TryGetValue<string>(out var raw):
                if (string.IsNullOrWhiteSpace(raw))
                {
                    arguments = new JsonObject();
                    error = null;
                    return true;
                }

                try
                {
                    if (JsonNode.Parse(raw) is JsonObject parsed)
                    {
                        arguments = parsed;
                        error = null;
                        return true;
                    }

                    arguments = new JsonObject();
                    error = "Tool call arguments must be a JSON object.";
                    return false;
                }
                catch (System.Text.Json.JsonException ex)
                {
                    arguments = new JsonObject();
                    error = $"Tool call arguments are not valid JSON: {ex.Message}";
                    return false;
                }
            default:
                arguments = new JsonObject();
                error = "Tool call arguments must be a JSON object.";
                return false;
        }
    }
}

/// <summary>The result of executing a tool call.</summary>
public sealed record ToolResultData
{
    /// <summary>The <see cref="ToolCallData.Id"/> this result answers.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>The tool's output: a JSON string value, object, or array.</summary>
    public JsonNode? Content { get; init; }

    public bool IsError { get; init; }

    /// <summary>Optional image produced by the tool (e.g. a screenshot or an image file read).</summary>
    public byte[]? ImageData { get; init; }

    public string? ImageMediaType { get; init; }

    /// <summary>The content rendered as text: strings verbatim, structured values as JSON.</summary>
    public string ContentText => JsonHelpers.NodeToText(Content);
}

/// <summary>Model reasoning content. For redacted thinking, <see cref="Text"/> carries the opaque payload verbatim.</summary>
public sealed record ThinkingData
{
    public string Text { get; init; } = "";

    /// <summary>Provider-specific signature that must be round-tripped (Anthropic).</summary>
    public string? Signature { get; init; }

    /// <summary>True for redacted thinking whose opaque <see cref="Text"/> must be passed back verbatim.</summary>
    public bool Redacted { get; init; }
}

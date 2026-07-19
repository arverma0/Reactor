using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>
/// One piece of a message body. A tagged union: <see cref="Kind"/> determines which data field is populated.
/// </summary>
public sealed record ContentPart
{
    public ContentKind Kind { get; init; } = ContentKind.Text;

    /// <summary>Populated when <see cref="Kind"/> is <see cref="ContentKind.Text"/>.</summary>
    public string? Text { get; init; }

    public ImageData? Image { get; init; }

    public AudioData? Audio { get; init; }

    public DocumentData? Document { get; init; }

    public ToolCallData? ToolCall { get; init; }

    public ToolResultData? ToolResult { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="ContentKind.Thinking"/> or <see cref="ContentKind.RedactedThinking"/>.</summary>
    public ThinkingData? Thinking { get; init; }

    /// <summary>
    /// Opaque provider data that must round-trip with this part, keyed by provider name
    /// (for example <c>{"gemini": {"thoughtSignature": "..."}}</c>). Adapters ignore keys that are not theirs.
    /// </summary>
    public JsonObject? ProviderMetadata { get; init; }

    public static ContentPart TextPart(string text) => new() { Kind = ContentKind.Text, Text = text };

    public static ContentPart ImagePart(ImageData image) => new() { Kind = ContentKind.Image, Image = image };

    public static ContentPart AudioPart(AudioData audio) => new() { Kind = ContentKind.Audio, Audio = audio };

    public static ContentPart DocumentPart(DocumentData document) => new() { Kind = ContentKind.Document, Document = document };

    public static ContentPart ToolCallPart(string id, string name, JsonNode? arguments, string type = "function") => new()
    {
        Kind = ContentKind.ToolCall,
        ToolCall = new ToolCallData { Id = id, Name = name, Arguments = arguments, Type = type },
    };

    public static ContentPart ToolResultPart(string toolCallId, JsonNode? content, bool isError = false, byte[]? imageData = null, string? imageMediaType = null) => new()
    {
        Kind = ContentKind.ToolResult,
        ToolResult = new ToolResultData
        {
            ToolCallId = toolCallId,
            Content = content,
            IsError = isError,
            ImageData = imageData,
            ImageMediaType = imageMediaType,
        },
    };

    public static ContentPart ThinkingPart(string text, string? signature = null) => new()
    {
        Kind = ContentKind.Thinking,
        Thinking = new ThinkingData { Text = text, Signature = signature },
    };

    public static ContentPart RedactedThinkingPart(string opaqueData) => new()
    {
        Kind = ContentKind.RedactedThinking,
        Thinking = new ThinkingData { Text = opaqueData, Redacted = true },
    };

    /// <summary>Gets provider-specific metadata for <paramref name="provider"/>, if any.</summary>
    public JsonObject? GetProviderMetadata(string provider) => ProviderMetadata?[provider] as JsonObject;
}

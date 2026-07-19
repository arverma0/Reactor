using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reactor.Llm;

/// <summary>
/// Discriminator for <see cref="ContentPart"/>. Accepts the well-known kinds and arbitrary strings so
/// provider-specific content kinds can be represented without changing the core model.
/// </summary>
[JsonConverter(typeof(ContentKindJsonConverter))]
public readonly struct ContentKind : IEquatable<ContentKind>
{
    private readonly string? _value;

    public ContentKind(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>The wire value, e.g. <c>"text"</c>. A default instance reads as <c>"text"</c>.</summary>
    public string Value => _value ?? "text";

    public static ContentKind Text { get; } = new("text");
    public static ContentKind Image { get; } = new("image");
    public static ContentKind Audio { get; } = new("audio");
    public static ContentKind Document { get; } = new("document");
    public static ContentKind ToolCall { get; } = new("tool_call");
    public static ContentKind ToolResult { get; } = new("tool_result");
    public static ContentKind Thinking { get; } = new("thinking");
    public static ContentKind RedactedThinking { get; } = new("redacted_thinking");

    public bool Equals(ContentKind other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ContentKind other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(ContentKind left, ContentKind right) => left.Equals(right);

    public static bool operator !=(ContentKind left, ContentKind right) => !left.Equals(right);

    public static implicit operator ContentKind(string value) => new(value);

    public override string ToString() => Value;
}

internal sealed class ContentKindJsonConverter : JsonConverter<ContentKind>
{
    public override ContentKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "text");

    public override void Write(Utf8JsonWriter writer, ContentKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

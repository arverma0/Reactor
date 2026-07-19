using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Reactor.Llm;

/// <summary>Discriminator for <see cref="StreamEvent"/>. Accepts the well-known types and arbitrary strings.</summary>
[JsonConverter(typeof(StreamEventTypeJsonConverter))]
public readonly struct StreamEventType : IEquatable<StreamEventType>
{
    private readonly string? _value;

    public StreamEventType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? "provider_event";

    /// <summary>Stream has begun. May include warnings.</summary>
    public static StreamEventType StreamStart { get; } = new("stream_start");

    /// <summary>A new text segment has begun. Includes <see cref="StreamEvent.TextId"/>.</summary>
    public static StreamEventType TextStart { get; } = new("text_start");

    /// <summary>Incremental text. Includes <see cref="StreamEvent.Delta"/> and <see cref="StreamEvent.TextId"/>.</summary>
    public static StreamEventType TextDelta { get; } = new("text_delta");

    /// <summary>A text segment is complete.</summary>
    public static StreamEventType TextEnd { get; } = new("text_end");

    public static StreamEventType ReasoningStart { get; } = new("reasoning_start");

    public static StreamEventType ReasoningDelta { get; } = new("reasoning_delta");

    public static StreamEventType ReasoningEnd { get; } = new("reasoning_end");

    /// <summary>A tool call has begun. <see cref="StreamEvent.ToolCall"/> carries id and name.</summary>
    public static StreamEventType ToolCallStart { get; } = new("tool_call_start");

    /// <summary>Incremental tool call arguments (partial JSON in <see cref="StreamEvent.Delta"/>).</summary>
    public static StreamEventType ToolCallDelta { get; } = new("tool_call_delta");

    /// <summary>A tool call is fully formed. <see cref="StreamEvent.ToolCall"/> carries the parsed arguments.</summary>
    public static StreamEventType ToolCallEnd { get; } = new("tool_call_end");

    /// <summary>Generation complete. Includes finish reason, usage, and the accumulated response.</summary>
    public static StreamEventType Finish { get; } = new("finish");

    public static StreamEventType Error { get; } = new("error");

    /// <summary>A raw provider event not mapped to the unified model.</summary>
    public static StreamEventType ProviderEvent { get; } = new("provider_event");

    /// <summary>High-level streaming: a tool-loop step finished (tools executed) and the next step begins.</summary>
    public static StreamEventType StepFinish { get; } = new("step_finish");

    public bool Equals(StreamEventType other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is StreamEventType other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(StreamEventType left, StreamEventType right) => left.Equals(right);

    public static bool operator !=(StreamEventType left, StreamEventType right) => !left.Equals(right);

    public static implicit operator StreamEventType(string value) => new(value);

    public override string ToString() => Value;
}

internal sealed class StreamEventTypeJsonConverter : JsonConverter<StreamEventType>
{
    public override StreamEventType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "provider_event");

    public override void Write(Utf8JsonWriter writer, StreamEventType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>A normalized streaming event. Provider-specific SSE formats are translated into this model.</summary>
public sealed record StreamEvent
{
    public required StreamEventType Type { get; init; }

    /// <summary>Incremental text (TEXT_DELTA) or incremental tool-call argument JSON (TOOL_CALL_DELTA).</summary>
    public string? Delta { get; init; }

    /// <summary>Identifies which text segment a text event belongs to.</summary>
    public string? TextId { get; init; }

    /// <summary>Incremental reasoning text (REASONING_DELTA).</summary>
    public string? ReasoningDelta { get; init; }

    /// <summary>Partial (START/DELTA: id and name) or complete (END: parsed arguments) tool call.</summary>
    public ToolCall? ToolCall { get; init; }

    public FinishReason? FinishReason { get; init; }

    public Usage? Usage { get; init; }

    /// <summary>The full accumulated response (FINISH).</summary>
    public Response? Response { get; init; }

    public SdkError? Error { get; init; }

    /// <summary>Non-fatal warnings (typically on STREAM_START).</summary>
    public IReadOnlyList<Warning>? Warnings { get; init; }

    /// <summary>The raw provider event, for passthrough.</summary>
    public JsonNode? Raw { get; init; }
}

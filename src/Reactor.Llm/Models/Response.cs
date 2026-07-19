using System.Text;
using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>The result of a non-streaming generation (or the accumulated result of a stream).</summary>
public sealed record Response
{
    /// <summary>Provider-assigned response id.</summary>
    public string Id { get; init; } = "";

    /// <summary>The model actually used (may differ from the requested one).</summary>
    public string Model { get; init; } = "";

    /// <summary>The provider that fulfilled the request.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The assistant's response.</summary>
    public required Message Message { get; init; }

    public FinishReason FinishReason { get; init; } = FinishReason.Stop;

    public Usage Usage { get; init; } = Usage.Empty;

    /// <summary>The raw provider response, for debugging.</summary>
    public JsonNode? Raw { get; init; }

    public IReadOnlyList<Warning> Warnings { get; init; } = [];

    public RateLimitInfo? RateLimit { get; init; }

    /// <summary>Concatenated text from all text parts.</summary>
    public string Text => Message.Text;

    /// <summary>Tool calls extracted from the message, in order.</summary>
    public IReadOnlyList<ToolCall> ToolCalls => Message.ToolCalls.Select(ToolCall.FromData).ToList();

    /// <summary>Concatenated reasoning text, or null when the response has no (readable) reasoning.</summary>
    public string? Reasoning
    {
        get
        {
            StringBuilder? sb = null;
            foreach (var part in Message.Content)
            {
                if (part.Kind == ContentKind.Thinking && part.Thinking is { Redacted: false } t && t.Text.Length > 0)
                {
                    sb ??= new StringBuilder();
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(t.Text);
                }
            }

            return sb?.ToString();
        }
    }
}

/// <summary>Why generation stopped: a portable reason plus the provider's raw value.</summary>
/// <param name="Reason"><c>stop</c>, <c>length</c>, <c>tool_calls</c>, <c>content_filter</c>, <c>error</c>, or <c>other</c>.</param>
/// <param name="Raw">The provider's native finish reason string.</param>
public sealed record FinishReason(string Reason, string? Raw = null)
{
    public const string StopReason = "stop";
    public const string LengthReason = "length";
    public const string ToolCallsReason = "tool_calls";
    public const string ContentFilterReason = "content_filter";
    public const string ErrorReason = "error";
    public const string OtherReason = "other";

    public static FinishReason Stop { get; } = new(StopReason);
    public static FinishReason Length { get; } = new(LengthReason);
    public static FinishReason ToolCalls { get; } = new(ToolCallsReason);
    public static FinishReason ContentFilter { get; } = new(ContentFilterReason);
    public static FinishReason Error { get; } = new(ErrorReason);
    public static FinishReason Other { get; } = new(OtherReason);
}

/// <summary>Token accounting. Supports <c>+</c> for aggregating across steps.</summary>
public sealed record Usage
{
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    public int TotalTokens { get; init; }

    /// <summary>Chain-of-thought tokens (estimated for Anthropic when derived from thinking blocks).</summary>
    public int? ReasoningTokens { get; init; }

    public int? CacheReadTokens { get; init; }

    public int? CacheWriteTokens { get; init; }

    /// <summary>The raw provider usage object.</summary>
    public JsonNode? Raw { get; init; }

    public static Usage Empty { get; } = new();

    public static Usage operator +(Usage a, Usage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new Usage
        {
            InputTokens = a.InputTokens + b.InputTokens,
            OutputTokens = a.OutputTokens + b.OutputTokens,
            TotalTokens = a.TotalTokens + b.TotalTokens,
            ReasoningTokens = AddOptional(a.ReasoningTokens, b.ReasoningTokens),
            CacheReadTokens = AddOptional(a.CacheReadTokens, b.CacheReadTokens),
            CacheWriteTokens = AddOptional(a.CacheWriteTokens, b.CacheWriteTokens),
        };
    }

    private static int? AddOptional(int? x, int? y) => x is null && y is null ? null : (x ?? 0) + (y ?? 0);
}

/// <summary>A non-fatal issue reported alongside a response.</summary>
public sealed record Warning(string Message, string? Code = null);

/// <summary>Rate limit metadata parsed from response headers. Informational only.</summary>
public sealed record RateLimitInfo
{
    public int? RequestsRemaining { get; init; }
    public int? RequestsLimit { get; init; }
    public int? TokensRemaining { get; init; }
    public int? TokensLimit { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
}

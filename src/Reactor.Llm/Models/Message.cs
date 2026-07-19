using System.Text;
using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>The fundamental unit of conversation. A conversation is an ordered list of messages.</summary>
public sealed record Message
{
    public required Role Role { get; init; }

    /// <summary>The message body (multimodal).</summary>
    public IReadOnlyList<ContentPart> Content { get; init; } = [];

    /// <summary>Optional name, used for tool messages and developer attribution.</summary>
    public string? Name { get; init; }

    /// <summary>Links a tool-result message to its tool call.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Concatenation of all text parts; empty when there are none.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var part in Content)
            {
                if (part.Kind == ContentKind.Text && part.Text is not null)
                {
                    sb.Append(part.Text);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>All tool calls contained in this message, in order.</summary>
    public IEnumerable<ToolCallData> ToolCalls =>
        Content.Where(p => p.Kind == ContentKind.ToolCall && p.ToolCall is not null).Select(p => p.ToolCall!);

    public static Message System(string text) => new() { Role = Role.System, Content = [ContentPart.TextPart(text)] };

    public static Message Developer(string text) => new() { Role = Role.Developer, Content = [ContentPart.TextPart(text)] };

    public static Message User(string text) => new() { Role = Role.User, Content = [ContentPart.TextPart(text)] };

    public static Message User(params ContentPart[] parts) => new() { Role = Role.User, Content = parts };

    public static Message Assistant(string text) => new() { Role = Role.Assistant, Content = [ContentPart.TextPart(text)] };

    public static Message Assistant(params ContentPart[] parts) => new() { Role = Role.Assistant, Content = parts };

    /// <summary>Creates a tool-result message for <paramref name="toolCallId"/> with string content.</summary>
    public static Message ToolResult(string toolCallId, string content, bool isError = false) =>
        ToolResult(toolCallId, JsonValue.Create(content), isError);

    /// <summary>Creates a tool-result message for <paramref name="toolCallId"/> with structured content.</summary>
    public static Message ToolResult(string toolCallId, JsonNode? content, bool isError = false, byte[]? imageData = null, string? imageMediaType = null) => new()
    {
        Role = Role.Tool,
        ToolCallId = toolCallId,
        Content = [ContentPart.ToolResultPart(toolCallId, content, isError, imageData, imageMediaType)],
    };
}

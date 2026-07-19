using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reactor.Llm.Utilities;

namespace Reactor.Llm;

/// <summary>A tool as described to the model: name, description, and a JSON Schema for its arguments.</summary>
public sealed partial record ToolDefinition
{
    public ToolDefinition(string name, string description, JsonObject parameters)
    {
        Validate(name, parameters);
        Name = name;
        Description = description ?? "";
        Parameters = parameters;
    }

    /// <summary>Unique identifier: <c>[a-zA-Z][a-zA-Z0-9_]*</c>, at most 64 characters.</summary>
    public string Name { get; }

    public string Description { get; }

    /// <summary>JSON Schema with <c>"type": "object"</c> at the root.</summary>
    public JsonObject Parameters { get; }

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && NamePattern().IsMatch(name);

    private static void Validate(string name, JsonObject parameters)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                $"Invalid tool name '{name}': names must match [a-zA-Z][a-zA-Z0-9_]* and be at most 64 characters.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(parameters);
        var type = parameters["type"];
        if (type is null || type.GetValueKind() != System.Text.Json.JsonValueKind.String || type.GetValue<string>() != "object")
        {
            throw new ArgumentException($"Tool '{name}' parameters must be a JSON Schema with \"type\": \"object\" at the root.", nameof(parameters));
        }
    }

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]*$")]
    private static partial Regex NamePattern();
}

/// <summary>Context passed to a tool's execute handler.</summary>
/// <param name="ToolCallId">The id of the call being executed.</param>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">Parsed, validated arguments.</param>
/// <param name="Messages">The conversation so far (injected context).</param>
public sealed record ToolInvocation(string ToolCallId, string Name, JsonObject Arguments, IReadOnlyList<Message> Messages);

/// <summary>Executes a tool. Return any JSON-serializable value; throw to report a tool failure to the model.</summary>
public delegate ValueTask<object?> ToolExecuteHandler(ToolInvocation invocation, CancellationToken cancellationToken);

/// <summary>
/// A tool definition plus an optional execute handler. Tools with a handler are "active" and are run automatically
/// by <c>Ai.GenerateAsync</c>; tools without one are "passive" and their calls are returned to the caller.
/// </summary>
public sealed class Tool
{
    public Tool(string name, string description, JsonObject parameters, ToolExecuteHandler? execute = null)
    {
        Definition = new ToolDefinition(name, description, parameters);
        Execute = execute;
    }

    public ToolDefinition Definition { get; }

    public string Name => Definition.Name;

    public string Description => Definition.Description;

    public JsonObject Parameters => Definition.Parameters;

    public ToolExecuteHandler? Execute { get; }

    public bool IsActive => Execute is not null;

    /// <summary>Creates an active tool from a synchronous handler over the parsed arguments.</summary>
    public static Tool Create(string name, string description, JsonObject parameters, Func<JsonObject, object?> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return new Tool(name, description, parameters, (inv, _) => ValueTask.FromResult(execute(inv.Arguments)));
    }

    /// <summary>Creates an active tool from an asynchronous handler over the parsed arguments.</summary>
    public static Tool Create(string name, string description, JsonObject parameters, Func<JsonObject, CancellationToken, Task<object?>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return new Tool(name, description, parameters, async (inv, ct) => await execute(inv.Arguments, ct).ConfigureAwait(false));
    }

    /// <summary>Creates a passive tool: calls are returned to the caller instead of being executed.</summary>
    public static Tool Passive(string name, string description, JsonObject parameters) => new(name, description, parameters);
}

/// <summary>Controls whether and how the model uses tools.</summary>
/// <param name="Mode"><c>auto</c>, <c>none</c>, <c>required</c>, or <c>named</c>.</param>
/// <param name="ToolName">Required when <paramref name="Mode"/> is <c>named</c>.</param>
public sealed record ToolChoice(string Mode, string? ToolName = null)
{
    public const string AutoMode = "auto";
    public const string NoneMode = "none";
    public const string RequiredMode = "required";
    public const string NamedMode = "named";

    public static ToolChoice Auto { get; } = new(AutoMode);
    public static ToolChoice None { get; } = new(NoneMode);
    public static ToolChoice Required { get; } = new(RequiredMode);

    public static ToolChoice Named(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return new ToolChoice(NamedMode, toolName);
    }
}

/// <summary>A tool call extracted from a response, ready for execution.</summary>
/// <param name="Id">Unique identifier (provider-assigned or synthesized).</param>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">Parsed JSON arguments (empty when <see cref="ArgumentsError"/> is set).</param>
/// <param name="RawArguments">The raw argument string before parsing, when available.</param>
public sealed record ToolCall(string Id, string Name, JsonObject Arguments, string? RawArguments = null)
{
    /// <summary>Set when the raw arguments could not be parsed as a JSON object.</summary>
    public string? ArgumentsError { get; init; }

    public static ToolCall FromData(ToolCallData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var ok = data.TryParseArguments(out var args, out var error);
        string? raw = data.Arguments switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            null => null,
            _ => data.Arguments.ToJsonString(),
        };
        return new ToolCall(data.Id, data.Name, args, raw) { ArgumentsError = ok ? null : error };
    }
}

/// <summary>The outcome of executing a tool call.</summary>
/// <param name="ToolCallId">Correlates to <see cref="ToolCall.Id"/>.</param>
/// <param name="Content">The tool's output: a string value, object, or array.</param>
/// <param name="IsError">True if the tool execution failed.</param>
public sealed record ToolResult(string ToolCallId, JsonNode? Content, bool IsError = false)
{
    public byte[]? ImageData { get; init; }

    public string? ImageMediaType { get; init; }

    public string ContentText => JsonHelpers.NodeToText(Content);

    public static ToolResult FromText(string toolCallId, string text, bool isError = false) =>
        new(toolCallId, JsonValue.Create(text), isError);

    /// <summary>Converts this result to a tool-role message for continuing the conversation.</summary>
    public Message ToMessage() => Message.ToolResult(ToolCallId, Content?.DeepClone(), IsError, ImageData, ImageMediaType);
}

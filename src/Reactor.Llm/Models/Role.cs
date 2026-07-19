namespace Reactor.Llm;

/// <summary>Who produced a message. Five roles cover the semantics of all major providers.</summary>
public enum Role
{
    /// <summary>High-level instructions shaping model behavior. Typically first.</summary>
    System,

    /// <summary>Human input: text, images, audio, documents.</summary>
    User,

    /// <summary>Model output: text, tool calls, thinking blocks.</summary>
    Assistant,

    /// <summary>Tool execution results, linked by <c>tool_call_id</c>.</summary>
    Tool,

    /// <summary>Privileged instructions from the application (not the end user).</summary>
    Developer,
}

public static class RoleExtensions
{
    /// <summary>The lowercase role name used in logs and serialized history.</summary>
    public static string ToWireName(this Role role) => role switch
    {
        Role.System => "system",
        Role.User => "user",
        Role.Assistant => "assistant",
        Role.Tool => "tool",
        Role.Developer => "developer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}

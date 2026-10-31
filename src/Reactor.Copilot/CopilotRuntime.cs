using System.Text.Json.Nodes;
using Reactor.Backends;

namespace Reactor.Copilot;

/// <summary>Normalized activity from a Copilot session (decoupled from the preview SDK's event types).</summary>
internal abstract record CopilotActivity
{
    /// <summary>True for events from a Copilot sub-agent rather than the top-level agent.</summary>
    public bool FromSubagent { get; init; }

    internal sealed record AssistantMessage(string Content, string? Model) : CopilotActivity;

    internal sealed record ToolStarted(string ToolCallId, string ToolName, JsonNode? Arguments) : CopilotActivity;

    internal sealed record ToolCompleted(string ToolCallId, bool Success, string? ErrorMessage) : CopilotActivity;

    /// <summary>
    /// One model call's usage. <see cref="InputTokens"/> includes cached tokens (<see cref="CacheReadTokens"/> and
    /// <see cref="CacheWriteTokens"/>); <see cref="NanoAiu"/> is the call's cost in nano AI units when the runtime reports it.
    /// </summary>
    internal sealed record Usage(string? Model, long InputTokens, long OutputTokens, long CacheReadTokens, long ReasoningTokens) : CopilotActivity
    {
        public long CacheWriteTokens { get; init; }

        public double? NanoAiu { get; init; }
    }

    internal sealed record Error(string Message, string? ErrorType, int? StatusCode) : CopilotActivity;

    internal sealed record Warning(string Message) : CopilotActivity;

    internal sealed record UsageCheckpoint(double TotalNanoAiu) : CopilotActivity;
}

/// <summary>Settings for creating a Copilot conversation (session).</summary>
internal sealed record CopilotConversationOptions
{
    public required string SessionId { get; init; }

    public string? Model { get; init; }

    public string? ReasoningEffort { get; init; }

    public required string WorkingDirectory { get; init; }

    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    /// <summary>The tool hooks in effect for the stage currently using the conversation.</summary>
    public required Func<ToolHookSettings?> Hooks { get; init; }

    public IReadOnlyList<string>? ExcludedTools { get; init; }

    /// <summary>Text appended to the runtime's system message (the stage's standing instructions), or null.</summary>
    public string? SystemMessage { get; init; }
}

/// <summary>A live Copilot conversation.</summary>
internal interface ICopilotConversation : IAsyncDisposable
{
    string SessionId { get; }

    IDisposable Subscribe(Action<CopilotActivity> handler);

    /// <summary>Sends a prompt and waits until the agent is idle; returns the final assistant message (or null).</summary>
    Task<string?> SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Interjects a message into the turn in progress (steering), without waiting for a reply.</summary>
    Task SteerAsync(string message, CancellationToken cancellationToken);

    Task AbortAsync();

    Task SetModelAsync(string model, string? reasoningEffort, CancellationToken cancellationToken);
}

/// <summary>A model offered by the Copilot runtime.</summary>
public sealed record CopilotModelInfo(
    string Id,
    string Name,
    string? PolicyState,
    double? BillingMultiplier,
    int ContextWindow,
    IReadOnlyList<string> ReasoningEfforts)
{
    /// <summary>False when the account's policy disables the model.</summary>
    public bool Enabled => !string.Equals(PolicyState, "disabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Creates Copilot conversations (one runtime process serves all of them).</summary>
internal interface ICopilotRuntime : IAsyncDisposable
{
    Task<ICopilotConversation> CreateConversationAsync(CopilotConversationOptions options, CancellationToken cancellationToken);

    /// <summary>The models available to the signed-in account.</summary>
    Task<IReadOnlyList<CopilotModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
}

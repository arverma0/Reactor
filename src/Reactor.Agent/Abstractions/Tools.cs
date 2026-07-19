using System.Text.Json.Nodes;
using Reactor.Llm;

namespace Reactor.Agent;

/// <summary>Executes a registered tool with parsed, validated arguments. Throw to report a tool error to the model.</summary>
public delegate Task<ToolOutput> ToolExecutor(JsonObject arguments, ToolContext context);

/// <summary>The output of a tool: text for the model, plus optional image data for multimodal models.</summary>
/// <param name="Text">Full (untruncated) text output. The session truncates before sending to the model.</param>
public sealed record ToolOutput(string Text)
{
    public byte[]? ImageData { get; init; }

    public string? ImageMediaType { get; init; }

    public static implicit operator ToolOutput(string text) => new(text);
}

/// <summary>Everything a tool executor can use while running.</summary>
public sealed class ToolContext
{
    public required IExecutionEnvironment Environment { get; init; }

    public required SessionConfig Config { get; init; }

    public string ToolCallId { get; init; } = "";

    public string ToolName { get; init; } = "";

    /// <summary>Receives incremental output (emitted as TOOL_CALL_OUTPUT_DELTA events).</summary>
    public Action<string>? OnOutputDelta { get; init; }

    /// <summary>Subagent operations, when the session supports them.</summary>
    public ISubagentHost? Subagents { get; init; }

    /// <summary>The profile's default shell timeout in ms, overriding <see cref="SessionConfig.DefaultCommandTimeoutMs"/>.</summary>
    public int? ProfileShellTimeoutMs { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>A tool definition paired with its executor.</summary>
public sealed class RegisteredTool
{
    public RegisteredTool(ToolDefinition definition, ToolExecutor executor)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public ToolDefinition Definition { get; }

    public ToolExecutor Executor { get; }

    public string Name => Definition.Name;
}

/// <summary>
/// The set of tools available to a session. Registration order is preserved; registering a name that already exists
/// replaces the earlier tool in place (latest wins).
/// </summary>
public sealed class ToolRegistry
{
    private readonly object _gate = new();
    private readonly List<RegisteredTool> _tools = [];

    public ToolRegistry()
    {
    }

    public ToolRegistry(IEnumerable<RegisteredTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools)
        {
            Register(tool);
        }
    }

    /// <summary>Adds or replaces a tool.</summary>
    public void Register(RegisteredTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_gate)
        {
            var index = _tools.FindIndex(t => t.Name == tool.Name);
            if (index >= 0)
            {
                _tools[index] = tool;
            }
            else
            {
                _tools.Add(tool);
            }
        }
    }

    /// <summary>Removes a tool; returns false when it was not registered.</summary>
    public bool Unregister(string name)
    {
        lock (_gate)
        {
            return _tools.RemoveAll(t => t.Name == name) > 0;
        }
    }

    public RegisteredTool? Get(string name)
    {
        lock (_gate)
        {
            return _tools.Find(t => t.Name == name);
        }
    }

    public bool Contains(string name) => Get(name) is not null;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tools.Count;
            }
        }
    }

    public IReadOnlyList<ToolDefinition> Definitions()
    {
        lock (_gate)
        {
            return _tools.Select(t => t.Definition).ToList();
        }
    }

    public IReadOnlyList<string> Names()
    {
        lock (_gate)
        {
            return _tools.Select(t => t.Name).ToList();
        }
    }

    public IReadOnlyList<RegisteredTool> All()
    {
        lock (_gate)
        {
            return _tools.ToList();
        }
    }

    /// <summary>A copy that can be modified independently (used for subagents).</summary>
    public ToolRegistry Clone() => new(All());
}

/// <summary>Subagent operations exposed to the subagent tools.</summary>
public interface ISubagentHost
{
    /// <summary>Spawns a subagent for <paramref name="task"/>; returns its id.</summary>
    Task<string> SpawnAsync(string task, string? workingDir, string? model, int? maxTurns, CancellationToken cancellationToken);

    /// <summary>Sends a message to a subagent (steering it if busy, or starting a new turn if idle).</summary>
    Task SendInputAsync(string agentId, string message, CancellationToken cancellationToken);

    /// <summary>Waits for a subagent's current work to finish.</summary>
    Task<SubAgentResult> WaitAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>Terminates a subagent; returns its final status.</summary>
    Task<SubAgentStatus> CloseAsync(string agentId, CancellationToken cancellationToken);
}

/// <summary>Lifecycle status of a subagent.</summary>
public enum SubAgentStatus
{
    Running,
    Completed,
    Failed,
}

/// <summary>The result of a subagent's work.</summary>
/// <param name="Output">Final text output.</param>
/// <param name="Success">Whether the subagent finished without error.</param>
/// <param name="TurnsUsed">Number of history turns the subagent used.</param>
public sealed record SubAgentResult(string Output, bool Success, int TurnsUsed);

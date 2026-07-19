using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Reactor.Llm;
using Reactor.Llm.Utilities;

namespace Reactor.Agent.Subagents;

/// <summary>A running (or finished) subagent.</summary>
public sealed class SubAgentHandle
{
    internal SubAgentHandle(string id, Session session)
    {
        Id = id;
        Session = session;
    }

    public string Id { get; }

    /// <summary>The subagent's independent session (shares the parent's execution environment).</summary>
    public Session Session { get; }

    internal Task Current { get; set; } = Task.CompletedTask;

    internal IDisposable? Forwarding { get; set; }

    public SubAgentStatus Status => Current.IsCompleted
        ? Current.IsCompletedSuccessfully && Session.State != SessionState.Closed ? SubAgentStatus.Completed : SubAgentStatus.Failed
        : SubAgentStatus.Running;
}

/// <summary>Spawns and tracks subagents for a parent session (spec section 7).</summary>
public sealed class SubAgentManager : ISubagentHost
{
    private readonly Session _parent;
    private readonly ConcurrentDictionary<string, SubAgentHandle> _agents = new(StringComparer.Ordinal);
    private int _counter;

    public SubAgentManager(Session parent)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    public IReadOnlyCollection<SubAgentHandle> Agents => _agents.Values.ToList();

    public Task<string> SpawnAsync(string task, string? workingDir, string? model, int? maxTurns, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        if (_parent.Depth >= _parent.Config.MaxSubagentDepth)
        {
            throw new InvalidOperationException($"Maximum subagent depth ({_parent.Config.MaxSubagentDepth}) reached; subagents cannot spawn further subagents.");
        }

        // Always wrap: the scope never cleans up the parent's environment when the subagent closes.
        var directory = string.IsNullOrWhiteSpace(workingDir)
            ? _parent.Environment.WorkingDirectory
            : _parent.Environment.ResolvePath(workingDir);
        IExecutionEnvironment environment = new ScopedExecutionEnvironment(_parent.Environment, directory);

        var config = _parent.Config.Clone();
        config.MaxTurns = maxTurns is > 0 ? maxTurns.Value : 0;
        var id = "agent-" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);
        var child = new Session(
            _parent.Profile.Clone(model),
            environment,
            _parent.Client,
            config,
            new SessionOptions
            {
                Id = $"{_parent.Id}/{id}",
                Depth = _parent.Depth + 1,
                Interceptors = _parent.Interceptors,
                Environment = workingDir is null ? _parent.Snapshot : null,
                ProjectDocs = workingDir is null ? _parent.ProjectDocsText : null,
            });

        var handle = new SubAgentHandle(id, child);
        handle.Forwarding = child.Events.Subscribe(evt =>
        {
            var data = new Dictionary<string, object?>(evt.Data, StringComparer.Ordinal) { ["agent_id"] = id };
            _parent.Events.Emit(evt with { Data = data });
        });
        handle.Current = RunAsync(child, task);
        _agents[id] = handle;
        return Task.FromResult(id);
    }

    public Task SendInputAsync(string agentId, string message, CancellationToken cancellationToken)
    {
        var handle = Get(agentId);
        if (handle.Session.State == SessionState.Closed)
        {
            throw new InvalidOperationException($"Subagent '{agentId}' is closed.");
        }

        if (handle.Current.IsCompleted)
        {
            handle.Current = RunAsync(handle.Session, message);
        }
        else
        {
            handle.Session.Steer(message);
        }

        return Task.CompletedTask;
    }

    public async Task<SubAgentResult> WaitAsync(string agentId, CancellationToken cancellationToken)
    {
        var handle = Get(agentId);
        var success = true;
        try
        {
            await handle.Current.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            success = false;
        }

        success &= handle.Session.State != SessionState.Closed || handle.Session.LastError is null;
        var output = handle.Session.LastAssistantText ?? "";
        if (!success && handle.Session.LastError is { } error)
        {
            output = $"{output}\n[subagent error: {error.Message}]".Trim();
        }

        return new SubAgentResult(output, success, handle.Session.AssistantTurnCount);
    }

    public async Task<SubAgentStatus> CloseAsync(string agentId, CancellationToken cancellationToken)
    {
        var handle = Get(agentId);
        var status = handle.Status;
        await handle.Session.CloseAsync().ConfigureAwait(false);
        try
        {
            await handle.Current.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The subagent was aborted; its task ends with an abort error.
        }

        handle.Forwarding?.Dispose();
        _agents.TryRemove(agentId, out _);
        return status == SubAgentStatus.Running ? SubAgentStatus.Failed : status;
    }

    /// <summary>Closes every subagent (parent shutdown).</summary>
    public async Task CloseAllAsync()
    {
        foreach (var id in _agents.Keys.ToList())
        {
            try
            {
                await CloseAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private SubAgentHandle Get(string agentId) =>
        _agents.TryGetValue(agentId, out var handle)
            ? handle
            : throw new InvalidOperationException($"Unknown subagent '{agentId}'. Known agents: {(_agents.IsEmpty ? "(none)" : string.Join(", ", _agents.Keys))}.");

    private static Task RunAsync(Session session, string input) => Task.Run(() => session.SubmitAsync(input));
}

/// <summary>The subagent tools: <c>spawn_agent</c>, <c>send_input</c>, <c>wait</c>, <c>close_agent</c>.</summary>
public static class SubagentTools
{
    public static IReadOnlyList<RegisteredTool> Create() => [SpawnAgent(), SendInput(), Wait(), CloseAgent()];

    public static RegisteredTool SpawnAgent() => new(
        new ToolDefinition(
            "spawn_agent",
            "Spawn a subagent to handle a scoped task autonomously. The subagent has its own conversation but shares this " +
            "workspace. Returns the agent id; use wait to collect its result.",
            Schema("""
                {"type":"object","properties":{
                  "task":{"type":"string","description":"Natural-language description of the task for the subagent."},
                  "working_dir":{"type":"string","description":"Optional subdirectory to scope the subagent to."},
                  "model":{"type":"string","description":"Optional model override (defaults to the current model)."},
                  "max_turns":{"type":"integer","description":"Optional turn limit (0 or omitted = unlimited)."}
                },"required":["task"]}
                """)),
        async (args, ctx) =>
        {
            var host = Host(ctx);
            var id = await host.SpawnAsync(
                args["task"]!.GetValue<string>(),
                Str(args, "working_dir"),
                Str(args, "model"),
                Int(args, "max_turns"),
                ctx.CancellationToken).ConfigureAwait(false);
            return $"Spawned subagent {id} (status: running). Call wait with agent_id=\"{id}\" to get its result.";
        });

    public static RegisteredTool SendInput() => new(
        new ToolDefinition(
            "send_input",
            "Send a message to a subagent: steers it if it is working, or starts a new turn if it is idle.",
            Schema("""
                {"type":"object","properties":{
                  "agent_id":{"type":"string"},
                  "message":{"type":"string"}
                },"required":["agent_id","message"]}
                """)),
        async (args, ctx) =>
        {
            var id = args["agent_id"]!.GetValue<string>();
            await Host(ctx).SendInputAsync(id, args["message"]!.GetValue<string>(), ctx.CancellationToken).ConfigureAwait(false);
            return $"Message delivered to {id}.";
        });

    public static RegisteredTool Wait() => new(
        new ToolDefinition(
            "wait",
            "Wait for a subagent to finish its current work and return its final output.",
            Schema("""{"type":"object","properties":{"agent_id":{"type":"string"}},"required":["agent_id"]}""")),
        async (args, ctx) =>
        {
            var id = args["agent_id"]!.GetValue<string>();
            var result = await Host(ctx).WaitAsync(id, ctx.CancellationToken).ConfigureAwait(false);
            return $"Subagent {id} finished (success: {(result.Success ? "true" : "false")}, turns used: {result.TurnsUsed}).\n\nOutput:\n{result.Output}";
        });

    public static RegisteredTool CloseAgent() => new(
        new ToolDefinition(
            "close_agent",
            "Terminate a subagent and release its resources.",
            Schema("""{"type":"object","properties":{"agent_id":{"type":"string"}},"required":["agent_id"]}""")),
        async (args, ctx) =>
        {
            var id = args["agent_id"]!.GetValue<string>();
            var status = await Host(ctx).CloseAsync(id, ctx.CancellationToken).ConfigureAwait(false);
            return $"Subagent {id} closed (final status: {status.ToString().ToLowerInvariant()}).";
        });

    private static ISubagentHost Host(ToolContext context) =>
        context.Subagents ?? throw new InvalidOperationException("Subagents are not available in this session (maximum depth reached).");

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string? Str(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetNumber(out var d) ? (int)d : null;
}

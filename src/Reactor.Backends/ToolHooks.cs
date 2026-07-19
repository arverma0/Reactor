using System.Text.Json.Nodes;
using Reactor.Agent;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Backends;

/// <summary>The <c>tool_hooks.pre</c>/<c>tool_hooks.post</c> commands in effect for one stage (spec 9.7).</summary>
public sealed record ToolHookSettings
{
    public const string PreKey = "tool_hooks.pre";
    public const string PostKey = "tool_hooks.post";

    public string? PreCommand { get; init; }

    public string? PostCommand { get; init; }

    public required string WorkingDirectory { get; init; }

    public string NodeId { get; init; } = "";

    public string RunId { get; init; } = "";

    /// <summary>Per-hook timeout (default 60 s). A timed-out pre-hook skips the tool call.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Receives stage-log messages (message, level).</summary>
    public Action<string, string>? Log { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(PreCommand) && string.IsNullOrWhiteSpace(PostCommand);

    /// <summary>Node-level hooks override graph-level hooks.</summary>
    public static ToolHookSettings Resolve(Node node, Graph? graph, StageSettings stage, Action<string, string>? log)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(stage);
        return new ToolHookSettings
        {
            PreCommand = NonEmpty(node[PreKey]) ?? NonEmpty(graph?[PreKey]),
            PostCommand = NonEmpty(node[PostKey]) ?? NonEmpty(graph?[PostKey]),
            WorkingDirectory = stage.WorkingDirectory,
            NodeId = node.Id,
            RunId = stage.RunId,
            Log = log,
        };
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Runs <c>tool_hooks.pre</c> before and <c>tool_hooks.post</c> after each agent tool call. Hooks receive metadata in
/// <c>REACTOR_*</c> environment variables and as JSON on stdin. A non-zero pre-hook exit skips the tool call; post-hook
/// failures are logged only. The settings are read through <paramref name="current"/> so a pooled session can serve
/// stages with different hooks.
/// </summary>
public sealed class ToolHookInterceptor(Func<ToolHookSettings?> current) : IToolInterceptor
{
    /// <summary>Maximum tool output passed to the post-hook on stdin.</summary>
    public const int MaxOutputChars = 64 * 1024;

    private readonly Func<ToolHookSettings?> _current = current ?? throw new ArgumentNullException(nameof(current));

    // Hooks typically append to audit logs; running them one at a time keeps parallel tool calls from racing on files.
    private readonly SemaphoreSlim _serial = new(1, 1);

    public async Task<ToolInterceptResult> BeforeAsync(ToolCallInfo call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var settings = _current();
        if (settings?.PreCommand is not { } command)
        {
            return ToolInterceptResult.Allow;
        }

        var payload = Payload("pre", call, settings);
        var result = await RunAsync(command, "pre", call, settings, payload, isError: null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0 && !result.TimedOut)
        {
            return ToolInterceptResult.Allow;
        }

        var detail = FirstNonEmpty(result.Stderr, result.Stdout);
        var reason = result.TimedOut
            ? $"pre-hook timed out after {settings.Timeout.TotalSeconds:0}s"
            : $"pre-hook exited with code {result.ExitCode}" + (detail.Length > 0 ? ": " + detail : "");
        settings.Log?.Invoke($"tool_hooks.pre blocked {call.ToolName}: {reason}", "warn");
        return ToolInterceptResult.Deny(reason);
    }

    public async Task AfterAsync(ToolCallInfo call, string output, bool isError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var settings = _current();
        if (settings?.PostCommand is not { } command)
        {
            return;
        }

        var payload = Payload("post", call, settings);
        payload["output"] = output.Length <= MaxOutputChars ? output : output[..MaxOutputChars];
        payload["is_error"] = isError;
        var result = await RunAsync(command, "post", call, settings, payload, isError, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
        {
            var detail = FirstNonEmpty(result.Stderr, result.Stdout);
            settings.Log?.Invoke(
                $"tool_hooks.post failed for {call.ToolName}: " + (result.TimedOut ? "timed out" : $"exit code {result.ExitCode}") + (detail.Length > 0 ? ": " + detail : ""),
                "warn");
        }
    }

    private static JsonObject Payload(string hook, ToolCallInfo call, ToolHookSettings settings) => new()
    {
        ["hook"] = hook,
        ["tool_name"] = call.ToolName,
        ["tool_call_id"] = call.ToolCallId,
        ["arguments"] = call.Arguments.DeepClone(),
        ["node_id"] = settings.NodeId,
        ["run_id"] = settings.RunId,
        ["session_id"] = call.SessionId,
        ["working_directory"] = call.WorkingDirectory,
    };

    private async Task<ShellResult> RunAsync(
        string command,
        string hook,
        ToolCallInfo call,
        ToolHookSettings settings,
        JsonObject payload,
        bool? isError,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REACTOR_HOOK"] = hook,
            ["REACTOR_TOOL_NAME"] = call.ToolName,
            ["REACTOR_TOOL_CALL_ID"] = call.ToolCallId,
            ["REACTOR_NODE_ID"] = settings.NodeId,
            ["REACTOR_RUN_ID"] = settings.RunId,
            ["REACTOR_SESSION_ID"] = call.SessionId,
        };
        if (isError is { } error)
        {
            environment["REACTOR_TOOL_IS_ERROR"] = error ? "true" : "false";
        }

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ShellRunner.RunAsync(command, settings.WorkingDirectory, environment, settings.Timeout, payload.ToJsonString() + "\n", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ShellResult(-1, "", $"failed to start hook: {ex.Message}", false, TimeSpan.Zero);
        }
        finally
        {
            _serial.Release();
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        LogText.Clip(values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "", 300);
}

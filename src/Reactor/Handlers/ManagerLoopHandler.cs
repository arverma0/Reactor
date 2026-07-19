using System.Globalization;
using Reactor.Conditions;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Handlers;

/// <summary>
/// Supervisor loop (spec 4.11): starts (or attaches to) a child pipeline and runs observe/steer/wait cycles until the
/// child finishes, a stop condition holds, or <c>manager.max_cycles</c> is exhausted.
/// <list type="bullet">
/// <item><c>stack.child_dotfile</c> (graph or node): child pipeline file, relative to <c>stack.child_workdir</c> or the run's working directory.</item>
/// <item><c>stack.child_autostart</c> (default true): start the child in-process; when false, observe <c>stack.child_logs_root</c>.</item>
/// <item><c>manager.poll_interval</c> (default 45s), <c>manager.max_cycles</c> (default 1000), <c>manager.stop_condition</c>,
/// <c>manager.actions</c> (default <c>observe,wait</c>; add <c>steer</c>), <c>manager.steer_cooldown</c> (default 5m),
/// <c>manager.steer_prompt</c> (defaults to the node prompt).</item>
/// </list>
/// Telemetry is written to <c>context.stack.child.*</c> keys (status, outcome, current_node, completed_stages, retries,
/// last_event, logs_root, cycles). Steering writes <c>steering.md</c> into the child's active stage directory.
/// </summary>
public sealed class ManagerLoopHandler : INodeHandler
{
    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);
        var run = context.Run;

        var pollInterval = AttributeValues.ParseDuration(node["manager.poll_interval"]) ?? TimeSpan.FromSeconds(45);
        var maxCycles = Math.Max(1, AttributeValues.ParseInt(node["manager.max_cycles"]) ?? 1000);
        var stopCondition = node["manager.stop_condition"] ?? "";
        var actions = (node["manager.actions"] ?? "observe,wait")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var autostart = AttributeValues.ParseBool(node["stack.child_autostart"]) ?? true;
        var steerCooldown = AttributeValues.ParseDuration(node["manager.steer_cooldown"]) ?? TimeSpan.FromMinutes(5);
        var steerPrompt = node["manager.steer_prompt"] ?? node.Prompt;

        ChildPipelineHandle? handle = null;
        string childLogsRoot;
        if (autostart)
        {
            var dotFile = graph[AttrKeys.StackChildDotfile] ?? node[AttrKeys.StackChildDotfile];
            if (string.IsNullOrWhiteSpace(dotFile))
            {
                return Outcome.Fail("No child pipeline configured (stack.child_dotfile).");
            }

            var workdir = node[AttrKeys.StackChildWorkdir] ?? graph[AttrKeys.StackChildWorkdir] ?? run?.WorkingDirectory ?? Directory.GetCurrentDirectory();
            workdir = Path.GetFullPath(workdir, run?.WorkingDirectory ?? Directory.GetCurrentDirectory());
            var dotPath = Path.GetFullPath(dotFile, workdir);
            if (!File.Exists(dotPath))
            {
                return Outcome.Fail($"Child pipeline file not found: {dotPath}");
            }

            if (run?.ChildLauncher is not { } launcher)
            {
                return Outcome.Fail("Child pipelines require an engine run with a child launcher.");
            }

            childLogsRoot = Path.Combine(logsRoot, node.Id, "child");
            handle = launcher.Start(dotPath, childLogsRoot, workdir, cancellationToken);
            run.Log(node.Id, $"Started child pipeline {Path.GetFileName(dotPath)} (logs: {childLogsRoot})");
        }
        else
        {
            childLogsRoot = node["stack.child_logs_root"] ?? graph["stack.child_logs_root"] ?? "";
            if (childLogsRoot.Length == 0)
            {
                return Outcome.Fail("stack.child_autostart=false requires stack.child_logs_root to observe an external child.");
            }
        }

        var lastSteer = DateTimeOffset.MinValue;
        try
        {
            for (var cycle = 1; cycle <= maxCycles; cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Completion is always detected; telemetry is only ingested into the context when "observe" is enabled.
                var (status, outcome, current, failure) = Probe(handle, childLogsRoot, context, cycle, ingest: actions.Contains("observe"));

                if (actions.Contains("steer") && status == "running" && current is not null &&
                    DateTimeOffset.UtcNow - lastSteer >= steerCooldown && !string.IsNullOrWhiteSpace(steerPrompt))
                {
                    var steeringPath = Path.Combine(childLogsRoot, current, "steering.md");
                    Directory.CreateDirectory(Path.GetDirectoryName(steeringPath)!);
                    await File.WriteAllTextAsync(steeringPath, Transforms.VariableExpansionTransform.Expand(steerPrompt, graph.Goal), cancellationToken).ConfigureAwait(false);
                    lastSteer = DateTimeOffset.UtcNow;
                    context.Set("context.stack.child.last_steer", lastSteer.ToString("O", CultureInfo.InvariantCulture));
                    run?.Log(node.Id, $"Steered child stage '{current}'");
                }

                if (status is "completed" or "failed")
                {
                    if (outcome == "success")
                    {
                        return Outcome.Success("Child completed", ChildUpdates(context));
                    }

                    return Outcome.Fail("Child failed" + (failure.Length > 0 ? ": " + failure : ""), ChildUpdates(context));
                }

                if (stopCondition.Length > 0 && ConditionEvaluator.Evaluate(stopCondition, Outcome.Success(), context))
                {
                    handle?.Cancel();
                    return Outcome.Success("Stop condition satisfied", ChildUpdates(context));
                }

                var delay = actions.Contains("wait") ? pollInterval : TimeSpan.FromMilliseconds(Math.Min(pollInterval.TotalMilliseconds, 1000));
                var waits = new List<Task> { Task.Delay(delay, cancellationToken) };
                if (handle is not null)
                {
                    waits.Add(handle.Completion);
                }

                await Task.WhenAny(waits).ConfigureAwait(false);
            }

            handle?.Cancel();
            return Outcome.Fail($"Max cycles exceeded ({maxCycles})", ChildUpdates(context));
        }
        catch (OperationCanceledException)
        {
            handle?.Cancel();
            throw;
        }
    }

    private static (string Status, string Outcome, string? Current, string Failure) Probe(
        ChildPipelineHandle? handle,
        string childLogsRoot,
        PipelineContext context,
        int cycle,
        bool ingest)
    {
        string status;
        string outcome;
        string? current;
        string failure;
        int completed;
        if (handle is not null)
        {
            status = handle.Status;
            outcome = handle.Outcome;
            current = handle.CurrentNode;
            completed = handle.CompletedStages;
            failure = handle.FailureReason;
            if (ingest)
            {
                context.Set("context.stack.child.retries", handle.Retries);
                context.Set("context.stack.child.last_event", handle.LastEvent ?? "");
            }
        }
        else
        {
            (status, outcome, current, completed, failure) = ChildPipelineHandle.ReadExternal(childLogsRoot);
        }

        if (ingest)
        {
            context.Set("context.stack.child.status", status);
            context.Set("context.stack.child.outcome", outcome);
            context.Set("context.stack.child.current_node", current ?? "");
            context.Set("context.stack.child.completed_stages", completed);
            context.Set("context.stack.child.failure_reason", failure);
            context.Set("context.stack.child.logs_root", childLogsRoot);
            context.Set("context.stack.child.cycles", cycle);
        }

        return (status, outcome, current, failure);
    }

    private static Dictionary<string, object?> ChildUpdates(PipelineContext context) => new()
    {
        ["stack.child.status"] = context.GetString("context.stack.child.status"),
        ["stack.child.outcome"] = context.GetString("context.stack.child.outcome"),
        ["stack.child.logs_root"] = context.GetString("context.stack.child.logs_root"),
    };
}

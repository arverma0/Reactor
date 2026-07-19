using System.Globalization;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Handlers;

/// <summary>
/// Runs an external command (spec 4.10) from <c>tool_command</c> in the run's working directory. Exit code 0 is SUCCESS;
/// a non-zero exit code or timeout is FAIL. Output is written to <c>stdout.txt</c>/<c>stderr.txt</c> in the stage
/// directory and exposed as <c>tool.output</c> (plus <c>tool.exit_code</c> and <c>tool.stderr</c>). The command receives
/// <c>REACTOR_RUN_ID</c>, <c>REACTOR_LOGS_ROOT</c>, <c>REACTOR_STAGE_DIR</c>, <c>REACTOR_NODE_ID</c>, and <c>REACTOR_GOAL</c>.
/// </summary>
public sealed class ToolHandler : INodeHandler
{
    /// <summary>Maximum characters of stdout stored in <c>tool.output</c>; the full output is in <c>stdout.txt</c>.</summary>
    public const int MaxContextOutput = 16 * 1024;

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);

        var command = node[AttrKeys.ToolCommand];
        if (string.IsNullOrWhiteSpace(command))
        {
            return Outcome.Fail("No tool_command specified");
        }

        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "command.txt"), command, cancellationToken).ConfigureAwait(false);

        var run = context.Run;
        var workingDirectory = run?.WorkingDirectory ?? Directory.GetCurrentDirectory();
        var timeout = node.Timeout ?? run?.RunnerOptions.DefaultToolTimeout ?? TimeSpan.FromMinutes(10);
        var environment = new Dictionary<string, string>
        {
            ["REACTOR_RUN_ID"] = run?.RunId ?? context.GetString("internal.run_id"),
            ["REACTOR_LOGS_ROOT"] = logsRoot,
            ["REACTOR_STAGE_DIR"] = stageDir,
            ["REACTOR_NODE_ID"] = node.Id,
            ["REACTOR_GOAL"] = graph.Goal,
        };

        var result = await ShellRunner.RunAsync(command, workingDirectory, environment, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "stdout.txt"), result.Stdout, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "stderr.txt"), result.Stderr, cancellationToken).ConfigureAwait(false);

        var output = result.Stdout.TrimEnd();
        var updates = new Dictionary<string, object?>
        {
            ["tool.output"] = output.Length <= MaxContextOutput ? output : output[^MaxContextOutput..],
            ["tool.exit_code"] = result.ExitCode,
            ["tool.stderr"] = Tail(result.Stderr.TrimEnd(), 4096),
            ["tool.duration_ms"] = (long)result.Duration.TotalMilliseconds,
        };

        if (result.TimedOut)
        {
            return Outcome.Fail($"Tool command timed out after {AttributeValues.FormatDuration(timeout)}: {command}", updates);
        }

        if (result.ExitCode != 0)
        {
            var detail = Tail(result.Stderr.Trim().Length > 0 ? result.Stderr.Trim() : result.Stdout.Trim(), 1500);
            return Outcome.Fail(
                string.Create(CultureInfo.InvariantCulture, $"Tool command exited with code {result.ExitCode}") + (detail.Length > 0 ? ": " + detail : ""),
                updates);
        }

        return Outcome.Success("Tool completed: " + command, updates);
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];
}

using System.Globalization;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Handlers;

/// <summary>
/// Runs an external command (spec 4.10) from <c>tool_command</c> in the run's working directory. Exit code 0 is SUCCESS;
/// a non-zero exit code or timeout is FAIL. Output is written to <c>stdout.txt</c>/<c>stderr.txt</c> in the stage
/// directory and exposed as <c>tool.output</c> (plus <c>tool.exit_code</c> and <c>tool.stderr</c>). The command receives
/// <c>REACTOR_RUN_ID</c>, <c>REACTOR_LOGS_ROOT</c>, <c>REACTOR_STAGE_DIR</c>, <c>REACTOR_NODE_ID</c>, and <c>REACTOR_GOAL</c>.
/// Artifact pins (Reactor extension): each produced input is passed as <c>REACTOR_INPUT_&lt;NAME&gt;</c> (a file path), a
/// text output records stdout, and file outputs must exist after a successful run (see <see cref="StageArtifacts"/>).
/// When the stage fails (the command failed or a declared file is missing), text outputs are still recorded, and so are
/// file outputs whose files exist.
/// <para>
/// Output is plain text: the command runs with <c>NO_COLOR=1</c>, <c>FORCE_COLOR=0</c>, and <c>TERM=dumb</c> (unless the
/// node sets <c>tool_color=true</c>), and escape sequences that remain are stripped (<see cref="AnsiText"/>) before the
/// output is recorded anywhere; when stripping changed it, the raw output is kept in <c>stdout.raw.txt</c> /
/// <c>stderr.raw.txt</c>. The failure reason of a failed command quotes the lines that explain the failure
/// (<see cref="FailureExcerpt"/>). Processes of a terminated command that survive are reported as a warning.
/// </para>
/// </summary>
public sealed class ToolHandler : INodeHandler
{
    /// <summary>Maximum characters of stdout stored in <c>tool.output</c>; the full output is in <c>stdout.txt</c>.</summary>
    public const int MaxContextOutput = 16 * 1024;

    /// <summary>
    /// Context key set to true when the command ran into the stage's time limit. The tool enforces the limit itself (so the
    /// partial output is recorded); the engine treats such a failure as a timeout for <c>on_timeout</c> and the retry's
    /// hand-off.
    /// </summary>
    public const string TimedOutKey = "tool.timed_out";

    /// <summary>Environment variables that ask tools for plain output, set unless the node has <c>tool_color=true</c>.</summary>
    private static readonly KeyValuePair<string, string>[] PlainOutputEnvironment =
    [
        new("NO_COLOR", "1"),
        new("FORCE_COLOR", "0"),
        new("TERM", "dumb"),
    ];

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
        var workingDirectory = context.WorkingDirectory;
        var timeout = node.Timeout ?? run?.RunnerOptions.DefaultToolTimeout ?? TimeSpan.FromMinutes(10);
        var environment = new Dictionary<string, string>
        {
            ["REACTOR_RUN_ID"] = run?.RunId ?? context.GetString("internal.run_id"),
            ["REACTOR_LOGS_ROOT"] = logsRoot,
            ["REACTOR_STAGE_DIR"] = stageDir,
            ["REACTOR_NODE_ID"] = node.Id,
            ["REACTOR_GOAL"] = graph.Goal,
        };
        if (AttributeValues.ParseBool(node[AttrKeys.ToolColor]) != true)
        {
            foreach (var (key, value) in PlainOutputEnvironment)
            {
                environment[key] = value;
            }
        }

        foreach (var (key, value) in await StageArtifacts.ToolInputEnvironmentAsync(node, context, stageDir, cancellationToken).ConfigureAwait(false))
        {
            environment[key] = value;
        }

        ShellResult result;
        try
        {
            result = await ShellRunner.RunAsync(command, workingDirectory, environment, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ShellCancelledException ex)
        {
            context.Run?.Log(node.Id, SurvivorsWarning("was stopped", ex.SurvivingProcessIds), "warn");
            throw;
        }

        if (result.SurvivingProcessIds.Count > 0)
        {
            context.Run?.Log(node.Id, SurvivorsWarning("timed out", result.SurvivingProcessIds), "warn");
        }

        var stdout = AnsiText.Strip(result.Stdout);
        var stderr = AnsiText.Strip(result.Stderr);
        await WriteOutputAsync(stageDir, "stdout", stdout, result.Stdout, cancellationToken).ConfigureAwait(false);
        await WriteOutputAsync(stageDir, "stderr", stderr, result.Stderr, cancellationToken).ConfigureAwait(false);

        var output = stdout.TrimEnd();
        var updates = new Dictionary<string, object?>
        {
            ["tool.output"] = output.Length <= MaxContextOutput ? output : output[^MaxContextOutput..],
            ["tool.exit_code"] = result.ExitCode,
            ["tool.stderr"] = Tail(stderr.TrimEnd(), 4096),
            ["tool.duration_ms"] = (long)result.Duration.TotalMilliseconds,
            [TimedOutKey] = result.TimedOut,
        };

        // A failing command's output is often what the next stage needs (a failing test report): record what exists.
        if (result.TimedOut)
        {
            var timedOut = Outcome.Fail($"Tool command timed out after {AttributeValues.FormatDuration(timeout)}: {command}", updates);
            return await StageArtifacts.RecordAvailableAsync(node, timedOut, context, output, cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            // Test runners report failures on stdout and wrappers (npm) add noise on stderr, so both are searched.
            var detail = FailureExcerpt.Extract(stdout, stderr, FailureExcerpt.DefaultMaxChars);
            var failed = Outcome.Fail(
                string.Create(CultureInfo.InvariantCulture, $"Tool command exited with code {result.ExitCode}") + (detail.Length > 0 ? ": " + detail : ""),
                updates);
            return await StageArtifacts.RecordAvailableAsync(node, failed, context, output, cancellationToken).ConfigureAwait(false);
        }

        var success = Outcome.Success("Tool completed: " + command, updates);
        var completed = await StageArtifacts.CompleteAsync(node, success, context, output, cancellationToken: cancellationToken).ConfigureAwait(false);

        // A declared file is missing: the stage fails, but what the command did produce is still recorded.
        return completed.Status == StageStatus.Fail
            ? await StageArtifacts.RecordAvailableAsync(node, completed, context, output, cancellationToken).ConfigureAwait(false)
            : completed;
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    // Plain text in <name>.txt; the raw capture in <name>.raw.txt when stripping changed it (a stale one from an earlier
    // attempt is removed).
    private static async Task WriteOutputAsync(string stageDir, string name, string text, string raw, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(stageDir, name + ".txt"), text, cancellationToken).ConfigureAwait(false);
        var rawPath = Path.Combine(stageDir, name + ".raw.txt");
        if (AnsiText.NeedsStripping(raw))
        {
            await File.WriteAllTextAsync(rawPath, raw, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                File.Delete(rawPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stale raw capture that cannot be removed (say, open in a viewer) is harmless.
            }
        }
    }

    private static string SurvivorsWarning(string what, IReadOnlyList<int> processIds) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The tool command {what}, but {processIds.Count} of its processes were still running after its process tree was terminated ({(processIds.Count == 1 ? "PID" : "PIDs")} {string.Join(", ", processIds)}); they may interfere with later stages in the same working directory.");
}

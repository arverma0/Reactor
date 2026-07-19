using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm;

namespace Reactor.Agent.Tools;

/// <summary><c>shell</c>: runs a command through the environment with the effective timeout, streaming output.</summary>
internal static class ShellTool
{
    public const string Name = "shell";

    private const string Description =
        "Run a command in the system shell and return its standard output, standard error, exit code, and duration. " +
        "Commands run in the working directory with a timeout (override it with `timeout_ms` for long-running commands " +
        "such as builds or test suites); when the timeout expires the whole process tree is terminated and the partial " +
        "output is returned. Commands cannot read interactive input (stdin is closed), so pass non-interactive flags. " +
        "A non-zero exit code is reported in the output, not as a failure. Prefer the dedicated tools for reading, " +
        "searching, and editing files.";

    public static RegisteredTool Create() => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("command", ToolSchema.String("The command to run.")),
                ("timeout_ms", ToolSchema.Integer("Timeout in milliseconds. Defaults to the session's command timeout; capped at the session maximum.")),
                ("description", ToolSchema.String("A short human-readable description of what the command does.")),
            ],
            "command")),
        ExecuteAsync);

    /// <summary>The timeout used for a call: explicit, else the profile default, else the session default; clamped to [1, max].</summary>
    public static int EffectiveTimeout(int? requested, ToolContext context)
    {
        var maximum = Math.Max(1, context.Config.MaxCommandTimeoutMs);
        var timeout = requested ?? context.ProfileShellTimeoutMs ?? context.Config.DefaultCommandTimeoutMs;
        return Math.Clamp(timeout, 1, maximum);
    }

    /// <summary>Formats a command result for the model.</summary>
    public static string Format(ExecResult result, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = new StringBuilder(result.Stdout);
        if (result.Stderr.Length > 0)
        {
            output.Append("\n[stderr]\n").Append(result.Stderr);
        }

        output.Append("\n[exit code: ").Append(result.ExitCode.ToString(CultureInfo.InvariantCulture))
            .Append(", duration: ").Append(result.DurationMs.ToString(CultureInfo.InvariantCulture)).Append("ms]");
        if (result.TimedOut)
        {
            output.Append("\n[ERROR: Command timed out after ").Append(timeoutMs.ToString(CultureInfo.InvariantCulture))
                .Append("ms. Partial output is shown above.\nYou can retry with a longer timeout by setting the timeout_ms parameter.]");
        }

        return output.ToString();
    }

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context)
    {
        var command = ToolArguments.RequireString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("command must not be empty.");
        }

        var timeout = EffectiveTimeout(ToolArguments.OptionalInt(arguments, "timeout_ms"), context);
        var result = await context.Environment.ExecCommandAsync(
            command, timeout, workingDir: null, envVars: null, context.OnOutputDelta, context.CancellationToken).ConfigureAwait(false);
        return Format(result, timeout);
    }
}

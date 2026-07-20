using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Reactor.Backends.Cli;

/// <summary>A resolved executable plus arguments that precede the caller's arguments.</summary>
/// <param name="FileName">The program to start.</param>
/// <param name="PrefixArguments">Arguments inserted before the caller's (e.g. the script for an npm shim).</param>
/// <param name="ViaCmd">True when the target is a batch file that must run through <c>cmd.exe</c>.</param>
public sealed record ResolvedCommand(string FileName, IReadOnlyList<string> PrefixArguments, bool ViaCmd = false)
{
    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(PrefixArguments));
}

/// <summary>
/// Finds CLI executables on PATH. On Windows it honors PATHEXT and unwraps npm <c>.cmd</c> shims to run
/// <c>node script.js</c> directly (avoiding <c>cmd.exe</c> argument re-parsing); other batch files run via <c>cmd.exe</c>.
/// </summary>
public static partial class CommandResolver
{
    /// <summary>Resolves <paramref name="command"/> (a name on PATH or a path), or returns null when not found.</summary>
    public static ResolvedCommand? Resolve(string command, string? pathVariable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var file = FindFile(command, pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "");
        if (file is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows() && Path.GetExtension(file).ToLowerInvariant() is ".cmd" or ".bat")
        {
            return TryUnwrapNpmShim(file, pathVariable) ?? new ResolvedCommand(file, [], ViaCmd: true);
        }

        return new ResolvedCommand(file, []);
    }

    /// <summary>Builds a start info for <paramref name="command"/> with <paramref name="arguments"/>.</summary>
    public static ProcessStartInfo CreateStartInfo(ResolvedCommand command, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(arguments);
        var all = command.PrefixArguments.Concat(arguments).ToList();
        ProcessStartInfo startInfo;
        if (command.ViaCmd)
        {
            // cmd.exe /s /c strips the outer quotes and runs the rest verbatim.
            var line = string.Join(' ', new[] { Quote(command.FileName) }.Concat(all.Select(Quote)));
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { Arguments = $"/d /s /c \"{line}\"" };
        }
        else
        {
            startInfo = new ProcessStartInfo(command.FileName);
            foreach (var argument in all)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    internal static ResolvedCommand? TryUnwrapNpmShim(string shimPath, string? pathVariable = null)
    {
        string content;
        try
        {
            content = File.ReadAllText(shimPath);
        }
        catch (IOException)
        {
            return null;
        }

        var match = NpmShimScript().Match(content);
        if (!match.Success)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(shimPath))!;
        var script = Path.GetFullPath(Path.Combine(directory, match.Groups["script"].Value.Replace('\\', Path.DirectorySeparatorChar)));
        if (!File.Exists(script))
        {
            return null;
        }

        var localNode = Path.Combine(directory, "node.exe");
        var node = File.Exists(localNode) ? localNode : FindFile("node", pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "");
        return node is null ? null : new ResolvedCommand(node, [script]);
    }

    private static string? FindFile(string command, string pathVariable)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];

        IEnumerable<string> Candidates(string basePath)
        {
            if (Path.HasExtension(basePath) || extensions.Length == 0)
            {
                yield return basePath;
            }

            foreach (var extension in extensions)
            {
                yield return basePath + extension.ToLowerInvariant();
            }
        }

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar) || Path.IsPathRooted(command))
        {
            return Candidates(Path.GetFullPath(command)).FirstOrDefault(File.Exists);
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string basePath;
            try
            {
                basePath = Path.Combine(directory.Trim('"'), command);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var found = Candidates(basePath).FirstOrDefault(File.Exists);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')']) < 0 ? argument : "\"" + argument.Replace("\"", "\"\"") + "\"";

    [GeneratedRegex(@"""%dp0%\\(?<script>[^""]+?\.(?:js|mjs|cjs))""", RegexOptions.IgnoreCase)]
    private static partial Regex NpmShimScript();
}

/// <summary>Result of <see cref="CliProcess.RunAsync"/>.</summary>
public sealed record CliProcessResult(int ExitCode, string Stderr, TimeSpan Duration);

/// <summary>Runs a CLI agent: writes the prompt to stdin, streams stdout lines to a callback, captures stderr.</summary>
public static class CliProcess
{
    /// <summary>Maximum stderr characters kept.</summary>
    public const int MaxStderrChars = 256 * 1024;

    /// <exception cref="CliAgentException">The process could not be started.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process tree is killed first.</exception>
    public static async Task<CliProcessResult> RunAsync(
        ResolvedCommand command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? stdin,
        IReadOnlyDictionary<string, string?>? environment,
        Action<string> onStdoutLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(onStdoutLine);
        var startInfo = CommandResolver.CreateStartInfo(command, arguments);
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardInputEncoding = utf8;
        startInfo.StandardOutputEncoding = utf8;
        startInfo.StandardErrorEncoding = utf8;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new CliAgentException($"Failed to start '{command}'.", retryable: false);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CliAgentException($"Failed to start '{command}': {ex.Message}", retryable: false, ex);
        }

        using var registration = cancellationToken.Register(() => Kill(process));
        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    try
                    {
                        onStdoutLine(line);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // A faulty consumer must not break output draining.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The process was disposed while a grandchild still held the pipe open.
            }
        }, CancellationToken.None);
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                int read;
                while ((read = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    lock (stderr)
                    {
                        if (stderr.Length < MaxStderrChars)
                        {
                            stderr.Append(buffer, 0, Math.Min(read, MaxStderrChars - stderr.Length));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }, CancellationToken.None);

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The process exited without reading all of its input; its output explains why.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        // Drain remaining output, bounded in case grandchildren keep the pipes open.
        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
        stopwatch.Stop();
        string stderrText;
        lock (stderr)
        {
            stderrText = stderr.ToString();
        }

        return new CliProcessResult(process.ExitCode, stderrText, stopwatch.Elapsed);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}

/// <summary>A CLI agent failed. <see cref="Retryable"/> tells the engine whether a retry may help.</summary>
public sealed class CliAgentException(string message, bool retryable = true, Exception? innerException = null) : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;

    /// <summary>Classifies CLI error text: authentication, permission, configuration and model errors are permanent.</summary>
    public static bool IsRetryableMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        var text = message.ToLowerInvariant();
        string[] permanent =
        [
            "not logged in", "/login", "login required", "authenticat", "unauthorized", "forbidden", "invalid api key",
            "permission denied", "not authorized", "subscription", "not available", "unknown model", "invalid model",
            "model not found", "is not supported", "unknown option", "unknown argument", "invalid value", "credit balance",
            "quota", "budget",
        ];
        return !permanent.Any(text.Contains);
    }
}

using System.Diagnostics;
using System.Text;

namespace Reactor.Execution;

/// <summary>Result of <see cref="ShellRunner.RunAsync"/>.</summary>
public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, TimeSpan Duration);

/// <summary>
/// Runs shell commands for tool nodes and hooks: <c>cmd.exe /d /s /c</c> on Windows, <c>/bin/bash -c</c> elsewhere
/// (falling back to <c>/bin/sh</c>). Timeouts and cancellation terminate the whole process tree.
/// </summary>
public static class ShellRunner
{
    /// <summary>Maximum characters captured per stream; further output is dropped with a marker.</summary>
    public const int MaxCapturedChars = 4 * 1024 * 1024;

    public static async Task<ShellResult> RunAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        string? stdin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var startInfo = CreateStartInfo(command, workingDirectory);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new BoundedBuffer();
        var stderr = new BoundedBuffer();
        process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The process may exit without reading its input.
        }
        finally
        {
            process.StandardInput.Close();
        }

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t && t > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(t);
        }

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        // Drain redirected output (bounded, in case orphaned grandchildren keep pipes open).
        try
        {
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        stopwatch.Stop();
        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new ShellResult(timedOut ? -1 : exitCode, stdout.ToString(), stderr.ToString(), timedOut, stopwatch.Elapsed);
    }

    private static ProcessStartInfo CreateStartInfo(string command, string workingDirectory)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" };
        }
        else
        {
            var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            startInfo = new ProcessStartInfo(shell);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        return startInfo;
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between the check and the kill, or cannot be signalled.
        }
    }

    private sealed class BoundedBuffer
    {
        private readonly StringBuilder _sb = new();
        private bool _truncated;

        public void AppendLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_sb)
            {
                if (_sb.Length + line.Length + 1 > MaxCapturedChars)
                {
                    _truncated = true;
                    return;
                }

                _sb.Append(line).Append('\n');
            }
        }

        public override string ToString()
        {
            lock (_sb)
            {
                return _truncated ? _sb + "[... output truncated ...]\n" : _sb.ToString();
            }
        }
    }
}

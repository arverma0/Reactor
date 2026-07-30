using System.Diagnostics;
using System.Text;
using Reactor.Processes;

namespace Reactor.Execution;

/// <summary>Result of <see cref="ShellRunner.RunAsync"/>.</summary>
/// <param name="ExitCode">The shell's exit code; -1 when the command timed out.</param>
/// <param name="Stdout">
/// Standard output as the command wrote it (line endings, escape sequences, and progress carriage returns included;
/// <see cref="AnsiText.Strip"/> turns it into plain text), capped at <see cref="ShellRunner.MaxCapturedChars"/>.
/// </param>
/// <param name="Stderr">Standard error, captured like <paramref name="Stdout"/>.</param>
/// <param name="TimedOut">True when the timeout expired and the command's process tree was terminated.</param>
/// <param name="Duration">How long the command ran, including the time spent draining its output.</param>
public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, TimeSpan Duration)
{
    /// <summary>
    /// Processes of a timed-out command's tree that were still running after it was terminated (normally none). They
    /// may race whatever runs next in the same working directory.
    /// </summary>
    public IReadOnlyList<int> SurvivingProcessIds { get; init; } = [];
}

/// <summary>
/// Thrown by <see cref="ShellRunner.RunAsync"/> instead of the usual <see cref="OperationCanceledException"/> when the
/// run was cancelled and processes of the command's tree were still running after it was terminated.
/// </summary>
public sealed class ShellCancelledException : OperationCanceledException
{
    public ShellCancelledException(IReadOnlyList<int> survivingProcessIds, Exception? innerException, CancellationToken cancellationToken)
        : base("The command was cancelled, but some of its processes could not be terminated.", innerException, cancellationToken)
    {
        SurvivingProcessIds = survivingProcessIds ?? [];
    }

    /// <summary>The processes still running after the command's process tree was terminated.</summary>
    public IReadOnlyList<int> SurvivingProcessIds { get; }
}

/// <summary>
/// Runs shell commands for tool nodes and hooks: <c>cmd.exe /d /s /c</c> on Windows, <c>/bin/bash -c</c> elsewhere
/// (falling back to <c>/bin/sh</c>). Timeouts and cancellation terminate the command's whole process tree, including
/// descendants whose parent already exited: on Windows the shell runs in a job object, on Unix in a session and process
/// group of its own (through <c>setsid</c> when available), which gets <c>SIGTERM</c>, a short grace period, then
/// <c>SIGKILL</c>. When the command exits by itself, processes it deliberately left running in the background keep
/// running; its output is then drained for at most a few seconds, since such processes may hold the pipes open.
/// </summary>
public static class ShellRunner
{
    /// <summary>Maximum characters captured per stream; further output is dropped with a marker.</summary>
    public const int MaxCapturedChars = 4 * 1024 * 1024;

    /// <summary>How long a terminated process tree gets to exit after <c>SIGTERM</c> (Unix) before it is killed.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for the output pipes to close after the shell exits.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled; the process tree was terminated first. A
    /// <see cref="ShellCancelledException"/> reports processes that survived.
    /// </exception>
    public static async Task<ShellResult> RunAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        string? stdin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(command, workingDirectory, out var ownsProcessGroup);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        var tree = ProcessTreeGuard.Attach(process, ownsProcessGroup);
        var stdout = new OutputPump(process.StandardOutput);
        var stderr = new OutputPump(process.StandardError);
        var exited = false;
        var terminated = false;
        var timedOut = false;
        var exitCode = -1;
        IReadOnlyList<int> survivors = [];
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t && t > TimeSpan.Zero)
            {
                timeoutCts.CancelAfter(t);
            }

            try
            {
                if (stdin is not null)
                {
                    try
                    {
                        await process.StandardInput.WriteAsync(stdin.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // The process may exit without reading its input.
                    }
                }

                CloseQuietly(process.StandardInput);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                exitCode = process.ExitCode;
                exited = true;
            }
            catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
            {
                terminated = true;
                survivors = await tree.TerminateAsync(GracePeriod).ConfigureAwait(false);
                CloseQuietly(process.StandardInput);
                if (cancellationToken.IsCancellationRequested)
                {
                    if (survivors.Count > 0)
                    {
                        throw new ShellCancelledException(survivors, ex, cancellationToken);
                    }

                    throw;
                }

                timedOut = true;
            }
        }
        finally
        {
            if (exited)
            {
                tree.Release();
            }
            else if (!terminated)
            {
                await tree.TerminateAsync(GracePeriod).ConfigureAwait(false);
            }

            tree.Dispose();

            // The pumps may stay blocked on pipes held open by processes the command left running: release the process
            // object only once they finish.
            _ = Task.WhenAll(stdout.Completion, stderr.Completion).ContinueWith(
                static (_, state) => ((Process)state!).Dispose(),
                process,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        await Task.WhenAny(Task.WhenAll(stdout.Completion, stderr.Completion), Task.Delay(DrainTimeout, CancellationToken.None))
            .ConfigureAwait(false);
        stopwatch.Stop();
        return new ShellResult(exitCode, stdout.Close(), stderr.Close(), timedOut, stopwatch.Elapsed) { SurvivingProcessIds = survivors };
    }

    private static ProcessStartInfo CreateStartInfo(string command, string workingDirectory, out bool ownsProcessGroup)
    {
        ProcessStartInfo startInfo;
        ownsProcessGroup = false;
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
            ownsProcessGroup = PosixProcesses.TryStartInNewSession(startInfo);
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

    private static void CloseQuietly(StreamWriter writer)
    {
        try
        {
            writer.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The process exited (or was terminated) without reading its input.
        }
    }

    /// <summary>
    /// Reads one output stream verbatim on a dedicated thread (process pipes are not truly asynchronous on every platform,
    /// and long-running commands would otherwise block thread-pool threads), keeping up to <see cref="MaxCapturedChars"/>.
    /// </summary>
    private sealed class OutputPump
    {
        private readonly StringBuilder _text = new();
        private bool _truncated;
        private bool _closed;

        public OutputPump(StreamReader reader) =>
            Completion = Task.Factory.StartNew(
                () => Pump(reader),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

        /// <summary>Completes when the stream reaches its end (every process holding the pipe has exited).</summary>
        public Task Completion { get; }

        /// <summary>Stops capturing (later output is read and discarded) and returns what was captured.</summary>
        public string Close()
        {
            lock (_text)
            {
                _closed = true;
                return _truncated ? _text + "\n[... output truncated ...]\n" : _text.ToString();
            }
        }

        private void Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (_text)
                    {
                        // Keep draining after Close() or the cap, so writers never block on a full pipe.
                        if (_closed || _truncated)
                        {
                            continue;
                        }

                        var room = MaxCapturedChars - _text.Length;
                        if (read > room)
                        {
                            _text.Append(buffer, 0, room);
                            _truncated = true;
                        }
                        else
                        {
                            _text.Append(buffer, 0, read);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The pipe broke; whatever was captured stands.
            }
        }
    }
}
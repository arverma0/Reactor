using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Reactor.Agent.Environments;

/// <summary>
/// Runs a shell process with captured output, a timeout, cancellation, and process-tree termination
/// (graceful signal, a 2 second grace period, then a forced kill).
/// </summary>
internal static class ProcessRunner
{
    /// <summary>How long a terminated process tree gets to exit after the graceful signal.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to wait for the output pipes to close after the shell exits. Background processes that inherited the pipes
    /// can keep them open indefinitely, so the wait is bounded.
    /// </summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    public static async Task<ExecResult> RunAsync(
        ProcessStartInfo startInfo,
        int timeoutMs,
        bool ownsProcessGroup,
        Action<string>? onOutput,
        ProcessTracker tracker,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = TextFileDecoding.Utf8;
        startInfo.StandardErrorEncoding = TextFileDecoding.Utf8;

        var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start '{startInfo.FileName}': {ex.Message}", ex);
        }

        var capture = new OutputCapture(onOutput);
        capture.Start(process.StandardOutput, process.StandardError);
        var running = new RunningProcess(process, ownsProcessGroup, capture.Completion);
        tracker.Add(running);
        try
        {
            // Commands that wait for input get EOF instead of hanging until the timeout.
            CloseQuietly(process.StandardInput);

            var timedOut = false;
            using (var timeout = new CancellationTokenSource(timeoutMs))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken))
            {
                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    timedOut = !cancellationToken.IsCancellationRequested;
                    await running.TerminateAsync().ConfigureAwait(false);
                }
            }

            await capture.WaitForCompletionAsync(DrainTimeout).ConfigureAwait(false);
            stopwatch.Stop();
            var (stdout, stderr) = capture.Close();
            cancellationToken.ThrowIfCancellationRequested();
            return new ExecResult
            {
                Stdout = stdout,
                Stderr = stderr,
                ExitCode = running.ExitCodeOrDefault(),
                TimedOut = timedOut,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        finally
        {
            tracker.Remove(running);

            // The output pumps may still be blocked on pipes held open by orphaned background processes, and a concurrent
            // CleanupAsync may still be terminating the tree; release the process object only once both are done.
            var termination = running.MarkFinished();
            _ = Task.WhenAll(capture.Completion, termination).ContinueWith(
                static (_, state) => ((Process)state!).Dispose(),
                process,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static void CloseQuietly(StreamWriter writer)
    {
        try
        {
            writer.Close();
        }
        catch (IOException)
        {
            // The process may already have exited and closed its end of the pipe.
        }
    }
}

/// <summary>Captures stdout and stderr on dedicated threads, streaming chunks to an observer.</summary>
internal sealed class OutputCapture
{
    /// <summary>Per-stream capture cap (head and tail are kept) protecting the host from runaway output.</summary>
    public const int MaxCapturedChars = 8_000_000;

    private readonly object _gate = new();
    private readonly CappedTextBuffer _stdout = new(MaxCapturedChars);
    private readonly CappedTextBuffer _stderr = new(MaxCapturedChars);
    private readonly Action<string>? _onOutput;
    private bool _closed;

    public OutputCapture(Action<string>? onOutput) => _onOutput = onOutput;

    /// <summary>Completes when both output pipes have closed.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start(StreamReader stdout, StreamReader stderr) =>
        Completion = Task.WhenAll(StartPump(stdout, _stdout), StartPump(stderr, _stderr));

    public async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        try
        {
            await Completion.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Orphaned background processes still hold the pipes; return what has been captured so far.
        }
    }

    /// <summary>Stops delivering output and returns everything captured so far.</summary>
    public (string Stdout, string Stderr) Close()
    {
        lock (_gate)
        {
            _closed = true;
            return (_stdout.ToString(), _stderr.ToString());
        }
    }

    // Synchronous reads on dedicated threads: process pipes are not truly asynchronous on every platform, and blocking
    // thread-pool threads for the lifetime of long-running commands would starve the pool.
    private Task StartPump(StreamReader reader, CappedTextBuffer target) =>
        Task.Factory.StartNew(
            () => Pump(reader, target),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private void Pump(StreamReader reader, CappedTextBuffer target)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                lock (_gate)
                {
                    // After Close() keep draining (and discarding) so writers never block on a full pipe.
                    if (_closed)
                    {
                        continue;
                    }

                    target.Append(buffer.AsSpan(0, read));
                    if (_onOutput is not null)
                    {
                        try
                        {
                            _onOutput(new string(buffer, 0, read));
                        }
                        catch (Exception)
                        {
                            // A failing observer must not stop output capture.
                        }
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

/// <summary>A text buffer that keeps the first and last halves of its capacity once full.</summary>
internal sealed class CappedTextBuffer
{
    private readonly int _headCapacity;
    private readonly int _tailCapacity;
    private readonly StringBuilder _head = new();
    private char[]? _tail;
    private int _tailStart;
    private int _tailLength;
    private long _dropped;

    public CappedTextBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _headCapacity = capacity / 2;
        _tailCapacity = capacity - _headCapacity;
    }

    public void Append(ReadOnlySpan<char> text)
    {
        var toHead = Math.Min(text.Length, _headCapacity - _head.Length);
        if (toHead > 0)
        {
            _head.Append(text[..toHead]);
            text = text[toHead..];
        }

        if (text.IsEmpty)
        {
            return;
        }

        _tail ??= new char[_tailCapacity];
        if (text.Length >= _tailCapacity)
        {
            _dropped += _tailLength + (text.Length - _tailCapacity);
            text[^_tailCapacity..].CopyTo(_tail);
            _tailStart = 0;
            _tailLength = _tailCapacity;
            return;
        }

        var overflow = _tailLength + text.Length - _tailCapacity;
        if (overflow > 0)
        {
            _tailStart = (_tailStart + overflow) % _tailCapacity;
            _tailLength -= overflow;
            _dropped += overflow;
        }

        var writeAt = (_tailStart + _tailLength) % _tailCapacity;
        var first = Math.Min(text.Length, _tailCapacity - writeAt);
        text[..first].CopyTo(_tail.AsSpan(writeAt));
        text[first..].CopyTo(_tail.AsSpan(0));
        _tailLength += text.Length;
    }

    public override string ToString()
    {
        var sb = new StringBuilder(_head.Length + _tailLength + 96);
        sb.Append(_head);
        if (_dropped > 0)
        {
            sb.Append("\n[... ").Append(_dropped.ToString(CultureInfo.InvariantCulture))
                .Append(" characters of output were not captured ...]\n");
        }

        if (_tail is not null && _tailLength > 0)
        {
            var first = Math.Min(_tailLength, _tailCapacity - _tailStart);
            sb.Append(_tail, _tailStart, first);
            sb.Append(_tail, 0, _tailLength - first);
        }

        return sb.ToString();
    }
}

/// <summary>A started shell process that can be terminated as a tree.</summary>
internal sealed class RunningProcess
{
    private readonly Process _process;
    private readonly bool _ownsProcessGroup;
    private readonly Task _outputClosed;
    private readonly object _gate = new();
    private Task? _termination;
    private bool _finished;

    public RunningProcess(Process process, bool ownsProcessGroup, Task outputClosed)
    {
        _process = process;
        _ownsProcessGroup = ownsProcessGroup;
        _outputClosed = outputClosed;
        Id = process.Id;
    }

    public int Id { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }
    }

    public int ExitCodeOrDefault()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return -1;
        }
    }

    /// <summary>Terminates the process tree: graceful signal, up to 2 seconds of grace, then a forced kill. Idempotent.</summary>
    public Task TerminateAsync()
    {
        lock (_gate)
        {
            if (_finished)
            {
                return _termination ?? Task.CompletedTask;
            }

            // Run on the thread pool so no work happens under the lock or on the caller's synchronization context.
            return _termination ??= Task.Run(TerminateCoreAsync);
        }
    }

    /// <summary>
    /// Marks the run as finished so later termination requests are no-ops; returns any termination still in progress,
    /// which must complete before the process object is disposed.
    /// </summary>
    public Task MarkFinished()
    {
        lock (_gate)
        {
            _finished = true;
            return _termination ?? Task.CompletedTask;
        }
    }

    private async Task TerminateCoreAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            await SignalWindowsTreeAsync().ConfigureAwait(false);
        }
        else
        {
            await SignalUnixTreeAsync().ConfigureAwait(false);
        }

        ForceKill();
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // Nothing more can be done; the caller reports what it has.
        }
    }

    private async Task SignalWindowsTreeAsync()
    {
        if (HasExited)
        {
            return;
        }

        // taskkill without /F asks every process in the tree to close. Console programs cannot be closed that way and
        // taskkill then reports failure, in which case there is no graceful shutdown to wait for.
        var (exitCode, _) = await HelperProcess.RunAsync("taskkill", ["/PID", Id.ToString(CultureInfo.InvariantCulture), "/T"])
            .ConfigureAwait(false);
        if (exitCode == 0)
        {
            await WaitForExitAndOutputAsync().ConfigureAwait(false);
        }
    }

    private async Task SignalUnixTreeAsync()
    {
        var pid = Id.ToString(CultureInfo.InvariantCulture);
        var stopwatch = Stopwatch.StartNew();
        if (_ownsProcessGroup)
        {
            // The shell was started through setsid, so its pid is also the id of a process group holding the whole tree.
            var group = "-" + pid;
            await SendSignalAsync("TERM", [group]).ConfigureAwait(false);
            await WaitForExitAndOutputAsync().ConfigureAwait(false);
            if (HasExited && _outputClosed.IsCompleted)
            {
                // Members that redirected their output do not hold our pipes, so ask whether the group still exists.
                var (stillAlive, _) = await HelperProcess.RunAsync("/bin/sh", ["-c", $"kill -0 -- {group} 2>/dev/null"]).ConfigureAwait(false);
                if (stillAlive != 0)
                {
                    return;
                }

                var remaining = ProcessRunner.GracePeriod - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining).ConfigureAwait(false);
                }
            }

            await SendSignalAsync("KILL", [group]).ConfigureAwait(false);
            return;
        }

        var targets = new List<string> { pid };
        targets.AddRange(await GetDescendantsAsync(Id).ConfigureAwait(false));
        await SendSignalAsync("TERM", targets).ConfigureAwait(false);
        await WaitForExitAndOutputAsync().ConfigureAwait(false);
        if (HasExited && _outputClosed.IsCompleted)
        {
            return;
        }

        if (!HasExited)
        {
            targets.AddRange(await GetDescendantsAsync(Id).ConfigureAwait(false));
        }

        await SendSignalAsync("KILL", targets.Distinct(StringComparer.Ordinal).ToList()).ConfigureAwait(false);
    }

    private async Task WaitForExitAndOutputAsync()
    {
        try
        {
            await Task.WhenAll(_process.WaitForExitAsync(), _outputClosed).WaitAsync(ProcessRunner.GracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Grace period over.
        }
    }

    private void ForceKill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already exited, or the tree could only be partially killed.
        }
    }

    // POSIX spelling "kill -s SIG -- -PGID", run through the shell's builtin to avoid differences between kill binaries.
    private static Task SendSignalAsync(string signal, IReadOnlyList<string> targets) =>
        targets.Count == 0
            ? Task.CompletedTask
            : HelperProcess.RunAsync("/bin/sh", ["-c", $"kill -s {signal} -- {string.Join(' ', targets)}"]);

    private static async Task<IReadOnlyList<string>> GetDescendantsAsync(int rootPid)
    {
        var (exitCode, output) = await HelperProcess.RunAsync("ps", ["-A", "-o", "pid=", "-o", "ppid="]).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return [];
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(pid);
            }
        }

        var result = new List<string>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        var seen = new HashSet<int> { rootPid };
        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var list))
            {
                continue;
            }

            foreach (var child in list)
            {
                if (seen.Add(child))
                {
                    result.Add(child.ToString(CultureInfo.InvariantCulture));
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }
}

/// <summary>Runs short helper commands (taskkill, kill, ps) quietly with a bounded wait.</summary>
internal static class HelperProcess
{
    public static async Task<(int? ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (null, "");
            }

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Best effort.
                }

                return (null, "");
            }

            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (null, "");
        }
    }
}

/// <summary>The set of commands currently running in an environment, so cleanup can terminate them.</summary>
internal sealed class ProcessTracker
{
    private readonly ConcurrentDictionary<RunningProcess, byte> _running = new();

    public int Count => _running.Count;

    public void Add(RunningProcess process) => _running.TryAdd(process, 0);

    public void Remove(RunningProcess process) => _running.TryRemove(process, out _);

    public Task TerminateAllAsync() => Task.WhenAll(_running.Keys.Select(p => p.TerminateAsync()));
}

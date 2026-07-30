using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Reactor.Processes;

/// <summary>
/// Keeps a started process together with every process it starts, so the whole tree can be terminated even after
/// intermediate processes have exited. Orphaned grandchildren (say <c>npm</c> → <c>cmd.exe</c> → <c>node</c>, once the
/// <c>cmd.exe</c> in the middle is gone) are exactly what a parent-PID walk such as
/// <c>Process.Kill(entireProcessTree: true)</c> misses.
/// <list type="bullet">
/// <item>Windows: the process is put in a <see cref="WindowsJobObject"/> right after it starts, and descendants it started
/// in the meantime are swept in. The job is kill-on-close, so if this process dies while the command runs, the system
/// ends the tree.</item>
/// <item>Unix: the process should have been started through <c>setsid</c>
/// (<see cref="PosixProcesses.TryStartInNewSession"/>, then pass <c>ownsProcessGroup: true</c>), so its process group
/// holds the tree; it is sent <c>SIGTERM</c>, given a grace period, then <c>SIGKILL</c>.</item>
/// </list>
/// Every failure falls back to the parent-PID walk: containment never keeps a command from running.
/// <para>
/// Lifecycle: call <see cref="TerminateAsync"/> on cancellation or timeout, or <see cref="Release"/> when the command
/// finished normally (processes it deliberately left running, such as a background server, keep running), then
/// <see cref="Dispose"/>. On Windows, disposing without either terminates whatever is left in the job.
/// </para>
/// </summary>
internal sealed class ProcessTreeGuard : IDisposable
{
    /// <summary>How long <see cref="TerminateAsync"/> waits for the terminated processes to disappear.</summary>
    public static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PosixPollInterval = TimeSpan.FromMilliseconds(150);

    private readonly Process _process;
    private readonly int _rootId;
    private readonly bool _ownsProcessGroup;
    private readonly object _gate = new();
    private WindowsJobObject? _job;
    private ProcessIdentity? _root;
    private bool _released;
    private bool _disposed;

    private ProcessTreeGuard(Process process, bool ownsProcessGroup)
    {
        _process = process;
        _rootId = process.Id;
        _ownsProcessGroup = ownsProcessGroup;
    }

    /// <summary>
    /// True when the tree is contained: in a job object (Windows) or a process group of its own (Unix). Otherwise
    /// termination falls back to the parent-PID walk.
    /// </summary>
    public bool IsContained
    {
        get
        {
            lock (_gate)
            {
                return _job is not null || _ownsProcessGroup;
            }
        }
    }

    /// <summary>
    /// Starts guarding <paramref name="process"/>, which must have been started already. Call it immediately after
    /// <see cref="Process.Start()"/>. <paramref name="ownsProcessGroup"/> (Unix) tells that the process leads its own
    /// process group (it was started through <c>setsid</c>).
    /// </summary>
    public static ProcessTreeGuard Attach(Process process, bool ownsProcessGroup = false)
    {
        ArgumentNullException.ThrowIfNull(process);
        var guard = new ProcessTreeGuard(process, ownsProcessGroup && !OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows())
        {
            guard.AttachJob();
        }

        return guard;
    }

    /// <summary>
    /// Terminates the process and all of its descendants, then waits up to <paramref name="verifyTimeout"/> (default
    /// <see cref="DefaultVerifyTimeout"/>) for them to disappear. On Unix the tree first gets <c>SIGTERM</c> and up to
    /// <paramref name="gracePeriod"/> to exit; Windows has no equivalent for console programs, so the tree is terminated
    /// at once. Returns the ids of processes that are still running afterwards (empty when everything is gone or when
    /// that cannot be determined).
    /// </summary>
    public Task<IReadOnlyList<int>> TerminateAsync(TimeSpan gracePeriod, TimeSpan? verifyTimeout = null)
    {
        var verify = verifyTimeout ?? DefaultVerifyTimeout;
        return OperatingSystem.IsWindows() ? TerminateWindowsAsync(verify) : TerminatePosixAsync(gracePeriod, verify);
    }

    /// <summary>
    /// The command finished normally: stop guarding without terminating anything, so processes it left running in the
    /// background keep running (as they would without the guard). Idempotent.
    /// </summary>
    public void Release()
    {
        WindowsJobObject? job;
        lock (_gate)
        {
            if (_released || _disposed)
            {
                return;
            }

            _released = true;
            job = _job;
            _job = null;
        }

        if (job is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (job.TryDetach() || job.GetProcessIds() is { Count: 0 })
            {
                job.Dispose();
            }
            else
            {
                // Closing a kill-on-close job would end the processes the command left running. Keep the handle open
                // instead: they then live until this process exits.
                job.Abandon();
            }
        }
        catch (Exception ex) when (IsContainmentFailure(ex))
        {
            job.Abandon();
        }
    }

    /// <summary>Releases the job handle; on Windows, processes still in the job are terminated unless released first.</summary>
    public void Dispose()
    {
        WindowsJobObject? job;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            job = _job;
            _job = null;
        }

        if (OperatingSystem.IsWindows())
        {
            job?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private void AttachJob()
    {
        WindowsJobObject? job = null;
        try
        {
            var handle = _process.SafeHandle;
            _root = WindowsJobObject.Identify(handle, _rootId);
            job = WindowsJobObject.TryCreate();
            if (job is null)
            {
                return;
            }

            if (!job.TryAssign(handle))
            {
                // The process may already have exited, or sits in a job that cannot be nested: fall back to the tree walk.
                job.Dispose();
                return;
            }

            lock (_gate)
            {
                _job = job;
            }

            // Children the process started before it joined the job are not in it; sweep them (and theirs) in.
            if (_root is { } root)
            {
                job.AdoptDescendants(root);
            }
        }
        catch (Exception ex) when (IsContainmentFailure(ex))
        {
            lock (_gate)
            {
                if (_job is null)
                {
                    job?.Dispose();
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<int>> TerminateWindowsAsync(TimeSpan verifyTimeout)
    {
        WindowsJobObject? job;
        lock (_gate)
        {
            job = _disposed ? null : _job;
        }

        // Descendants still linked to the root are found even when the job is unavailable (or one escaped it).
        var linked = SafeFindDescendants();
        try
        {
            if (job is not null && _root is { } root)
            {
                job.AdoptDescendants(root);
            }

            job?.Terminate();
        }
        catch (Exception ex) when (IsContainmentFailure(ex))
        {
            job = null;
        }

        KillTree(_process);
        foreach (var descendant in linked)
        {
            WindowsJobObject.TryTerminate(descendant);
        }

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var remaining = new SortedSet<int>();
            try
            {
                foreach (var id in job?.GetProcessIds() ?? [])
                {
                    remaining.Add(id);
                }
            }
            catch (Exception ex) when (IsContainmentFailure(ex))
            {
                job = null;
            }

            foreach (var descendant in linked.Where(WindowsJobObject.IsAlive))
            {
                remaining.Add(descendant.Id);
            }

            if (_root is { } r && WindowsJobObject.IsAlive(r))
            {
                remaining.Add(r.Id);
            }

            if (remaining.Count == 0 || deadline.Elapsed >= verifyTimeout)
            {
                return remaining.ToList();
            }

            // A process that was being created while the job was terminated can outlive the first pass.
            try
            {
                job?.Terminate();
            }
            catch (Exception ex) when (IsContainmentFailure(ex))
            {
                job = null;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<ProcessIdentity> SafeFindDescendants()
    {
        try
        {
            return _root is { } root ? WindowsJobObject.FindDescendants(root) : [];
        }
        catch (Exception ex) when (IsContainmentFailure(ex))
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<int>> TerminatePosixAsync(TimeSpan gracePeriod, TimeSpan verifyTimeout)
    {
        // With a process group of its own (setsid), the group holds the tree, and it outlives its leader. Descendants
        // that moved to a session of their own (node's `detached` children, such as the browsers Playwright launches)
        // are outside it, so the ones still linked to the tree are signalled one by one as well; without a group, the
        // parent-PID walk is all there is.
        int? group = _ownsProcessGroup ? _rootId : null;
        var linked = new HashSet<int> { _rootId };
        await AddDescendantsAsync(linked).ConfigureAwait(false);
        await SignalAsync("TERM", group, linked).ConfigureAwait(false);

        // Only processes seen alive a moment ago are signalled again, which keeps reused ids out of harm's way.
        var alive = new HashSet<int>(linked);
        var grace = Stopwatch.StartNew();
        while (grace.Elapsed < gracePeriod)
        {
            var remaining = await PosixProcesses.FindLiveAsync(group, linked).ConfigureAwait(false);
            if (remaining is null)
            {
                // The process table cannot be read: give the tree what is left of the grace period through its root.
                await WaitForRootExitAsync(gracePeriod - grace.Elapsed).ConfigureAwait(false);
                break;
            }

            if (remaining.Count == 0)
            {
                return [];
            }

            alive = remaining.ToHashSet();
            await Task.Delay(PosixPollInterval).ConfigureAwait(false);
        }

        if (!HasExited(_process))
        {
            var fresh = new HashSet<int>();
            await AddDescendantsAsync(fresh).ConfigureAwait(false);
            linked.UnionWith(fresh);
            alive.UnionWith(fresh);
        }

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            await SignalAsync("KILL", group, alive).ConfigureAwait(false);
            KillTree(_process);
            var remaining = await PosixProcesses.FindLiveAsync(group, linked).ConfigureAwait(false);
            if (remaining is null || remaining.Count == 0 || deadline.Elapsed >= verifyTimeout)
            {
                return remaining ?? [];
            }

            alive = remaining.ToHashSet();
            await Task.Delay(PosixPollInterval).ConfigureAwait(false);
        }
    }

    private async Task AddDescendantsAsync(HashSet<int> ids)
    {
        foreach (var id in await PosixProcesses.GetDescendantsAsync(_rootId).ConfigureAwait(false))
        {
            ids.Add(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static Task SignalAsync(string signal, int? group, IReadOnlyCollection<int> ids)
    {
        var targets = ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (group is { } g)
        {
            targets.Insert(0, "-" + g.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return PosixProcesses.SignalAsync(signal, targets);
    }

    private async Task WaitForRootExitAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // Grace period over.
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already exited, or the tree could only be partially killed.
        }
    }

    // Containment is best effort: no interop or system failure may break the command it guards.
    private static bool IsContainmentFailure(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or ObjectDisposedException or ExternalException
            or DllNotFoundException or EntryPointNotFoundException or NotSupportedException or UnauthorizedAccessException;
}

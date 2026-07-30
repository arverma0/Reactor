using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Reactor.Processes;

/// <summary>
/// Process-group helpers for Unix-like systems (Linux, macOS), built on the <c>setsid</c>, <c>kill</c>, and <c>ps</c>
/// commands so no native interop is needed.
/// </summary>
internal static class PosixProcesses
{
    private static readonly Lazy<string?> Setsid = new(FindSetsid, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The <c>setsid</c> executable (util-linux; macOS has none), or null; always null on Windows.</summary>
    public static string? SetsidPath => OperatingSystem.IsWindows() ? null : Setsid.Value;

    /// <summary>
    /// Makes <paramref name="startInfo"/> run its program through <c>setsid</c>, so the process leads a new session and
    /// process group whose id is the process id, and the whole tree can be signalled through the group. Returns false,
    /// leaving the start info unchanged, on Windows, when <c>setsid</c> is unavailable, or when the start info passes
    /// its arguments as a single string instead of <see cref="ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public static bool TryStartInNewSession(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (SetsidPath is not { } setsid || string.IsNullOrEmpty(startInfo.FileName) || !string.IsNullOrEmpty(startInfo.Arguments))
        {
            return false;
        }

        startInfo.ArgumentList.Insert(0, startInfo.FileName);
        startInfo.FileName = setsid;
        return true;
    }

    /// <summary>
    /// Sends <paramref name="signal"/> (e.g. <c>TERM</c>) to process ids, or to process groups written as <c>-PGID</c>, with
    /// the POSIX spelling <c>kill -s SIG -- targets</c> run through the shell's builtin (kill binaries differ).
    /// </summary>
    public static Task SignalAsync(string signal, IReadOnlyCollection<string> targets) =>
        targets.Count == 0
            ? Task.CompletedTask
            : HelperProcess.RunAsync("/bin/sh", ["-c", $"kill -s {signal} -- {string.Join(' ', targets)}"]);

    /// <summary>The ids of the descendants of <paramref name="rootPid"/> found through parent-process links.</summary>
    public static async Task<IReadOnlyList<string>> GetDescendantsAsync(int rootPid)
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

    /// <summary>
    /// The ids of the live processes (zombies excluded) that are in process group <paramref name="groupId"/> (when given)
    /// or among <paramref name="ids"/>, in ascending order; null when the process table cannot be read.
    /// </summary>
    public static async Task<IReadOnlyList<int>?> FindLiveAsync(int? groupId, IReadOnlyCollection<int> ids)
    {
        var table = await ListAsync().ConfigureAwait(false);
        return table?
            .Where(p => !p.IsZombie && ((groupId is { } group && p.GroupId == group) || ids.Contains(p.Id)))
            .Select(p => p.Id)
            .Distinct()
            .Order()
            .ToList();
    }

    private static async Task<IReadOnlyList<PosixProcessInfo>?> ListAsync()
    {
        var (exitCode, output) = await HelperProcess.RunAsync("ps", ["-A", "-o", "pid=", "-o", "pgid=", "-o", "stat="]).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return null;
        }

        var result = new List<PosixProcessInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var group))
            {
                result.Add(new PosixProcessInfo(pid, group, parts[2]));
            }
        }

        return result;
    }

    private static string? FindSetsid()
    {
        foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "setsid");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entries are skipped.
            }
        }

        return null;
    }

    private readonly record struct PosixProcessInfo(int Id, int GroupId, string State)
    {
        public bool IsZombie => State.StartsWith('Z');
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

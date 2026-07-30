using System.Diagnostics;
using System.Globalization;

namespace Reactor.Tests.Support;

/// <summary>
/// Commands that start an orphaned grandchild — a process whose parent exits right away, so a parent-PID walk cannot
/// reach it — which writes its process id to a file and then sleeps for a minute. Shared by the process-tree tests of
/// ShellRunner, the agent's shell tool, and CLI agents.
/// </summary>
internal static class OrphanedGrandchild
{
    /// <summary>
    /// Whether orphans can be contained here: Windows (job objects), or Unix with <c>setsid</c> (process groups; macOS has
    /// no setsid).
    /// </summary>
    public static bool Supported => OperatingSystem.IsWindows() || File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid");

    /// <summary>
    /// A command for the platform shell (<c>cmd.exe</c> or <c>sh</c>) that orphans a grandchild writing its pid to
    /// <paramref name="pidFile"/>, then keeps running for <paramref name="keepRunningSeconds"/> (0: exits at once).
    /// </summary>
    public static string ShellCommand(string pidFile, int keepRunningSeconds = 60)
    {
        if (OperatingSystem.IsWindows())
        {
            // The intermediate cmd.exe exits as soon as `start /b` has launched PowerShell.
            var spawn = $"cmd /d /c start \"\" /b powershell -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 60\"";
            return keepRunningSeconds > 0
                ? spawn + string.Create(CultureInfo.InvariantCulture, $" & ping -n {keepRunningSeconds + 1} 127.0.0.1 >nul")
                : spawn;
        }

        // The subshell exits as soon as it has put the sleeper in the background.
        var orphan = $"(sh -c 'echo $$ > \"$1\"; exec sleep 60' sh '{pidFile}' >/dev/null 2>&1 &)";
        return keepRunningSeconds > 0 ? orphan + string.Create(CultureInfo.InvariantCulture, $"; sleep {keepRunningSeconds}") : orphan;
    }

    /// <summary>Writes an executable script (<c>.cmd</c> on Windows, <c>sh</c> elsewhere) running <see cref="ShellCommand"/>.</summary>
    public static string WriteScript(string directory, string pidFile, int keepRunningSeconds = 60)
    {
        var windows = OperatingSystem.IsWindows();
        var path = Path.Combine(directory, windows ? "orphan.cmd" : "orphan.sh");
        var body = windows
            ? "@echo off\r\n" + ShellCommand(pidFile, keepRunningSeconds) + "\r\n"
            : "#!/bin/sh\n" + ShellCommand(pidFile, keepRunningSeconds) + "\n";
        File.WriteAllText(path, body);
        if (!windows)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Waits until the grandchild has written its pid, failing if <paramref name="running"/> ends first.</summary>
    public static async Task<int> WaitForPidAsync(string pidFile, Task running, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(30)))
        {
            if (TryReadPid(pidFile) is { } pid)
            {
                return pid;
            }

            if (running.IsCompleted)
            {
                await running;
                throw new InvalidOperationException("The command ended before its grandchild started.");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"The grandchild did not write {pidFile}.");
    }

    public static int? TryReadPid(string pidFile)
    {
        try
        {
            return File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                ? pid
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>True while the grandchild runs (zombies count as gone; so does a process that reused its id).</summary>
    public static bool IsAlive(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            var ps = Process.Start(new ProcessStartInfo("ps", ["-o", "stat=", "-o", "comm=", "-p", pid.ToString(CultureInfo.InvariantCulture)])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var fields = ps.StandardOutput.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            ps.WaitForExit();
            return ps.ExitCode == 0 && fields.Length >= 2 && !fields[0].StartsWith('Z') && fields[^1].EndsWith("sleep", StringComparison.Ordinal);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the process to be gone; returns whether it is.</summary>
    public static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (IsAlive(pid))
        {
            if (deadline.Elapsed > timeout)
            {
                return false;
            }

            await Task.Delay(100);
        }

        return true;
    }

    /// <summary>Kills the grandchild if a test left it running.</summary>
    public static void Kill(string pidFile)
    {
        if (TryReadPid(pidFile) is not { } pid || !IsAlive(pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}

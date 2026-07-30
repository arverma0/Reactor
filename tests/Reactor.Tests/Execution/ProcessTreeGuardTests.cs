using System.Diagnostics;
using Reactor.Processes;
using Reactor.Tests.Support;

namespace Reactor.Tests.Execution;

public class ProcessTreeGuardTests
{
    [Fact]
    public async Task Children_started_before_the_guard_attached_join_the_job_and_die_with_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job objects are Windows-only.");
        using var dir = new TempDir();
        var pidFile = dir.Combine("child.pid");
        var command = $"powershell -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 60\"";
        using var shell = Process.Start(new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c \"" + command + "\"", UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            // The child is running before the guard exists, as when a shell starts one between Start() and Attach().
            var pid = await OrphanedGrandchild.WaitForPidAsync(pidFile, shell.WaitForExitAsync());
            var guard = ProcessTreeGuard.Attach(shell);
            Assert.True(guard.IsContained);

            // Disposing a guard that was neither released nor terminated closes the kill-on-close job, as a crash would.
            guard.Dispose();

            Assert.True(await OrphanedGrandchild.WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(5)), "the child was not swept into the job");
            Assert.True(shell.WaitForExit(5_000));
        }
        finally
        {
            OrphanedGrandchild.Kill(pidFile);
            if (!shell.HasExited)
            {
                shell.Kill(entireProcessTree: true);
            }
        }
    }
}

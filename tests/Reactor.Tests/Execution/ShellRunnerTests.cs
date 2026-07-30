using System.Diagnostics;
using Reactor.Execution;
using Reactor.Tests.Support;

namespace Reactor.Tests.Execution;

public class ShellRunnerTests
{
    private const string Unsupported = "Containing orphaned processes needs Windows job objects or setsid.";

    [Fact]
    public async Task Cancellation_terminates_grandchildren_whose_parent_already_exited()
    {
        Assert.SkipUnless(OrphanedGrandchild.Supported, Unsupported);
        using var dir = new TempDir();
        var pidFile = dir.Combine("grandchild.pid");
        using var cts = new CancellationTokenSource();
        try
        {
            var run = ShellRunner.RunAsync(OrphanedGrandchild.ShellCommand(pidFile), dir.Path, timeout: TimeSpan.FromMinutes(2), cancellationToken: cts.Token);
            var pid = await OrphanedGrandchild.WaitForPidAsync(pidFile, run);
            Assert.True(OrphanedGrandchild.IsAlive(pid));

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(await OrphanedGrandchild.WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(5)), "the orphaned grandchild outlived the cancelled command");
        }
        finally
        {
            OrphanedGrandchild.Kill(pidFile);
        }
    }

    [Fact]
    public async Task Timeouts_terminate_grandchildren_whose_parent_already_exited()
    {
        Assert.SkipUnless(OrphanedGrandchild.Supported, Unsupported);
        using var dir = new TempDir();

        // An orphaned grandchild that would write late.txt five seconds in, while the command outlives the timeout.
        var command = OperatingSystem.IsWindows()
            ? "cmd /d /c start \"\" /b cmd /d /c \"ping -n 6 127.0.0.1 >nul & echo late> late.txt\" & ping -n 60 127.0.0.1 >nul"
            : "(sh -c 'sleep 5; echo late > late.txt' >/dev/null 2>&1 &); sleep 60";

        var result = await ShellRunner.RunAsync(command, dir.Path, timeout: TimeSpan.FromSeconds(1.5));
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.SurvivingProcessIds);
        Assert.False(File.Exists(dir.Combine("late.txt")), "the orphaned grandchild kept running after the command timed out");
    }

    [Fact]
    public async Task Processes_a_command_leaves_running_survive_its_normal_exit()
    {
        Assert.SkipUnless(OrphanedGrandchild.Supported, Unsupported);
        using var dir = new TempDir();
        var pidFile = dir.Combine("background.pid");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await ShellRunner.RunAsync(OrphanedGrandchild.ShellCommand(pidFile, keepRunningSeconds: 0), dir.Path, timeout: TimeSpan.FromMinutes(2));

            // The background process may hold the output pipes open, so draining them is bounded.
            Assert.True(stopwatch.Elapsed < ShellRunner.DrainTimeout + TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            var pid = await OrphanedGrandchild.WaitForPidAsync(pidFile, Task.Delay(Timeout.Infinite));
            await Task.Delay(500);
            Assert.True(OrphanedGrandchild.IsAlive(pid), "a process the command started in the background was killed when it exited");
        }
        finally
        {
            OrphanedGrandchild.Kill(pidFile);
        }
    }

    [Fact]
    public async Task Output_is_captured_verbatim()
    {
        using var dir = new TempDir();
        var command = OperatingSystem.IsWindows() ? "echo first& echo second 1>&2& echo third" : "printf 'first\\r\\nsecond' 1>&2; printf 'progress 1\\rprogress 2\\n'";

        var result = await ShellRunner.RunAsync(command, dir.Path, timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("first\r\nthird\r\n", result.Stdout);
            Assert.Equal("second \r\n", result.Stderr);
        }
        else
        {
            Assert.Equal("progress 1\rprogress 2\n", result.Stdout);
            Assert.Equal("first\r\nsecond", result.Stderr);
        }
    }
}

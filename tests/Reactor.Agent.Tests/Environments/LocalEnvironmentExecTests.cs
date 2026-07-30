using System.Diagnostics;
using System.Text;
using Reactor.Agent.Environments;
using Reactor.Tests.Support;

namespace Reactor.Agent.Tests.Environments;

public class LocalEnvironmentExecTests
{
    private static bool IsWindows => OperatingSystem.IsWindows();

    private static string LongCommand => IsWindows ? "echo started & ping -n 30 127.0.0.1 >nul" : "echo started; sleep 30";

    private static string EchoVariable(string name) => IsWindows ? $"echo [%{name}%]" : $"echo \"[${name}]\"";

    [Fact]
    public async Task Echo_captures_stdout_exit_code_and_duration()
    {
        await using var ws = new TestWorkspace();

        var result = await ws.Environment.ExecCommandAsync("echo hello", 10_000);

        Assert.Equal("hello", result.Stdout.Trim());
        Assert.Equal("", result.Stderr);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.InRange(result.DurationMs, 0, 10_000);
    }

    [Fact]
    public async Task Non_zero_exit_codes_are_reported()
    {
        await using var ws = new TestWorkspace();

        var result = await ws.Environment.ExecCommandAsync("exit 3", 10_000);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Stderr_is_captured_separately()
    {
        await using var ws = new TestWorkspace();

        var result = await ws.Environment.ExecCommandAsync(IsWindows ? "echo out & echo oops 1>&2" : "echo out; echo oops 1>&2", 10_000);

        Assert.Equal("out", result.Stdout.Trim());
        Assert.Equal("oops", result.Stderr.Trim());
    }

    [Fact]
    public async Task Quotes_in_commands_are_passed_through_verbatim()
    {
        await using var ws = new TestWorkspace();

        var result = await ws.Environment.ExecCommandAsync("echo \"a  b\" && echo c", 10_000);

        var lines = result.Stdout.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(IsWindows ? "\"a  b\"" : "a  b", lines[0].Trim());
        Assert.Equal("c", lines[1].Trim());
    }

    [Fact]
    public async Task Commands_run_in_the_working_directory_or_the_requested_one()
    {
        await using var ws = new TestWorkspace();
        ws.MakeDirectory("sub");

        await ws.Environment.ExecCommandAsync("echo x> marker.txt", 10_000);
        await ws.Environment.ExecCommandAsync("echo y> marker2.txt", 10_000, workingDir: "sub");

        Assert.True(ws.Exists("marker.txt"));
        Assert.True(ws.Exists("sub/marker2.txt"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.Environment.ExecCommandAsync("echo x", 10_000, workingDir: "missing"));
    }

    [Fact]
    public async Task Sensitive_variables_are_filtered_by_default_but_visible_with_inherit_all()
    {
        const string name = "REACTOR_TEST_API_KEY";
        Environment.SetEnvironmentVariable(name, "secret-value-123");
        try
        {
            await using var filtered = new TestWorkspace();
            await using var inheritAll = new TestWorkspace(new LocalEnvironmentOptions { EnvVarPolicy = EnvVarPolicy.InheritAll });

            var hidden = await filtered.Environment.ExecCommandAsync(EchoVariable(name), 10_000);
            var visible = await inheritAll.Environment.ExecCommandAsync(EchoVariable(name), 10_000);

            Assert.DoesNotContain("secret-value-123", hidden.Stdout);
            Assert.Contains("[secret-value-123]", visible.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Non_sensitive_variables_are_inherited_by_default_but_not_with_inherit_core()
    {
        const string name = "REACTOR_TEST_PLAIN_SETTING";
        Environment.SetEnvironmentVariable(name, "plain-value");
        try
        {
            await using var filtered = new TestWorkspace();
            await using var core = new TestWorkspace(new LocalEnvironmentOptions { EnvVarPolicy = EnvVarPolicy.InheritCore });

            var inherited = await filtered.Environment.ExecCommandAsync(EchoVariable(name), 10_000);
            var coreOnly = await core.Environment.ExecCommandAsync(EchoVariable(name), 10_000);
            var path = await core.Environment.ExecCommandAsync(EchoVariable("PATH"), 10_000);

            Assert.Contains("[plain-value]", inherited.Stdout);
            Assert.DoesNotContain("plain-value", coreOnly.Stdout);
            Assert.True(path.Stdout.Trim().Length > 10, path.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Explicit_variables_win_over_the_policy()
    {
        await using var ws = new TestWorkspace(new LocalEnvironmentOptions
        {
            EnvVarPolicy = EnvVarPolicy.InheritNone,
            ExtraEnvironment = new Dictionary<string, string> { ["MY_SERVICE_TOKEN"] = "from-options", ["SHARED"] = "options" },
        });

        var result = await ws.Environment.ExecCommandAsync(
            EchoVariable("MY_SERVICE_TOKEN") + (IsWindows ? " & " : "; ") + EchoVariable("SHARED") + (IsWindows ? " & " : "; ") + EchoVariable("PER_CALL"),
            10_000,
            envVars: new Dictionary<string, string> { ["SHARED"] = "call", ["PER_CALL"] = "yes" });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[from-options]", result.Stdout);
        Assert.Contains("[call]", result.Stdout);
        Assert.Contains("[yes]", result.Stdout);
    }

    [Fact]
    public async Task Output_is_streamed_while_the_command_runs()
    {
        await using var ws = new TestWorkspace();
        var chunks = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        long firstSeenAt = -1;
        var command = IsWindows ? "echo first& ping -n 2 127.0.0.1 >nul& echo second" : "echo first; sleep 1; echo second";

        var result = await ws.Environment.ExecCommandAsync(command, 10_000, onOutput: chunk =>
        {
            lock (chunks)
            {
                chunks.Add(chunk);
                if (firstSeenAt < 0 && string.Concat(chunks).Contains("first", StringComparison.Ordinal))
                {
                    firstSeenAt = stopwatch.ElapsedMilliseconds;
                }
            }
        });
        var total = stopwatch.ElapsedMilliseconds;

        Assert.Contains("second", string.Concat(chunks));
        Assert.Equal(result.Stdout, string.Concat(chunks));
        Assert.True(firstSeenAt >= 0 && total - firstSeenAt >= 500, $"first chunk at {firstSeenAt}ms, finished at {total}ms");
    }

    [Fact]
    public async Task Timeouts_return_promptly_with_partial_output()
    {
        await using var ws = new TestWorkspace();
        var stopwatch = Stopwatch.StartNew();

        var result = await ws.Environment.ExecCommandAsync(LongCommand, 1_000);

        Assert.True(result.TimedOut);
        Assert.Contains("started", result.Stdout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        Assert.True(result.DurationMs >= 900, $"duration {result.DurationMs}");
        Assert.Equal(0, ws.Environment.RunningProcessCount);
    }

    [Fact]
    public async Task Timeouts_kill_the_whole_process_tree()
    {
        await using var ws = new TestWorkspace();
        var command = IsWindows ? "ping -n 3 127.0.0.1 >nul & echo late> late.txt" : "sleep 2; echo late > late.txt";

        var result = await ws.Environment.ExecCommandAsync(command, 1_000);
        await Task.Delay(3_000);

        Assert.True(result.TimedOut);
        Assert.False(ws.Exists("late.txt"), "the command kept running after the timeout");
    }

    [Fact]
    public async Task Cancellation_kills_the_command_and_throws()
    {
        await using var ws = new TestWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ws.Environment.ExecCommandAsync(LongCommand, 60_000, cancellationToken: cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        Assert.Equal(0, ws.Environment.RunningProcessCount);
    }

    [Fact]
    public async Task Already_cancelled_tokens_throw_without_running()
    {
        await using var ws = new TestWorkspace();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ws.Environment.ExecCommandAsync("echo x> ran.txt", 10_000, cancellationToken: new CancellationToken(true)));

        Assert.False(ws.Exists("ran.txt"));
    }

    [Fact]
    public async Task Cancellation_terminates_grandchildren_whose_parent_already_exited()
    {
        Assert.SkipUnless(OrphanedGrandchild.Supported, "Containing orphaned processes needs Windows job objects or setsid.");
        await using var ws = new TestWorkspace();
        var pidFile = ws.PathOf("grandchild.pid");
        using var cts = new CancellationTokenSource();
        try
        {
            var running = ws.Environment.ExecCommandAsync(OrphanedGrandchild.ShellCommand(pidFile), 120_000, cancellationToken: cts.Token);
            var pid = await OrphanedGrandchild.WaitForPidAsync(pidFile, running);

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.True(await OrphanedGrandchild.WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(5)), "the orphaned grandchild outlived the cancelled command");
        }
        finally
        {
            OrphanedGrandchild.Kill(pidFile);
        }
    }

    [Fact]
    public async Task Processes_a_command_leaves_running_survive_its_normal_exit()
    {
        Assert.SkipUnless(OrphanedGrandchild.Supported, "Containing orphaned processes needs Windows job objects or setsid.");
        await using var ws = new TestWorkspace();
        var pidFile = ws.PathOf("background.pid");
        try
        {
            var result = await ws.Environment.ExecCommandAsync(OrphanedGrandchild.ShellCommand(pidFile, keepRunningSeconds: 0), 120_000);

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
    public async Task Cleanup_terminates_running_commands()
    {
        await using var ws = new TestWorkspace();
        var running = ws.Environment.ExecCommandAsync(LongCommand, 60_000);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ws.Environment.RunningProcessCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, ws.Environment.RunningProcessCount);
        await ws.Environment.CleanupAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, ws.Environment.RunningProcessCount);
    }

    [Fact]
    public async Task Stdin_is_closed_so_commands_do_not_wait_for_input()
    {
        await using var ws = new TestWorkspace();
        var command = IsWindows ? "findstr x & echo done" : "cat; echo done";

        var result = await ws.Environment.ExecCommandAsync(command, 10_000);

        Assert.False(result.TimedOut);
        Assert.Contains("done", result.Stdout);
    }

    [Fact]
    public async Task Large_output_is_captured_completely()
    {
        await using var ws = new TestWorkspace();
        var command = IsWindows ? "for /L %i in (1,1,5000) do @echo line %i" : "for i in $(seq 1 5000); do echo line $i; done";

        var result = await ws.Environment.ExecCommandAsync(command, 30_000);

        var lines = result.Stdout.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5000, lines.Length);
        Assert.Equal("line 5000", lines[^1].Trim());
    }

    [Fact]
    public async Task Output_is_decoded_as_utf8()
    {
        await using var ws = new TestWorkspace();
        ws.Write("utf8.txt", "héllo wörld ✓");

        var result = await ws.Environment.ExecCommandAsync(IsWindows ? "type utf8.txt" : "cat utf8.txt", 10_000);

        Assert.Equal("héllo wörld ✓", result.Stdout.Trim());
    }

    [Fact]
    public async Task Commands_can_run_concurrently()
    {
        await using var ws = new TestWorkspace();

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => ws.Environment.ExecCommandAsync($"echo run{i}", 20_000)));

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal($"run{i + 1}", results[i].Stdout.Trim());
        }
    }

    [Fact]
    public async Task Invalid_timeouts_are_rejected()
    {
        await using var ws = new TestWorkspace();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ws.Environment.ExecCommandAsync("echo x", 0));
    }

    [Fact]
    public async Task PowerShell_shell_runs_commands_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PowerShell is only available on Windows.");
        await using var ws = new TestWorkspace(new LocalEnvironmentOptions { Shell = ShellKind.PowerShell });

        var result = await ws.Environment.ExecCommandAsync("Write-Output \"ps-$((2+3))\"", 30_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ps-5", result.Stdout.Trim());
    }

    [Fact]
    public void Captured_output_is_capped_keeping_head_and_tail()
    {
        var buffer = new CappedTextBuffer(10);
        buffer.Append("01234");
        buffer.Append("56789");
        buffer.Append("abc");
        buffer.Append("defgh");

        Assert.Equal("01234\n[... 8 characters of output were not captured ...]\ndefgh", buffer.ToString());

        var small = new CappedTextBuffer(10);
        small.Append("abc");
        Assert.Equal("abc", small.ToString());

        var big = new CappedTextBuffer(4);
        big.Append(new StringBuilder().Append('x', 100).Append("yz").ToString());
        Assert.Equal("xx\n[... 98 characters of output were not captured ...]\nyz", big.ToString());
    }
}

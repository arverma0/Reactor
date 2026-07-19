using Reactor.Agent.Environments;
using Reactor.Agent.Tools;

namespace Reactor.Agent.Tests.Environments;

public class WrapperTests
{
    [Fact]
    public async Task Logging_wrapper_logs_commands_exit_codes_durations_and_writes()
    {
        await using var ws = new TestWorkspace();
        var log = new List<string>();
        var env = new LoggingExecutionEnvironment(ws.Environment, line => { lock (log) { log.Add(line); } });

        var result = await env.ExecCommandAsync("exit 2", 10_000);
        await env.WriteFileAsync("out.txt", "hello");
        await env.DeleteFileAsync("out.txt");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("exec: exit 2", log[0]);
        Assert.Matches(@"^exit: 2 in \d+ms$", log[1]);
        Assert.Equal("write: out.txt (5 chars)", log[2]);
        Assert.Equal("delete: out.txt", log[3]);
        Assert.False(ws.Exists("out.txt"));
    }

    [Fact]
    public async Task Logging_wrapper_delegates_everything_else()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "needle\n");
        var env = new LoggingExecutionEnvironment(ws.Environment, _ => { });

        Assert.Same(ws.Environment, env.Inner);
        Assert.Equal(ws.Environment.WorkingDirectory, env.WorkingDirectory);
        Assert.Equal(ws.Environment.Platform, env.Platform);
        Assert.Equal(ws.Environment.ShellName, env.ShellName);
        Assert.Equal(ws.Environment.ResolvePath("a.txt"), env.ResolvePath("a.txt"));
        Assert.Equal("needle", await env.ReadFileAsync("a.txt"));
        Assert.True(await env.FileExistsAsync("a.txt"));
        Assert.Equal("a.txt:1:needle", await env.GrepAsync("needle", ".", new GrepOptions()));
        Assert.Equal(new[] { "a.txt" }, await env.GlobAsync("*.txt", "."));
        Assert.Single(await env.ListDirectoryAsync(".", 1));
    }

    [Fact]
    public async Task Logging_wrapper_logs_timeouts()
    {
        await using var ws = new TestWorkspace();
        var log = new List<string>();
        var env = new LoggingExecutionEnvironment(ws.Environment, line => { lock (log) { log.Add(line); } });
        var command = OperatingSystem.IsWindows() ? "ping -n 10 127.0.0.1 >nul" : "sleep 10";

        var result = await env.ExecCommandAsync(command, 500);

        Assert.True(result.TimedOut);
        Assert.EndsWith("(timed out)", log[1]);
    }

    [Fact]
    public async Task Read_only_wrapper_rejects_writes_and_deletes()
    {
        await using var ws = new TestWorkspace();
        ws.Write("keep.txt", "original");
        var env = new ReadOnlyExecutionEnvironment(ws.Environment);

        var write = await Assert.ThrowsAsync<InvalidOperationException>(() => env.WriteFileAsync("new.txt", "x"));
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => env.DeleteFileAsync("keep.txt"));

        Assert.Equal("Write operations are disabled in read-only mode", write.Message);
        Assert.Equal("Write operations are disabled in read-only mode", delete.Message);
        Assert.False(ws.Exists("new.txt"));
        Assert.Equal("original", ws.ReadText("keep.txt"));
    }

    [Fact]
    public async Task Read_only_wrapper_allows_reads_searches_and_commands()
    {
        await using var ws = new TestWorkspace();
        ws.Write("keep.txt", "original\n");
        var env = new ReadOnlyExecutionEnvironment(ws.Environment);

        Assert.Equal("original", await env.ReadFileAsync("keep.txt"));
        Assert.Equal("keep.txt:1:original", await env.GrepAsync("orig", ".", new GrepOptions()));
        Assert.Equal(new[] { "keep.txt" }, await env.GlobAsync("*", "."));
        Assert.Equal("hi", (await env.ExecCommandAsync("echo hi", 10_000)).Stdout.Trim());
        Assert.Same(ws.Environment, env.Inner);
    }

    [Fact]
    public async Task Read_only_wrapper_makes_write_tools_fail()
    {
        await using var ws = new TestWorkspace();
        var context = ws.Context(environment: new ReadOnlyExecutionEnvironment(ws.Environment));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ws.RunAsync(CoreTools.WriteFile(), """{"file_path":"x.txt","content":"y"}""", context));

        Assert.Equal("Write operations are disabled in read-only mode", error.Message);
    }
}

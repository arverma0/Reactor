using System.Diagnostics;
using Reactor.Agent.Environments;
using Reactor.Agent.Tests.Environments;
using Reactor.Agent.Tools;

namespace Reactor.Agent.Tests.Tools;

public class ShellToolTests
{
    private const string TimeoutMessage =
        "\n[ERROR: Command timed out after {0}ms. Partial output is shown above.\nYou can retry with a longer timeout by setting the timeout_ms parameter.]";

    [Fact]
    public void Output_format_lists_stdout_stderr_and_exit_information()
    {
        var withStderr = ShellTool.Format(new ExecResult { Stdout = "out\n", Stderr = "err\n", ExitCode = 2, DurationMs = 15 }, 10_000);
        var plain = ShellTool.Format(new ExecResult { Stdout = "out", ExitCode = 0, DurationMs = 3 }, 10_000);

        Assert.Equal("out\n\n[stderr]\nerr\n\n[exit code: 2, duration: 15ms]", withStderr);
        Assert.Equal("out\n[exit code: 0, duration: 3ms]", plain);
    }

    [Fact]
    public void Timeouts_append_the_exact_message()
    {
        var output = ShellTool.Format(new ExecResult { Stdout = "partial", ExitCode = -1, TimedOut = true, DurationMs = 1200 }, 1000);

        Assert.Equal("partial\n[exit code: -1, duration: 1200ms]" + string.Format(TimeoutMessage, 1000), output);
    }

    [Fact]
    public void Effective_timeout_prefers_explicit_then_profile_then_session_default_and_is_clamped()
    {
        var config = new SessionConfig { DefaultCommandTimeoutMs = 10_000, MaxCommandTimeoutMs = 600_000 };
        ToolContext Context(int? profile) => new() { Environment = new LocalExecutionEnvironment(), Config = config, ProfileShellTimeoutMs = profile };

        Assert.Equal(10_000, ShellTool.EffectiveTimeout(null, Context(null)));
        Assert.Equal(120_000, ShellTool.EffectiveTimeout(null, Context(120_000)));
        Assert.Equal(5_000, ShellTool.EffectiveTimeout(5_000, Context(120_000)));
        Assert.Equal(600_000, ShellTool.EffectiveTimeout(9_999_999, Context(null)));
        Assert.Equal(1, ShellTool.EffectiveTimeout(0, Context(null)));
        Assert.Equal(1, ShellTool.EffectiveTimeout(-5, Context(null)));

        // A session override beats the profile default but not an explicit timeout_ms.
        config.CommandTimeoutOverrideMs = 30_000;
        Assert.Equal(30_000, ShellTool.EffectiveTimeout(null, Context(120_000)));
        Assert.Equal(30_000, ShellTool.EffectiveTimeout(null, Context(null)));
        Assert.Equal(5_000, ShellTool.EffectiveTimeout(5_000, Context(120_000)));
        Assert.Equal(30_000, config.Clone().CommandTimeoutOverrideMs);
    }

    [Fact]
    public async Task Tool_passes_timeout_streaming_callback_and_cancellation_to_the_environment()
    {
        await using var ws = new TestWorkspace();
        var recorder = new RecordingEnvironment(ws.Environment);
        using var cts = new CancellationTokenSource();
        Action<string> onOutput = _ => { };
        var context = ws.Context(
            new SessionConfig { DefaultCommandTimeoutMs = 7_000, MaxCommandTimeoutMs = 60_000 },
            onOutput,
            profileShellTimeoutMs: 120_000,
            cancellationToken: cts.Token,
            environment: recorder);

        await ws.RunAsync(CoreTools.Shell(), """{"command":"echo hi","description":"say hi"}""", context);

        Assert.Equal("echo hi", recorder.Command);
        Assert.Equal(60_000, recorder.TimeoutMs);
        Assert.Same(onOutput, recorder.OnOutput);
        Assert.Equal(cts.Token, recorder.CancellationToken);
        Assert.Null(recorder.WorkingDir);

        await ws.RunAsync(CoreTools.Shell(), Args.Of(("command", "echo hi"), ("timeout_ms", 2_500)), context);
        Assert.Equal(2_500, recorder.TimeoutMs);
    }

    [Fact]
    public async Task Runs_real_commands_and_reports_non_zero_exits_as_output()
    {
        await using var ws = new TestWorkspace();
        var command = OperatingSystem.IsWindows() ? "echo hello & echo problem 1>&2 & exit 4" : "echo hello; echo problem 1>&2; exit 4";

        var output = (await ws.RunAsync(CoreTools.Shell(), Args.Of(("command", command)))).Text.Replace("\r", "", StringComparison.Ordinal);

        Assert.StartsWith("hello", output);
        Assert.Contains("\n[stderr]\nproblem", output);
        Assert.Matches(@"\n\[exit code: 4, duration: \d+ms\]$", output);
    }

    [Fact]
    public async Task Streams_output_through_the_context()
    {
        await using var ws = new TestWorkspace();
        var chunks = new List<string>();
        var context = ws.Context(onOutput: chunk => { lock (chunks) { chunks.Add(chunk); } });

        await ws.RunAsync(CoreTools.Shell(), """{"command":"echo streamed"}""", context);

        Assert.Contains("streamed", string.Concat(chunks));
    }

    [Fact]
    public async Task Timed_out_commands_report_the_timeout_message()
    {
        await using var ws = new TestWorkspace();
        var command = OperatingSystem.IsWindows() ? "echo begun & ping -n 30 127.0.0.1 >nul" : "echo begun; sleep 30";
        var stopwatch = Stopwatch.StartNew();

        var output = (await ws.RunAsync(CoreTools.Shell(), Args.Of(("command", command), ("timeout_ms", 1_000)))).Text;

        Assert.Contains("begun", output);
        Assert.EndsWith(string.Format(TimeoutMessage, 1000), output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Session_default_timeout_applies_when_no_override_is_given()
    {
        await using var ws = new TestWorkspace();
        var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
        var context = ws.Context(new SessionConfig { DefaultCommandTimeoutMs = 800 });

        var output = (await ws.RunAsync(CoreTools.Shell(), Args.Of(("command", command)), context)).Text;

        Assert.EndsWith(string.Format(TimeoutMessage, 800), output);
    }

    [Fact]
    public async Task Empty_commands_are_rejected()
    {
        await using var ws = new TestWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.Shell(), """{"command":"   "}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.Shell(), """{}"""));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        await using var ws = new TestWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ws.RunAsync(CoreTools.Shell(), Args.Of(("command", command), ("timeout_ms", 60_000)), ws.Context(cancellationToken: cts.Token)));
    }
}

/// <summary>Delegates to an inner environment, recording the arguments of the last command.</summary>
internal sealed class RecordingEnvironment(IExecutionEnvironment inner) : IExecutionEnvironment
{
    public string? Command { get; private set; }

    public int TimeoutMs { get; private set; }

    public string? WorkingDir { get; private set; }

    public Action<string>? OnOutput { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public string? GrepPattern { get; private set; }

    public string? GrepPath { get; private set; }

    public GrepOptions? GrepOptions { get; private set; }

    public string WorkingDirectory => inner.WorkingDirectory;

    public string Platform => inner.Platform;

    public string OsVersion => inner.OsVersion;

    public string ShellName => inner.ShellName;

    public string ResolvePath(string path) => inner.ResolvePath(path);

    public Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default) =>
        inner.ReadFileAsync(path, offset, limit, cancellationToken);

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) => inner.ReadFileBytesAsync(path, cancellationToken);

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) => inner.WriteFileAsync(path, content, cancellationToken);

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => inner.FileExistsAsync(path, cancellationToken);

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) => inner.DirectoryExistsAsync(path, cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteFileAsync(path, cancellationToken);

    public Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default) =>
        inner.ListDirectoryAsync(path, depth, cancellationToken);

    public Task<ExecResult> ExecCommandAsync(
        string command, int timeoutMs, string? workingDir = null, IReadOnlyDictionary<string, string>? envVars = null,
        Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        Command = command;
        TimeoutMs = timeoutMs;
        WorkingDir = workingDir;
        OnOutput = onOutput;
        CancellationToken = cancellationToken;
        return Task.FromResult(new ExecResult { Stdout = "recorded", ExitCode = 0, DurationMs = 1 });
    }

    public Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default)
    {
        GrepPattern = pattern;
        GrepPath = path;
        GrepOptions = options;
        return inner.GrepAsync(pattern, path, options, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default) =>
        inner.GlobAsync(pattern, path, cancellationToken);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);

    public Task CleanupAsync() => inner.CleanupAsync();
}

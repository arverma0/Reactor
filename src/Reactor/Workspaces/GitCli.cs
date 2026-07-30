using System.Diagnostics;
using System.Text;

namespace Reactor.Workspaces;

/// <summary>The result of one git invocation.</summary>
internal sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The first line of standard output, trimmed.</summary>
    public string FirstLine => Output.Split('\n', 2)[0].Trim();

    /// <summary>A short description of a failure for logs and exceptions.</summary>
    public string Describe() => Error.Trim() is { Length: > 0 } error ? error.ReplaceLineEndings(" ") : $"exit code {ExitCode}";
}

/// <summary>Options for one git invocation.</summary>
internal sealed record GitCall
{
    /// <summary>Extra environment variables (e.g. <c>GIT_INDEX_FILE</c> for a private index).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Text written to standard input (then closed).</summary>
    public string? Input { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Runs git for workspace tracking. Every call gets a sanitized environment (no inherited <c>GIT_DIR</c>,
/// <c>GIT_WORK_TREE</c>, or <c>GIT_INDEX_FILE</c>; optional locks off, so the user's index is never refreshed or locked)
/// and configuration that keeps git from running anything of the user's: hooks are disabled, the file-system monitor and
/// split index are off, and long paths are on. On Windows the real <c>git.exe</c> behind the <c>cmd\git.exe</c> launcher
/// is used when it can be found, which saves a process per call.
/// </summary>
internal static class GitCli
{
    private static readonly string[] InheritedVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_QUARANTINE_PATH", "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "GIT_PAGER",
    ];

    private static readonly Lazy<Task<Executable?>> Located = new(LocateAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A path that does not exist, used as <c>core.hooksPath</c> so no repository hook runs for our commands.</summary>
    private static readonly string NoHooks = Path.Combine(AppContext.BaseDirectory, ".reactor-no-git-hooks");

    private static readonly string[] Settings =
    [
        "core.hooksPath=" + NoHooks, "core.fsmonitor=false", "core.splitIndex=false", "core.longpaths=true",
        "core.safecrlf=false", "advice.addEmbeddedRepo=false", "advice.detachedHead=false", "gc.auto=0",
    ];

    internal sealed record Executable(string Path, string? SearchPath, string Version);

    /// <summary>True when a working git is on the PATH (checked once per process).</summary>
    public static async Task<bool> IsAvailableAsync() => await Located.Value.ConfigureAwait(false) is not null;

    /// <summary>The git version string, or null when git is not available.</summary>
    public static async Task<string?> VersionAsync() => (await Located.Value.ConfigureAwait(false))?.Version;

    /// <summary>Runs <c>git &lt;arguments&gt;</c> in <paramref name="workingDirectory"/>.</summary>
    /// <exception cref="InvalidOperationException">git is not installed.</exception>
    /// <exception cref="TimeoutException">The command ran longer than <see cref="GitCall.Timeout"/> (it is killed).</exception>
    public static async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, GitCall? call = null, CancellationToken cancellationToken = default)
    {
        var git = await Located.Value.ConfigureAwait(false) ?? throw new InvalidOperationException("git is not installed (or not on the PATH).");
        return await RunWithAsync(git.Path, git.SearchPath, workingDirectory, arguments, call ?? new GitCall(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs git and throws <see cref="InvalidOperationException"/> when it exits non-zero.</summary>
    public static async Task<GitResult> RunCheckedAsync(string workingDirectory, IReadOnlyList<string> arguments, GitCall? call = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workingDirectory, arguments, call, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result
            : throw new InvalidOperationException($"git {string.Join(' ', arguments.Take(2))} failed: {result.Describe()}");
    }

    /// <summary>Starts a long-running git command with redirected standard streams (the caller owns the process).</summary>
    /// <exception cref="InvalidOperationException">git is not installed.</exception>
    public static async Task<Process> StartAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var git = await Located.Value.ConfigureAwait(false) ?? throw new InvalidOperationException("git is not installed (or not on the PATH).");
        var process = new Process { StartInfo = StartInfo(git.Path, git.SearchPath, workingDirectory, arguments, null, redirectInput: true) };
        process.Start();
        return process;
    }

    private static ProcessStartInfo StartInfo(string executable, string? searchPath, string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = redirectInput ? new UTF8Encoding(false) : null,
        };
        foreach (var setting in Settings)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(setting);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in InheritedVariables)
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        if (searchPath is not null)
        {
            startInfo.Environment["PATH"] = searchPath + Path.PathSeparator + (startInfo.Environment.TryGetValue("PATH", out var path) ? path : "");
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private static async Task<GitResult> RunWithAsync(
        string executable,
        string? searchPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitCall call,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartInfo(executable, searchPath, workingDirectory, arguments, call.Environment, redirectInput: call.Input is not null) };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(call.Timeout);
        try
        {
            if (call.Input is { } input)
            {
                try
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The process exited without reading its input; its exit code tells what happened.
                }
            }

            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"git {string.Join(' ', arguments.Take(2))} timed out after {call.Timeout.TotalSeconds:0}s.");
        }

        return new GitResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static async Task<Executable?> LocateAsync()
    {
        var quick = new GitCall { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var probe = await RunWithAsync("git", null, AppContext.BaseDirectory, ["--version"], quick, CancellationToken.None).ConfigureAwait(false);
            if (!probe.Succeeded)
            {
                return null;
            }

            var version = probe.FirstLine;
            if (!OperatingSystem.IsWindows())
            {
                return new Executable("git", null, version);
            }

            // Git for Windows: cmd\git.exe is a launcher that starts <root>\<mingw|ucrt>64\bin\git.exe with an extended PATH.
            var execPath = await RunWithAsync("git", null, AppContext.BaseDirectory, ["--exec-path"], quick, CancellationToken.None).ConfigureAwait(false);
            if (execPath.Succeeded && execPath.FirstLine.Length > 0)
            {
                var bin = Path.GetFullPath(Path.Combine(execPath.FirstLine, "..", "..", "bin"));
                var direct = Path.Combine(bin, "git.exe");
                var usr = Path.GetFullPath(Path.Combine(bin, "..", "..", "usr", "bin"));
                var searchPath = Directory.Exists(usr) ? bin + Path.PathSeparator + usr : bin;
                if (File.Exists(direct))
                {
                    var check = await RunWithAsync(direct, searchPath, AppContext.BaseDirectory, ["--version"], quick, CancellationToken.None).ConfigureAwait(false);
                    if (check.Succeeded)
                    {
                        return new Executable(direct, searchPath, version);
                    }
                }
            }

            return new Executable("git", null, version);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }
}

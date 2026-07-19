namespace Reactor.Agent;

/// <summary>
/// Where tools run. All tool operations go through this interface, so the same tools work locally, in Docker,
/// over SSH, or anywhere else an implementation targets. Relative paths resolve against <see cref="WorkingDirectory"/>.
/// </summary>
public interface IExecutionEnvironment
{
    /// <summary>The directory relative paths resolve against.</summary>
    string WorkingDirectory { get; }

    /// <summary><c>darwin</c>, <c>linux</c>, <c>windows</c>, or <c>wasm</c>.</summary>
    string Platform { get; }

    /// <summary>Human-readable OS version string.</summary>
    string OsVersion { get; }

    /// <summary>Description of the shell used by <see cref="ExecCommandAsync"/>, e.g. <c>cmd.exe</c> or <c>/bin/bash</c>.</summary>
    string ShellName { get; }

    /// <summary>Resolves <paramref name="path"/> against the working directory (expanding <c>~</c>).</summary>
    string ResolvePath(string path);

    /// <summary>
    /// Reads a text file. <paramref name="offset"/> is a 1-based starting line; <paramref name="limit"/> caps the number of lines.
    /// Returns the selected lines without line numbers. Throws <see cref="FileNotFoundException"/> when missing.
    /// </summary>
    Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a file's raw bytes.</summary>
    Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes a text file, creating parent directories as needed.</summary>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);

    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file. Missing files are not an error.</summary>
    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Lists a directory to <paramref name="depth"/> levels (1 = immediate children).</summary>
    Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a shell command. On timeout the process tree is terminated gracefully, then forcibly after 2 seconds, and the
    /// collected output is returned with <see cref="ExecResult.TimedOut"/> set. <paramref name="onOutput"/> receives output
    /// chunks as they arrive (for streaming tool output).
    /// </summary>
    Task<ExecResult> ExecCommandAsync(
        string command,
        int timeoutMs,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default);

    /// <summary>Searches file contents by regex. Returns formatted matches (<c>path:line:content</c> in content mode).</summary>
    Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default);

    /// <summary>Finds files matching a glob pattern; results sorted by modification time, newest first.</summary>
    Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default);

    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases resources and terminates any still-running processes.</summary>
    Task CleanupAsync();
}

/// <summary>Result of <see cref="IExecutionEnvironment.ExecCommandAsync"/>.</summary>
public sealed record ExecResult
{
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>A directory listing entry.</summary>
/// <param name="Name">The entry name, or its path relative to the listed directory for entries below the first level.</param>
/// <param name="IsDir">Whether the entry is a directory.</param>
/// <param name="Size">File size in bytes, null for directories.</param>
public sealed record DirEntry(string Name, bool IsDir, long? Size);

/// <summary>Options for <see cref="IExecutionEnvironment.GrepAsync"/>.</summary>
public sealed record GrepOptions
{
    /// <summary>File name glob filter, e.g. <c>*.cs</c>.</summary>
    public string? GlobFilter { get; init; }

    public bool CaseInsensitive { get; init; }

    /// <summary>Maximum number of results (matching lines, files, or counts). Default 100.</summary>
    public int MaxResults { get; init; } = 100;

    /// <summary><c>content</c> (default), <c>files_with_matches</c>, or <c>count</c>.</summary>
    public string OutputMode { get; init; } = "content";

    /// <summary>Context lines before each match (content mode).</summary>
    public int ContextBefore { get; init; }

    /// <summary>Context lines after each match (content mode).</summary>
    public int ContextAfter { get; init; }
}

/// <summary>Which environment variables child processes inherit.</summary>
public enum EnvVarPolicy
{
    /// <summary>Inherit everything except sensitive variables (<c>*_API_KEY</c>, <c>*_SECRET</c>, <c>*_TOKEN</c>, <c>*_PASSWORD</c>, <c>*_CREDENTIAL</c>). Default.</summary>
    FilterSensitive,

    /// <summary>Inherit all variables unfiltered.</summary>
    InheritAll,

    /// <summary>Inherit only core variables (PATH, HOME, USER, SHELL, LANG, TERM, TMPDIR, language toolchain paths, Windows system variables).</summary>
    InheritCore,

    /// <summary>Start with an empty environment (plus explicitly supplied variables).</summary>
    InheritNone,
}

/// <summary>The shell used to run commands.</summary>
public enum ShellKind
{
    /// <summary><c>/bin/bash -c</c> on Linux/macOS (falling back to <c>/bin/sh</c>), <c>cmd.exe /c</c> on Windows.</summary>
    Default,
    Bash,
    Sh,
    Cmd,

    /// <summary>Windows PowerShell (<c>powershell.exe</c>).</summary>
    PowerShell,

    /// <summary>PowerShell 7+ (<c>pwsh</c>).</summary>
    Pwsh,
}

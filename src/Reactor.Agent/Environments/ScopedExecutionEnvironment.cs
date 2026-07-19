namespace Reactor.Agent;

/// <summary>
/// Wraps an environment so relative paths and commands resolve against a subdirectory (used to scope subagents).
/// Absolute paths pass through unchanged.
/// </summary>
public sealed class ScopedExecutionEnvironment(IExecutionEnvironment inner, string workingDirectory) : IExecutionEnvironment
{
    private readonly IExecutionEnvironment _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory);

    public string Platform => _inner.Platform;

    public string OsVersion => _inner.OsVersion;

    public string ShellName => _inner.ShellName;

    public string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith('~'))
        {
            return _inner.ResolvePath(path);
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));
    }

    public Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _inner.ReadFileAsync(ResolvePath(path), offset, limit, cancellationToken);

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.ReadFileBytesAsync(ResolvePath(path), cancellationToken);

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
        _inner.WriteFileAsync(ResolvePath(path), content, cancellationToken);

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.FileExistsAsync(ResolvePath(path), cancellationToken);

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.DirectoryExistsAsync(ResolvePath(path), cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.DeleteFileAsync(ResolvePath(path), cancellationToken);

    public Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default) =>
        _inner.ListDirectoryAsync(ResolvePath(path), depth, cancellationToken);

    public Task<ExecResult> ExecCommandAsync(
        string command,
        int timeoutMs,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        _inner.ExecCommandAsync(command, timeoutMs, ResolvePath(workingDir ?? "."), envVars, onOutput, cancellationToken);

    public Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default) =>
        _inner.GrepAsync(pattern, ResolvePath(path), options, cancellationToken);

    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default) =>
        _inner.GlobAsync(pattern, ResolvePath(path), cancellationToken);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(WorkingDirectory);
        return Task.CompletedTask;
    }

    /// <summary>The scope does not own the inner environment; cleanup is left to its owner.</summary>
    public Task CleanupAsync() => Task.CompletedTask;
}

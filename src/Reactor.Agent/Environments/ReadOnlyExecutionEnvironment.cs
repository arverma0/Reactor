namespace Reactor.Agent.Environments;

/// <summary>
/// Wraps another environment and rejects file writes and deletions with
/// <see cref="InvalidOperationException"/>. Reads, searches, and commands are delegated unchanged (commands are not
/// analysed for write intent).
/// </summary>
public sealed class ReadOnlyExecutionEnvironment : IExecutionEnvironment
{
    /// <summary>The error message used for rejected writes.</summary>
    public const string WriteDisabledMessage = "Write operations are disabled in read-only mode";

    private readonly IExecutionEnvironment _inner;

    /// <summary>Creates a read-only view of <paramref name="inner"/>.</summary>
    /// <param name="inner">The environment that serves reads, searches, and commands.</param>
    public ReadOnlyExecutionEnvironment(IExecutionEnvironment inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>The wrapped environment.</summary>
    public IExecutionEnvironment Inner => _inner;

    /// <inheritdoc />
    public string WorkingDirectory => _inner.WorkingDirectory;

    /// <inheritdoc />
    public string Platform => _inner.Platform;

    /// <inheritdoc />
    public string OsVersion => _inner.OsVersion;

    /// <inheritdoc />
    public string ShellName => _inner.ShellName;

    /// <inheritdoc />
    public string ResolvePath(string path) => _inner.ResolvePath(path);

    /// <inheritdoc />
    public Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _inner.ReadFileAsync(path, offset, limit, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.ReadFileBytesAsync(path, cancellationToken);

    /// <summary>Always throws: writes are disabled.</summary>
    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(WriteDisabledMessage));

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.FileExistsAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.DirectoryExistsAsync(path, cancellationToken);

    /// <summary>Always throws: deletions are disabled.</summary>
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(WriteDisabledMessage));

    /// <inheritdoc />
    public Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default) =>
        _inner.ListDirectoryAsync(path, depth, cancellationToken);

    /// <inheritdoc />
    public Task<ExecResult> ExecCommandAsync(
        string command,
        int timeoutMs,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        _inner.ExecCommandAsync(command, timeoutMs, workingDir, envVars, onOutput, cancellationToken);

    /// <inheritdoc />
    public Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default) =>
        _inner.GrepAsync(pattern, path, options, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default) =>
        _inner.GlobAsync(pattern, path, cancellationToken);

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public Task CleanupAsync() => _inner.CleanupAsync();
}

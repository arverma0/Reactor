using System.Globalization;

namespace Reactor.Agent.Environments;

/// <summary>
/// Wraps another environment and logs commands (with exit codes and durations), file writes and deletions, searches, and
/// lifecycle calls. Everything is delegated to the inner environment unchanged.
/// </summary>
public sealed class LoggingExecutionEnvironment : IExecutionEnvironment
{
    private readonly IExecutionEnvironment _inner;
    private readonly Action<string> _log;

    /// <summary>Creates a logging view of <paramref name="inner"/>.</summary>
    /// <param name="inner">The environment that does the work.</param>
    /// <param name="log">Receives one line per logged operation.</param>
    public LoggingExecutionEnvironment(IExecutionEnvironment inner, Action<string> log)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        _log($"write: {path} ({content.Length.ToString(CultureInfo.InvariantCulture)} chars)");
        try
        {
            await _inner.WriteFileAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"write failed: {path}: {ex.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.FileExistsAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _inner.DirectoryExistsAsync(path, cancellationToken);

    /// <inheritdoc />
    public async Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        _log($"delete: {path}");
        await _inner.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default) =>
        _inner.ListDirectoryAsync(path, depth, cancellationToken);

    /// <inheritdoc />
    public async Task<ExecResult> ExecCommandAsync(
        string command,
        int timeoutMs,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        _log("exec: " + command);
        try
        {
            var result = await _inner.ExecCommandAsync(command, timeoutMs, workingDir, envVars, onOutput, cancellationToken)
                .ConfigureAwait(false);
            _log("exit: " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + " in " +
                 result.DurationMs.ToString(CultureInfo.InvariantCulture) + "ms" + (result.TimedOut ? " (timed out)" : ""));
            return result;
        }
        catch (OperationCanceledException)
        {
            _log("exec cancelled: " + command);
            throw;
        }
        catch (Exception ex)
        {
            _log("exec failed: " + ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default)
    {
        _log($"grep: {pattern} in {path}");
        return _inner.GrepAsync(pattern, path, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default)
    {
        _log($"glob: {pattern} in {path}");
        return _inner.GlobAsync(pattern, path, cancellationToken);
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _log("initialize: " + _inner.WorkingDirectory);
        return _inner.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task CleanupAsync()
    {
        _log("cleanup");
        return _inner.CleanupAsync();
    }
}

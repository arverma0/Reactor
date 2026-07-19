namespace Reactor.Agent.Environments;

/// <summary>Options for <see cref="LocalExecutionEnvironment"/>.</summary>
public sealed record LocalEnvironmentOptions
{
    /// <summary>Working directory; defaults to the process's current directory.</summary>
    public string? WorkingDirectory { get; init; }

    public EnvVarPolicy EnvVarPolicy { get; init; } = EnvVarPolicy.FilterSensitive;

    /// <summary>Variables added to (or overriding) the inherited environment for every command.</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    public ShellKind Shell { get; init; } = ShellKind.Default;

    /// <summary>Use ripgrep for grep when it is on PATH (default true); falls back to a managed regex search.</summary>
    public bool UseRipgrep { get; init; } = true;
}

/// <summary>Runs tools on the local machine. (Contract stub: implementation pending.)</summary>
public sealed class LocalExecutionEnvironment : IExecutionEnvironment
{
    private readonly LocalEnvironmentOptions _options;

    public LocalExecutionEnvironment(LocalEnvironmentOptions? options = null)
    {
        _options = options ?? new LocalEnvironmentOptions();
        WorkingDirectory = Path.GetFullPath(_options.WorkingDirectory ?? Directory.GetCurrentDirectory());
    }

    public string WorkingDirectory { get; }

    public string Platform => throw new NotImplementedException();

    public string OsVersion => throw new NotImplementedException();

    public string ShellName => throw new NotImplementedException();

    public string ResolvePath(string path) => throw new NotImplementedException();

    public Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<ExecResult> ExecCommandAsync(string command, int timeoutMs, string? workingDir = null, IReadOnlyDictionary<string, string>? envVars = null, Action<string>? onOutput = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task CleanupAsync() => throw new NotImplementedException();
}

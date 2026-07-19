using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Reactor.Agent.Environments;

/// <summary>Options for <see cref="LocalExecutionEnvironment"/>.</summary>
public sealed record LocalEnvironmentOptions
{
    /// <summary>Working directory; defaults to the process's current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Which environment variables commands inherit (default: everything except secrets).</summary>
    public EnvVarPolicy EnvVarPolicy { get; init; } = EnvVarPolicy.FilterSensitive;

    /// <summary>Variables added to (or overriding) the inherited environment for every command.</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>The shell commands run in (default: <c>cmd.exe</c> on Windows, <c>/bin/bash</c> or <c>/bin/sh</c> elsewhere).</summary>
    public ShellKind Shell { get; init; } = ShellKind.Default;

    /// <summary>Use ripgrep for grep when it is on PATH (default true); falls back to a managed regex search.</summary>
    public bool UseRipgrep { get; init; } = true;
}

/// <summary>
/// Runs tools on the local machine. Relative paths resolve against <see cref="WorkingDirectory"/> (with <c>~</c> expanded).
/// Commands run through the platform shell (<c>cmd.exe</c> on Windows, <c>/bin/bash</c> or <c>/bin/sh</c> elsewhere) in
/// their own process group where possible, with stdin closed, filtered environment variables, a timeout that terminates the
/// whole process tree (graceful signal, 2 seconds of grace, then a forced kill), and streamed output. Grep uses ripgrep
/// when available and a managed regex search otherwise. Instances are thread-safe.
/// </summary>
public sealed class LocalExecutionEnvironment : IExecutionEnvironment
{
    private static readonly Lazy<string?> SetsidPath = new(
        () => OperatingSystem.IsWindows() ? null : ExecutableLocator.FindAny("setsid", "/usr/bin/setsid", "/bin/setsid"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly LocalEnvironmentOptions _options;
    private readonly ShellInvocation _shell;
    private readonly ProcessTracker _processes = new();
    private readonly Lazy<string?> _ripgrepPath;

    /// <summary>Creates an environment with the given options (all defaults when null).</summary>
    public LocalExecutionEnvironment(LocalEnvironmentOptions? options = null)
    {
        _options = options ?? new LocalEnvironmentOptions();
        var configured = _options.WorkingDirectory;
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(ExpandHome(configured));
        WorkingDirectory = Path.TrimEndingDirectorySeparator(directory);
        _shell = ShellInvocation.Resolve(_options.Shell);
        _ripgrepPath = new Lazy<string?>(
            () => _options.UseRipgrep ? ExecutableLocator.Find("rg") : null,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string WorkingDirectory { get; }

    /// <inheritdoc />
    public string Platform { get; } = DetectPlatform();

    /// <inheritdoc />
    public string OsVersion => RuntimeInformation.OSDescription;

    /// <inheritdoc />
    public string ShellName => _shell.DisplayName;

    /// <summary>Number of commands currently running (diagnostics and tests).</summary>
    internal int RunningProcessCount => _processes.Count;

    /// <inheritdoc />
    public string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return WorkingDirectory;
        }

        return Path.GetFullPath(ExpandHome(path.Trim()), WorkingDirectory);
    }

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(path);
        EnsureExistingFile(fullPath);
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must not be negative.");
        }

        var first = Math.Max(1, offset ?? 1);
        var maxLines = limit ?? int.MaxValue;
        if (maxLines == 0)
        {
            return "";
        }

        var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, TextFileDecoding.Utf8, detectEncodingFromByteOrderMarks: true);
            var result = new StringBuilder();
            var lineNumber = 0;
            var taken = 0;
            await foreach (var line in ReadLinesAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                if (++lineNumber < first)
                {
                    continue;
                }

                if (taken > 0)
                {
                    result.Append('\n');
                }

                result.Append(line);
                if (++taken >= maxLines)
                {
                    break;
                }
            }

            return result.ToString();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(path);
        EnsureExistingFile(fullPath);

        // Share read/write/delete so files other processes are still writing (logs, build outputs) can be read.
        var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, Array.MaxLength) : 0);
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var fullPath = ResolvePath(path);
        if (Directory.Exists(fullPath))
        {
            throw new IOException($"Cannot write to '{fullPath}': it is a directory.");
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllTextAsync(fullPath, content, TextFileDecoding.Utf8, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolvePath(path)));

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Directory.Exists(ResolvePath(path)));

    /// <inheritdoc />
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(path);
        if (Directory.Exists(fullPath))
        {
            throw new IOException($"Cannot delete '{fullPath}': it is a directory, not a file.");
        }

        File.Delete(fullPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(path);
        if (!Directory.Exists(fullPath))
        {
            throw File.Exists(fullPath)
                ? new IOException($"Not a directory: {fullPath}")
                : new DirectoryNotFoundException($"Directory not found: {fullPath}");
        }

        var levels = Math.Max(1, depth);
        return Task.Run<IReadOnlyList<DirEntry>>(
            () =>
            {
                var entries = new List<DirEntry>();
                AddDirectoryEntries(new DirectoryInfo(fullPath), "", levels, entries, cancellationToken);
                return entries;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExecResult> ExecCommandAsync(
        string command,
        int timeoutMs,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMs, 1);
        var directory = workingDir is null ? WorkingDirectory : ResolvePath(workingDir);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Working directory not found: {directory}");
        }

        var setsid = _shell.UsesArgumentList ? SetsidPath.Value : null;
        var startInfo = _shell.CreateStartInfo(command, setsid);
        startInfo.WorkingDirectory = directory;
        startInfo.Environment.Clear();
        var environment = EnvironmentVariables.Build(
            _options.EnvVarPolicy, EnvironmentVariables.CurrentProcess(), _options.ExtraEnvironment, envVars, OperatingSystem.IsWindows());
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        return await ProcessRunner.RunAsync(startInfo, timeoutMs, setsid is not null, onOutput, _processes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(options);
        var fullPath = ResolvePath(path ?? "");
        var isFile = File.Exists(fullPath);
        if (!isFile && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Path not found: {fullPath}");
        }

        GrepModes.Parse(options.OutputMode);
        if (_ripgrepPath.Value is { } ripgrep)
        {
            var result = await RipgrepSearch.RunAsync(ripgrep, pattern, fullPath, options, WorkingDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        var regex = ManagedGrep.CreateRegex(pattern, options.CaseInsensitive);
        return await Task.Run(
            () => ManagedGrep.Search(regex, fullPath, isFile, options, WorkingDirectory, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var baseDirectory = ResolvePath(path ?? "");
        if (!Directory.Exists(baseDirectory))
        {
            throw new DirectoryNotFoundException(File.Exists(baseDirectory)
                ? $"Not a directory: {baseDirectory}"
                : $"Directory not found: {baseDirectory}");
        }

        var glob = GlobPattern.Parse(ExpandHome(pattern), GlobPattern.IgnoreCaseByDefault);
        return Task.Run(() => GlobSearch.Find(glob, baseDirectory, WorkingDirectory, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(WorkingDirectory);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CleanupAsync() => _processes.TerminateAllAsync();

    private static string DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return "darwin";
        }

        if (OperatingSystem.IsBrowser() || OperatingSystem.IsWasi())
        {
            return "wasm";
        }

        return OperatingSystem.IsFreeBSD() ? "freebsd" : "linux";
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal)))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("HOME") ?? "";
            }

            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    private static void EnsureExistingFile(string fullPath)
    {
        if (File.Exists(fullPath))
        {
            return;
        }

        if (Directory.Exists(fullPath))
        {
            throw new IOException($"Cannot read '{fullPath}': it is a directory, not a file.");
        }

        throw new FileNotFoundException($"File not found: {fullPath}", fullPath);
    }

    private static void AddDirectoryEntries(DirectoryInfo directory, string prefix, int remainingDepth, List<DirEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var children = DirectoryWalker.ListEntries(directory);
        var directories = children.OfType<DirectoryInfo>()
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.Ordinal);
        var files = children.Where(c => c is not DirectoryInfo)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal);

        foreach (var child in directories)
        {
            var name = prefix + child.Name;
            entries.Add(new DirEntry(name, true, null));
            if (remainingDepth > 1 && !DirectoryWalker.IsVcsDirectory(child.Name) && !DirectoryWalker.IsLink(child))
            {
                AddDirectoryEntries(child, name + "/", remainingDepth - 1, entries, cancellationToken);
            }
        }

        foreach (var file in files)
        {
            entries.Add(new DirEntry(prefix + file.Name, false, SafeLength(file)));
        }
    }

    private static long? SafeLength(FileSystemInfo file)
    {
        try
        {
            return file is FileInfo info ? info.Length : null;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // Splits on '\n' (dropping the '\r' of CRLF) so line numbers agree with the tools and ripgrep.
    private static async IAsyncEnumerable<string> ReadLinesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[16 * 1024];
        var current = new StringBuilder();
        var pending = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, '\n', start, read - start);
                if (newline < 0)
                {
                    current.Append(buffer, start, read - start);
                    pending = true;
                    break;
                }

                current.Append(buffer, start, newline - start);
                if (current.Length > 0 && current[^1] == '\r')
                {
                    current.Length--;
                }

                yield return current.ToString();
                current.Clear();
                pending = false;
                start = newline + 1;
            }
        }

        if (pending)
        {
            yield return current.ToString();
        }
    }
}

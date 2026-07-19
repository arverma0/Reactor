using System.Globalization;
using System.Text;
using Reactor.Llm;

namespace Reactor.Agent.Prompts;

/// <summary>Runtime information captured at session start for the system prompt (spec 6.3, 6.4).</summary>
public sealed record EnvironmentSnapshot
{
    public required string WorkingDirectory { get; init; }
    public bool IsGitRepository { get; init; }
    public string? GitRoot { get; init; }
    public string? GitBranch { get; init; }

    /// <summary>Short status, e.g. <c>3 modified, 1 untracked</c>.</summary>
    public string? GitStatus { get; init; }

    public IReadOnlyList<string> RecentCommits { get; init; } = [];
    public required string Platform { get; init; }
    public required string OsVersion { get; init; }
    public string Shell { get; init; } = "";
    public string Date { get; init; } = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Captures a snapshot, running read-only git commands through the environment.</summary>
    public static async Task<EnvironmentSnapshot> CaptureAsync(IExecutionEnvironment environment, bool includeGit = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var snapshot = new EnvironmentSnapshot
        {
            WorkingDirectory = environment.WorkingDirectory,
            Platform = environment.Platform,
            OsVersion = environment.OsVersion,
            Shell = environment.ShellName,
        };

        if (!includeGit)
        {
            return snapshot;
        }

        var root = await GitAsync(environment, "git rev-parse --show-toplevel", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root))
        {
            return snapshot;
        }

        var branch = await GitAsync(environment, "git rev-parse --abbrev-ref HEAD", cancellationToken).ConfigureAwait(false);
        var status = await GitAsync(environment, "git status --porcelain", cancellationToken).ConfigureAwait(false) ?? "";
        var log = await GitAsync(environment, "git log --oneline -n 8", cancellationToken).ConfigureAwait(false) ?? "";

        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var untracked = lines.Count(l => l.StartsWith("??", StringComparison.Ordinal));
        var modified = lines.Length - untracked;
        return snapshot with
        {
            IsGitRepository = true,
            GitRoot = Path.GetFullPath(root.Trim()),
            GitBranch = branch?.Trim(),
            GitStatus = lines.Length == 0 ? "clean" : $"{modified} modified, {untracked} untracked",
            RecentCommits = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }

    /// <summary>The <c>&lt;environment&gt;</c> block included in every system prompt.</summary>
    public string ToPromptBlock(string model)
    {
        var info = ModelCatalog.GetModelInfo(model);
        var sb = new StringBuilder();
        sb.Append("<environment>\n");
        sb.Append("Working directory: ").Append(WorkingDirectory).Append('\n');
        sb.Append("Is git repository: ").Append(IsGitRepository ? "true" : "false").Append('\n');
        if (IsGitRepository)
        {
            sb.Append("Git branch: ").Append(GitBranch ?? "(unknown)").Append('\n');
            sb.Append("Git status: ").Append(GitStatus ?? "(unknown)").Append('\n');
        }

        sb.Append("Platform: ").Append(Platform).Append('\n');
        sb.Append("OS version: ").Append(OsVersion).Append('\n');
        if (Shell.Length > 0)
        {
            sb.Append("Shell: ").Append(Shell).Append('\n');
        }

        sb.Append("Today's date: ").Append(Date).Append('\n');
        sb.Append("Model: ").Append(info?.DisplayName is { Length: > 0 } name ? $"{name} ({model})" : model).Append('\n');
        if (info?.KnowledgeCutoff is { Length: > 0 } cutoff)
        {
            sb.Append("Knowledge cutoff: ").Append(cutoff).Append('\n');
        }

        sb.Append("</environment>");
        if (IsGitRepository && RecentCommits.Count > 0)
        {
            sb.Append("\n\nRecent commits:\n");
            foreach (var commit in RecentCommits)
            {
                sb.Append("- ").Append(commit).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<string?> GitAsync(IExecutionEnvironment environment, string command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await environment.ExecCommandAsync(command, 5000, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && !result.TimedOut ? result.Stdout.Trim() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>
/// Discovers project instruction files (spec 6.5): walks from the git root (or working directory) down to the working
/// directory, loading recognized files at each level (root first; deeper files are appended and take precedence),
/// within a 32 KB budget.
/// </summary>
public static class ProjectDocs
{
    public const int ByteBudget = 32 * 1024;
    public const string TruncationMarker = "[Project instructions truncated at 32KB]";

    public static async Task<string> DiscoverAsync(
        IExecutionEnvironment environment,
        string? gitRoot,
        IReadOnlyList<string> fileNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileNames);
        var workingDirectory = Path.GetFullPath(environment.WorkingDirectory);
        var directories = new List<string>();
        var root = gitRoot is null ? workingDirectory : Path.GetFullPath(gitRoot);
        var relative = Path.GetRelativePath(root, workingDirectory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            directories.Add(workingDirectory);
        }
        else
        {
            directories.Add(root);
            var current = root;
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                current = Path.Combine(current, segment);
                directories.Add(current);
            }
        }

        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var directory in directories)
        {
            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(directory, fileName);
                if (!await environment.FileExistsAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                string content;
                try
                {
                    content = await environment.ReadFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var section = $"## {Path.GetRelativePath(root, path).Replace('\\', '/')}\n\n{content.Trim()}\n\n";
                var sectionBytes = Encoding.UTF8.GetByteCount(section);
                if (bytes + sectionBytes > ByteBudget)
                {
                    var remaining = Math.Max(0, ByteBudget - bytes);
                    sb.Append(TruncateUtf8(section, remaining)).Append('\n').Append(TruncationMarker);
                    return sb.ToString().TrimEnd();
                }

                sb.Append(section);
                bytes += sectionBytes;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var length = Math.Min(text.Length, maxBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > maxBytes)
        {
            length--;
        }

        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}

/// <summary>Assembles the layered system prompt (spec 6.1).</summary>
public static class SystemPromptBuilder
{
    /// <summary>
    /// Base instructions → environment context → tool descriptions → project instructions → user instructions (last,
    /// highest priority).
    /// </summary>
    public static string Build(
        string baseInstructions,
        EnvironmentSnapshot? environment,
        string model,
        IReadOnlyList<ToolDefinition> tools,
        string? projectDocs,
        string? userInstructions)
    {
        ArgumentNullException.ThrowIfNull(baseInstructions);
        ArgumentNullException.ThrowIfNull(tools);
        var sb = new StringBuilder();
        sb.Append(baseInstructions.Trim()).Append("\n\n");
        if (environment is not null)
        {
            sb.Append(environment.ToPromptBlock(model)).Append("\n\n");
        }

        if (tools.Count > 0)
        {
            sb.Append("# Available tools\n");
            foreach (var tool in tools)
            {
                var firstLine = tool.Description.Split('\n', 2)[0].Trim();
                sb.Append("- ").Append(tool.Name).Append(": ").Append(firstLine).Append('\n');
            }

            sb.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(projectDocs))
        {
            sb.Append("# Project instructions\n");
            sb.Append("The following instructions come from files in the repository. Follow them; deeper (more specific) files take precedence.\n\n");
            sb.Append(projectDocs.Trim()).Append("\n\n");
        }

        if (!string.IsNullOrWhiteSpace(userInstructions))
        {
            sb.Append("# Additional instructions\n");
            sb.Append(userInstructions.Trim()).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }
}

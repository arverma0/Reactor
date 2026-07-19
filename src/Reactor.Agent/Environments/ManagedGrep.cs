using System.Text.RegularExpressions;

namespace Reactor.Agent.Environments;

/// <summary>
/// The managed (no ripgrep) grep implementation: recursive search skipping version-control directories and node_modules,
/// binary files (NUL byte in the first 8 KB), and files larger than 10 MB.
/// </summary>
internal static class ManagedGrep
{
    /// <summary>Files larger than this are skipped.</summary>
    public const long MaxFileSize = 10 * 1024 * 1024;

    /// <summary>Per-line regex evaluation limit guarding against catastrophic backtracking.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".bzr", "_darcs", "node_modules",
    };

    /// <summary>Directory names grep never descends into.</summary>
    public static IReadOnlyCollection<string> SkippedDirectoryNames => SkippedDirectories;

    /// <summary>Compiles <paramref name="pattern"/>; throws <see cref="ArgumentException"/> ("Invalid regex: ...") when invalid.</summary>
    public static Regex CreateRegex(string pattern, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return new Regex(pattern, options, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regex: {ex.Message}", ex);
        }
    }

    public static string Search(
        Regex regex, string root, bool rootIsFile, GrepOptions options, string workingDirectory, CancellationToken cancellationToken)
    {
        var mode = GrepModes.Parse(options.OutputMode);
        var collector = new GrepCollector(mode, options.MaxResults, options.ContextBefore, options.ContextAfter);
        GlobPattern? filter = null;
        var filterUsesPath = false;
        if (!string.IsNullOrWhiteSpace(options.GlobFilter))
        {
            filter = GlobPattern.Parse(options.GlobFilter, GlobPattern.IgnoreCaseByDefault);
            filterUsesPath = options.GlobFilter.Contains('/', StringComparison.Ordinal) ||
                             (OperatingSystem.IsWindows() && options.GlobFilter.Contains('\\', StringComparison.Ordinal));
        }

        IEnumerable<(string FullPath, string RelativePath)> files = rootIsFile
            ? [(root, Path.GetFileName(root))]
            : DirectoryWalker.EnumerateFiles(root, SkippedDirectories.Contains, cancellationToken);
        foreach (var (fullPath, relativePath) in files)
        {
            if (collector.IsComplete)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!rootIsFile && filter is not null)
            {
                var subject = filterUsesPath ? relativePath : relativePath[(relativePath.LastIndexOf('/') + 1)..];
                if (!filter.IsMatch(subject))
                {
                    continue;
                }
            }

            SearchFile(regex, fullPath, PathDisplay.ForDisplay(fullPath, workingDirectory), mode, options, collector);
        }

        return collector.Build();
    }

    private static void SearchFile(Regex regex, string fullPath, string display, GrepOutputMode mode, GrepOptions options, GrepCollector collector)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileSize)
            {
                return;
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (TextFileDecoding.IsBinary(bytes))
        {
            return;
        }

        var lines = TextFileDecoding.SplitLines(TextFileDecoding.Decode(bytes).Text);
        switch (mode)
        {
            case GrepOutputMode.FilesWithMatches:
                foreach (var line in lines)
                {
                    if (IsMatch(regex, line, display))
                    {
                        collector.AddFile(display);
                        return;
                    }
                }

                return;

            case GrepOutputMode.Count:
                var count = 0;
                foreach (var line in lines)
                {
                    if (IsMatch(regex, line, display))
                    {
                        count++;
                    }
                }

                if (count > 0)
                {
                    collector.AddCount(display, count);
                }

                return;

            default:
                SearchContent(regex, lines, display, Math.Max(0, options.ContextBefore), Math.Max(0, options.ContextAfter), collector);
                return;
        }
    }

    private static void SearchContent(Regex regex, List<string> lines, string display, int before, int after, GrepCollector collector)
    {
        var lastEmitted = -1;
        var afterRemaining = 0;
        for (var i = 0; i < lines.Count && !collector.IsComplete; i++)
        {
            if (IsMatch(regex, lines[i], display))
            {
                for (var j = Math.Max(lastEmitted + 1, i - before); j < i; j++)
                {
                    collector.AddContext(display, j + 1, GrepCollector.Clip(lines[j]));
                }

                collector.AddMatch(display, i + 1, GrepCollector.Clip(lines[i]));
                lastEmitted = i;
                afterRemaining = after;
            }
            else if (afterRemaining > 0)
            {
                collector.AddContext(display, i + 1, GrepCollector.Clip(lines[i]));
                lastEmitted = i;
                afterRemaining--;
            }
        }
    }

    private static bool IsMatch(Regex regex, string line, string display)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new InvalidOperationException(
                $"The regex took too long to evaluate on {display}; simplify the pattern (for example, avoid nested quantifiers).", ex);
        }
    }
}

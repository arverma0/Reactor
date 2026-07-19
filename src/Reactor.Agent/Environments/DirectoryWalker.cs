namespace Reactor.Agent.Environments;

/// <summary>Directory traversal shared by grep and glob: deterministic order, no symlink cycles, inaccessible entries skipped.</summary>
internal static class DirectoryWalker
{
    private static readonly HashSet<string> VcsDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".bzr", "_darcs",
    };

    /// <summary>Non-recursive enumeration that includes hidden and system entries and skips inaccessible ones.</summary>
    public static EnumerationOptions Options { get; } = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>True for version-control metadata directories (<c>.git</c>, <c>.hg</c>, <c>.svn</c>, <c>.bzr</c>, <c>_darcs</c>).</summary>
    public static bool IsVcsDirectory(string name) => VcsDirectories.Contains(name);

    /// <summary>True for symbolic links and junctions (never descended into, to avoid cycles).</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>The entries of <paramref name="directory"/>, or an empty list when it cannot be read.</summary>
    public static List<FileSystemInfo> ListEntries(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFileSystemInfos("*", Options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Enumerates files below <paramref name="root"/> depth-first with entries in ordinal name order (like <c>rg --sort path</c>),
    /// skipping directories selected by <paramref name="skipDirectory"/> and symlinked directories. Relative paths use <c>/</c>.
    /// </summary>
    public static IEnumerable<(string FullPath, string RelativePath)> EnumerateFiles(
        string root, Func<string, bool> skipDirectory, CancellationToken cancellationToken) =>
        Enumerate(new DirectoryInfo(root), "", skipDirectory, cancellationToken);

    private static IEnumerable<(string FullPath, string RelativePath)> Enumerate(
        DirectoryInfo directory, string prefix, Func<string, bool> skipDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = ListEntries(directory);
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var entry in entries)
        {
            var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            if (entry is DirectoryInfo subdirectory)
            {
                if (skipDirectory(subdirectory.Name) || IsLink(subdirectory))
                {
                    continue;
                }

                foreach (var file in Enumerate(subdirectory, relative, skipDirectory, cancellationToken))
                {
                    yield return file;
                }
            }
            else
            {
                yield return (entry.FullName, relative);
            }
        }
    }
}

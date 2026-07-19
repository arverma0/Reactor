namespace Reactor.Agent.Environments;

/// <summary>Formats paths for model-facing output: relative to the working directory (with <c>/</c>) when inside it, else absolute.</summary>
internal static class PathDisplay
{
    /// <summary>Path comparison used for containment checks (case-insensitive on Windows and macOS).</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Comparer matching <see cref="Comparison"/>.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Returns <paramref name="fullPath"/> relative to <paramref name="baseDirectory"/> when inside it, otherwise unchanged.</summary>
    public static string ForDisplay(string fullPath, string baseDirectory) =>
        TryGetRelative(fullPath, baseDirectory) ?? fullPath;

    /// <summary>The <c>/</c>-separated path of <paramref name="fullPath"/> relative to <paramref name="baseDirectory"/>, or null when outside it.</summary>
    public static string? TryGetRelative(string fullPath, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        var root = Path.TrimEndingDirectorySeparator(baseDirectory);
        var path = Path.TrimEndingDirectorySeparator(fullPath);
        if (string.Equals(path, root, Comparison))
        {
            return ".";
        }

        if (!path.StartsWith(root, Comparison))
        {
            return null;
        }

        string relative;
        if (root.Length > 0 && IsSeparator(root[^1]))
        {
            relative = path[root.Length..];
        }
        else if (path.Length > root.Length && IsSeparator(path[root.Length]))
        {
            relative = path[(root.Length + 1)..];
        }
        else
        {
            return null;
        }

        return ToForwardSlashes(relative);
    }

    /// <summary>Replaces the platform directory separator with <c>/</c>.</summary>
    public static string ToForwardSlashes(string path) =>
        Path.DirectorySeparatorChar == '/' ? path : path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}

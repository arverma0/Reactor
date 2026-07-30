using System.Text;
using System.Text.RegularExpressions;

namespace Reactor.Workspaces;

/// <summary>
/// A stage's <c>write_scope</c> (Reactor extension): comma-separated globs relative to the stage's working directory
/// that the stage may change. <c>*</c> and <c>?</c> match within one path segment, <c>**</c> matches any number of
/// segments, a pattern ending in <c>/</c> or without wildcards also covers everything below it, and <c>\</c> is read as
/// <c>/</c>. Matching ignores case on Windows.
/// </summary>
internal sealed class WriteScope
{
    private readonly List<Regex> _patterns;

    private WriteScope(IReadOnlyList<string> patterns, List<Regex> compiled)
    {
        Patterns = patterns;
        _patterns = compiled;
    }

    /// <summary>The patterns as written (normalized separators).</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>Parses a <c>write_scope</c> value; null when it is empty.</summary>
    public static WriteScope? Parse(string? value)
    {
        var patterns = Split(value);
        return patterns.Count == 0 ? null : new WriteScope(patterns, patterns.Select(Compile).ToList());
    }

    /// <summary>The normalized patterns of a value.</summary>
    public static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Replace('\\', '/'))
                .Select(p => p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p)
                .Where(p => p.Length > 0)
                .ToList();

    /// <summary>A problem with one pattern (for lint), or null when it is usable.</summary>
    public static string? Problem(string pattern)
    {
        if (Path.IsPathRooted(pattern) || pattern.StartsWith('/') || (pattern.Length > 1 && pattern[1] == ':'))
        {
            return $"'{pattern}' is absolute; write_scope patterns are relative to the stage's working directory.";
        }

        return pattern.Split('/').Any(s => s == "..") ? $"'{pattern}' leaves the working directory ('..')." : null;
    }

    /// <summary>True when <paramref name="path"/> (relative, <c>/</c>-separated) is inside the scope.</summary>
    public bool Allows(string path) => _patterns.Any(p => p.IsMatch(path));

    private static Regex Compile(string pattern)
    {
        var directory = pattern.EndsWith('/');
        var trimmed = pattern.TrimEnd('/');
        var sb = new StringBuilder("^");
        var segments = trimmed.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var last = i == segments.Length - 1;
            if (segment == "**")
            {
                // "**" spans zero or more whole segments.
                sb.Append(last ? ".*" : "(?:[^/]+/)*");
                continue;
            }

            foreach (var c in segment)
            {
                sb.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }

            if (!last)
            {
                sb.Append('/');
            }
        }

        // A directory pattern (trailing slash, or a plain path without wildcards) also covers what is below it.
        if (directory || (!trimmed.Contains('*', StringComparison.Ordinal) && !trimmed.Contains('?', StringComparison.Ordinal)))
        {
            sb.Append("(?:/.*)?");
        }

        sb.Append('$');
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(sb.ToString(), options);
    }
}

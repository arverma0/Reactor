using System.Text;
using System.Text.RegularExpressions;

namespace Reactor.Agent.Environments;

/// <summary>
/// A compiled glob pattern supporting <c>**</c> (any number of path segments), <c>*</c>, <c>?</c>, character classes
/// (<c>[abc]</c>, <c>[a-z]</c>, <c>[!abc]</c>), and brace alternatives (<c>{a,b}</c>, nestable). Wildcards also match
/// names starting with a dot. On Windows backslashes are path separators; elsewhere they escape the next character.
/// </summary>
internal sealed class GlobPattern
{
    private const int MaxAlternatives = 1024;

    private GlobPattern(string pattern, IReadOnlyList<GlobAlternative> alternatives)
    {
        Pattern = pattern;
        Alternatives = alternatives;
    }

    /// <summary>The pattern as given.</summary>
    public string Pattern { get; }

    /// <summary>The brace-expanded alternatives.</summary>
    public IReadOnlyList<GlobAlternative> Alternatives { get; }

    /// <summary>Default case sensitivity for the platform's file system (insensitive on Windows and macOS).</summary>
    public static bool IgnoreCaseByDefault => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private static bool EscapesAllowed => !OperatingSystem.IsWindows();

    /// <summary>Parses and compiles <paramref name="pattern"/>; throws <see cref="ArgumentException"/> when it is invalid.</summary>
    public static GlobPattern Parse(string pattern, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Invalid glob pattern: the pattern is empty.");
        }

        if (pattern.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid glob pattern: the pattern contains a NUL character.");
        }

        var normalized = OperatingSystem.IsWindows() ? pattern.Replace('\\', '/') : pattern;
        var expanded = new List<string>();
        ExpandBraces(normalized, pattern, expanded);
        var alternatives = expanded.Distinct(StringComparer.Ordinal)
            .Select(p => GlobAlternative.Compile(p, pattern, ignoreCase, EscapesAllowed))
            .ToList();
        return new GlobPattern(pattern, alternatives);
    }

    /// <summary>True when the <c>/</c>-separated <paramref name="relativePath"/> matches any alternative.</summary>
    public bool IsMatch(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var alternative in Alternatives)
        {
            if (alternative.MatchesSegments(parts))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="text"/> contains glob metacharacters (<c>* ? [ {</c>).</summary>
    public static bool ContainsGlobCharacters(string text) => text.AsSpan().IndexOfAny("*?[{") >= 0;

    private static void ExpandBraces(string pattern, string original, List<string> results)
    {
        var escapes = EscapesAllowed;
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (escapes && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '[')
            {
                var classEnd = GlobAlternative.FindClassEnd(pattern, i, escapes);
                i = classEnd < 0 ? i + 1 : classEnd + 1;
                continue;
            }

            if (c == '{')
            {
                var close = FindMatchingBrace(pattern, i, escapes);
                if (close < 0)
                {
                    throw new ArgumentException($"Invalid glob pattern '{original}': unmatched '{{'.");
                }

                var parts = SplitTopLevel(pattern, i + 1, close, escapes);
                if (parts.Count < 2)
                {
                    // "{abc}" without a comma is literal text, as in shells.
                    i = close + 1;
                    continue;
                }

                var prefix = pattern[..i];
                var suffix = pattern[(close + 1)..];
                foreach (var part in parts)
                {
                    ExpandBraces(prefix + part + suffix, original, results);
                    if (results.Count > MaxAlternatives)
                    {
                        throw new ArgumentException($"Invalid glob pattern '{original}': brace expansion produces too many alternatives.");
                    }
                }

                return;
            }

            i++;
        }

        results.Add(pattern);
    }

    private static int FindMatchingBrace(string pattern, int open, bool escapes)
    {
        var depth = 0;
        for (var i = open; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (escapes && c == '\\')
            {
                i++;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevel(string pattern, int start, int end, bool escapes)
    {
        var parts = new List<string>();
        var depth = 0;
        var partStart = start;
        for (var i = start; i < end; i++)
        {
            var c = pattern[i];
            if (escapes && c == '\\')
            {
                i++;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(pattern[partStart..i]);
                partStart = i + 1;
            }
        }

        parts.Add(pattern[partStart..end]);
        return parts;
    }
}

/// <summary>One brace-expanded alternative: a literal directory prefix plus the wildcard segments below it.</summary>
internal sealed class GlobAlternative
{
    private GlobAlternative(string literalPrefix, IReadOnlyList<GlobSegment> segments, IReadOnlyList<GlobSegment> allSegments)
    {
        LiteralPrefix = literalPrefix;
        Segments = segments;
        AllSegments = allSegments;
    }

    /// <summary>
    /// The leading wildcard-free part (may be absolute or contain <c>..</c>); when <see cref="Segments"/> is empty this is
    /// the whole pattern, naming a single path.
    /// </summary>
    public string LiteralPrefix { get; }

    /// <summary>Segments after <see cref="LiteralPrefix"/>, matched while walking from the prefix directory.</summary>
    public IReadOnlyList<GlobSegment> Segments { get; }

    /// <summary>All segments, for matching relative paths (e.g. grep's glob filter).</summary>
    public IReadOnlyList<GlobSegment> AllSegments { get; }

    public static GlobAlternative Compile(string pattern, string original, bool ignoreCase, bool escapes)
    {
        var rawSegments = pattern.Split('/');
        var firstWildcard = Array.FindIndex(rawSegments, s => HasWildcard(s, escapes));

        var allSegments = rawSegments
            .Where(s => s.Length > 0 && s != ".")
            .Select(s => CompileSegment(s, original, ignoreCase, escapes))
            .ToList();

        if (firstWildcard < 0)
        {
            return new GlobAlternative(Unescape(pattern, escapes), [], allSegments);
        }

        var prefix = string.Join('/', rawSegments[..firstWildcard]);
        if (prefix.Length == 0 && firstWildcard > 0)
        {
            prefix = "/";
        }
        else if (prefix.EndsWith(':'))
        {
            prefix += "/";
        }

        var segments = rawSegments[firstWildcard..]
            .Where(s => s.Length > 0 && s != ".")
            .Select(s => CompileSegment(s, original, ignoreCase, escapes))
            .ToList();
        return new GlobAlternative(Unescape(prefix, escapes), segments, allSegments);
    }

    /// <summary>Matches path segments against <see cref="AllSegments"/>.</summary>
    public bool MatchesSegments(IReadOnlyList<string> parts)
    {
        var states = GlobSearch.Closure(AllSegments, [0]);
        foreach (var part in parts)
        {
            states = GlobSearch.Step(AllSegments, states, part);
            if (states.Count == 0)
            {
                return false;
            }
        }

        return states.Contains(AllSegments.Count);
    }

    /// <summary>Index of the <c>]</c> closing the class opened at <paramref name="open"/>, or -1.</summary>
    internal static int FindClassEnd(string text, int open, bool escapes)
    {
        var i = open + 1;
        if (i < text.Length && (text[i] == '!' || text[i] == '^'))
        {
            i++;
        }

        if (i < text.Length && text[i] == ']')
        {
            i++;
        }

        for (; i < text.Length; i++)
        {
            if (escapes && text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == ']')
            {
                return i;
            }
            else if (text[i] == '/')
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool HasWildcard(string segment, bool escapes)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (escapes && c == '\\')
            {
                i++;
            }
            else if (c is '*' or '?' or '[')
            {
                return true;
            }
        }

        return false;
    }

    private static string Unescape(string text, bool escapes)
    {
        if (!escapes || !text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private static GlobSegment CompileSegment(string segment, string original, bool ignoreCase, bool escapes)
    {
        if (segment == "**")
        {
            return GlobSegment.GlobStar;
        }

        if (!HasWildcard(segment, escapes))
        {
            if (escapes && segment.EndsWith('\\') && !segment.EndsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid glob pattern '{original}': dangling escape character.");
            }

            return GlobSegment.Literal(Unescape(segment, escapes), ignoreCase);
        }

        var regex = new StringBuilder("^");
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            switch (c)
            {
                case '\\' when escapes:
                    if (i + 1 >= segment.Length)
                    {
                        throw new ArgumentException($"Invalid glob pattern '{original}': dangling escape character.");
                    }

                    regex.Append(Regex.Escape(segment[++i].ToString()));
                    break;
                case '*':
                    while (i + 1 < segment.Length && segment[i + 1] == '*')
                    {
                        i++;
                    }

                    regex.Append(".*");
                    break;
                case '?':
                    regex.Append('.');
                    break;
                case '[':
                    var end = FindClassEnd(segment, i, escapes);
                    if (end < 0)
                    {
                        throw new ArgumentException($"Invalid glob pattern '{original}': unclosed '['.");
                    }

                    regex.Append(ConvertClass(segment, i, end, escapes));
                    i = end;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        regex.Append('$');
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return GlobSegment.FromRegex(new Regex(regex.ToString(), options, TimeSpan.FromSeconds(1)));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid glob pattern '{original}': {ex.Message}", ex);
        }
    }

    private static string ConvertClass(string segment, int open, int close, bool escapes)
    {
        var sb = new StringBuilder("[");
        var i = open + 1;
        if (segment[i] is '!' or '^')
        {
            sb.Append('^');
            i++;
        }

        for (; i < close; i++)
        {
            var c = segment[i];
            if (escapes && c == '\\' && i + 1 < close)
            {
                c = segment[++i];
                sb.Append('\\').Append(c);
                continue;
            }

            if (c is '\\' or '[' or ']' or '^')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append(']').ToString();
    }
}

/// <summary>One path segment of a glob: <c>**</c>, a literal name, or a wildcard expression.</summary>
internal sealed class GlobSegment
{
    private readonly Regex? _regex;
    private readonly string? _literal;
    private readonly StringComparison _comparison;

    private GlobSegment(Regex? regex, string? literal, StringComparison comparison, bool isGlobStar)
    {
        _regex = regex;
        _literal = literal;
        _comparison = comparison;
        IsGlobStar = isGlobStar;
    }

    /// <summary>The <c>**</c> segment (matches zero or more path segments).</summary>
    public static GlobSegment GlobStar { get; } = new(null, null, StringComparison.Ordinal, true);

    public bool IsGlobStar { get; }

    public static GlobSegment Literal(string name, bool ignoreCase) =>
        new(null, name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal, false);

    public static GlobSegment FromRegex(Regex regex) => new(regex, null, StringComparison.Ordinal, false);

    public bool IsMatch(string name)
    {
        if (IsGlobStar)
        {
            return true;
        }

        return _literal is not null ? string.Equals(name, _literal, _comparison) : _regex!.IsMatch(name);
    }
}

/// <summary>Walks directories matching a <see cref="GlobPattern"/> (an NFA over path segments, pruning dead branches).</summary>
internal static class GlobSearch
{
    /// <summary>
    /// Finds files matching <paramref name="glob"/> below <paramref name="baseDirectory"/>. Returns display paths (relative
    /// to <paramref name="workingDirectory"/> when inside it), newest first, ties broken by path.
    /// </summary>
    public static IReadOnlyList<string> Find(GlobPattern glob, string baseDirectory, string workingDirectory, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, DateTime>(PathDisplay.Comparer);
        foreach (var alternative in glob.Alternatives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = alternative.LiteralPrefix.Length == 0
                ? baseDirectory
                : Path.GetFullPath(Path.Combine(baseDirectory, alternative.LiteralPrefix));
            if (alternative.Segments.Count == 0)
            {
                if (File.Exists(start))
                {
                    found.TryAdd(start, File.GetLastWriteTimeUtc(start));
                }

                continue;
            }

            if (Directory.Exists(start))
            {
                Walk(new DirectoryInfo(start), alternative.Segments, Closure(alternative.Segments, [0]), found, cancellationToken);
            }
        }

        return found
            .Select(kv => (Display: PathDisplay.ForDisplay(kv.Key, workingDirectory), Time: kv.Value))
            .OrderByDescending(x => x.Time)
            .ThenBy(x => x.Display, StringComparer.Ordinal)
            .Select(x => x.Display)
            .ToList();
    }

    internal static HashSet<int> Step(IReadOnlyList<GlobSegment> segments, HashSet<int> states, string name)
    {
        var next = new HashSet<int>();
        foreach (var state in states)
        {
            if (state >= segments.Count)
            {
                continue;
            }

            if (segments[state].IsGlobStar)
            {
                next.Add(state);
            }
            else if (segments[state].IsMatch(name))
            {
                next.Add(state + 1);
            }
        }

        return Closure(segments, next);
    }

    internal static HashSet<int> Closure(IReadOnlyList<GlobSegment> segments, HashSet<int> states)
    {
        var pending = new Stack<int>(states);
        while (pending.Count > 0)
        {
            var state = pending.Pop();
            if (state < segments.Count && segments[state].IsGlobStar && states.Add(state + 1))
            {
                pending.Push(state + 1);
            }
        }

        return states;
    }

    private static void Walk(
        DirectoryInfo directory,
        IReadOnlyList<GlobSegment> segments,
        HashSet<int> states,
        Dictionary<string, DateTime> found,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in DirectoryWalker.ListEntries(directory))
        {
            var next = Step(segments, states, entry.Name);
            if (next.Count == 0)
            {
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                if (!DirectoryWalker.IsVcsDirectory(subdirectory.Name) && !DirectoryWalker.IsLink(subdirectory) &&
                    next.Any(s => s < segments.Count))
                {
                    Walk(subdirectory, segments, next, found, cancellationToken);
                }
            }
            else if (next.Contains(segments.Count))
            {
                found.TryAdd(entry.FullName, entry.LastWriteTimeUtc);
            }
        }
    }
}

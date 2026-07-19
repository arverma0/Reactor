using System.Globalization;
using System.Text;

namespace Reactor.Agent.Environments;

/// <summary>Grep output modes.</summary>
internal enum GrepOutputMode
{
    Content,
    FilesWithMatches,
    Count,
}

/// <summary>Parsing helpers for <see cref="GrepOptions.OutputMode"/>.</summary>
internal static class GrepModes
{
    public const string Content = "content";
    public const string FilesWithMatches = "files_with_matches";
    public const string Count = "count";

    public static GrepOutputMode Parse(string? mode) => (mode ?? Content).Trim().ToLowerInvariant() switch
    {
        "" or Content => GrepOutputMode.Content,
        FilesWithMatches => GrepOutputMode.FilesWithMatches,
        Count => GrepOutputMode.Count,
        _ => throw new ArgumentException(
            $"Invalid grep output mode '{mode}'; expected '{Content}', '{FilesWithMatches}', or '{Count}'."),
    };
}

/// <summary>
/// Accumulates grep results in the shared output format, enforcing the result limit. Content lines are
/// <c>path:line:text</c> (context lines <c>path-line-text</c>, non-adjacent groups separated by <c>--</c> when context is
/// requested); files mode prints one path per line; count mode prints <c>path:N</c>. When more results exist than the limit
/// allows, a final <c>[results truncated at N]</c> line is added.
/// </summary>
internal sealed class GrepCollector
{
    /// <summary>Displayed lines longer than this are cut.</summary>
    public const int MaxLineLength = 2000;

    private readonly StringBuilder _output = new();
    private readonly GrepOutputMode _mode;
    private readonly int _maxResults;
    private readonly int _contextAfter;
    private readonly bool _hasContext;
    private int _results;
    private string? _lastFile;
    private int _lastLine;
    private int _lastMatchLine;
    private bool _anyLine;

    public GrepCollector(GrepOutputMode mode, int maxResults, int contextBefore, int contextAfter)
    {
        _mode = mode;
        _maxResults = maxResults <= 0 ? int.MaxValue : maxResults;
        _contextAfter = Math.Max(0, contextAfter);
        _hasContext = mode == GrepOutputMode.Content && (contextBefore > 0 || contextAfter > 0);
    }

    /// <summary>True once a result beyond the limit was seen; producers should stop.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>True when at least one result was recorded.</summary>
    public bool HasResults => _results > 0;

    public GrepOutputMode Mode => _mode;

    public void AddMatch(string file, int lineNumber, string text)
    {
        if (IsComplete)
        {
            return;
        }

        if (_results >= _maxResults)
        {
            IsComplete = true;
            return;
        }

        AppendLine(file, lineNumber, text, ':');
        _results++;
        _lastMatchLine = lineNumber;
    }

    public void AddContext(string file, int lineNumber, string text)
    {
        if (IsComplete)
        {
            return;
        }

        // Once the limit is reached only the trailing context of the final match is shown.
        if (_results >= _maxResults &&
            (!string.Equals(file, _lastFile, StringComparison.Ordinal) || lineNumber != _lastLine + 1 ||
             lineNumber > _lastMatchLine + _contextAfter))
        {
            return;
        }

        AppendLine(file, lineNumber, text, '-');
    }

    public void AddFile(string file)
    {
        if (TryReserve())
        {
            _output.Append(file).Append('\n');
        }
    }

    public void AddCount(string file, long count)
    {
        if (TryReserve())
        {
            _output.Append(file).Append(':').Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    /// <summary>The formatted results, or an empty string when nothing matched.</summary>
    public string Build()
    {
        if (_output.Length == 0)
        {
            return "";
        }

        var text = _output.ToString().TrimEnd('\n');
        return IsComplete
            ? text + "\n[results truncated at " + _maxResults.ToString(CultureInfo.InvariantCulture) + "]"
            : text;
    }

    /// <summary>Cuts overly long lines for display.</summary>
    public static string Clip(string line)
    {
        if (line.Length <= MaxLineLength)
        {
            return line;
        }

        var cut = char.IsHighSurrogate(line[MaxLineLength - 1]) ? MaxLineLength - 1 : MaxLineLength;
        return line[..cut] + "... [line truncated]";
    }

    private bool TryReserve()
    {
        if (IsComplete)
        {
            return false;
        }

        if (_results >= _maxResults)
        {
            IsComplete = true;
            return false;
        }

        _results++;
        return true;
    }

    private void AppendLine(string file, int lineNumber, string text, char separator)
    {
        if (_hasContext && _anyLine && (!string.Equals(file, _lastFile, StringComparison.Ordinal) || lineNumber != _lastLine + 1))
        {
            _output.Append("--\n");
        }

        _output.Append(file).Append(separator).Append(lineNumber.ToString(CultureInfo.InvariantCulture)).Append(separator)
            .Append(text).Append('\n');
        _lastFile = file;
        _lastLine = lineNumber;
        _anyLine = true;
    }
}

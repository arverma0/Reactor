using System.Text;

namespace Reactor.Agent.Tools;

/// <summary>A matched span in the original text.</summary>
/// <param name="Start">Start index (inclusive).</param>
/// <param name="End">End index (exclusive).</param>
internal readonly record struct TextSpan(int Start, int End);

/// <summary>Text normalized for fuzzy matching, with a map from normalized positions back to the original text.</summary>
internal sealed class NormalizedText
{
    private readonly int[] _starts;
    private readonly int[] _ends;

    public NormalizedText(string text, int[] starts, int[] ends)
    {
        Text = text;
        _starts = starts;
        _ends = ends;
    }

    public string Text { get; }

    /// <summary>The original span covering normalized characters [<paramref name="start"/>, start + length).</summary>
    public TextSpan ToOriginal(int start, int length) => new(_starts[start], _ends[start + length - 1]);
}

/// <summary>
/// Exact and fuzzy text matching shared by the editing tools. Fuzzy matching ignores CRLF/LF differences, trailing
/// whitespace on each line, and typographic punctuation (curly quotes, dashes, ellipsis, non-breaking spaces), while
/// always reporting spans of the original text so edits apply to what is actually in the file.
/// </summary>
internal static class TextMatching
{
    /// <summary>The ASCII equivalent used for fuzzy matching, or null when the character is kept as is.</summary>
    public static string? Equivalent(char c) => c switch
    {
        '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u2032' => "'",
        '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\u2033' => "\"",
        '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => "-",
        '\u2026' => "...",
        '\u00A0' or (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000' => " ",
        _ => null,
    };

    /// <summary>Replaces typographic punctuation and exotic spaces with ASCII equivalents.</summary>
    public static string NormalizePunctuation(string text)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var equivalent = Equivalent(text[i]);
            if (equivalent is null)
            {
                sb?.Append(text[i]);
                continue;
            }

            sb ??= new StringBuilder(text.Length + 8).Append(text, 0, i);
            sb.Append(equivalent);
        }

        return sb?.ToString() ?? text;
    }

    /// <summary>Non-overlapping ordinal occurrences of <paramref name="needle"/>.</summary>
    public static List<TextSpan> FindExact(string haystack, string needle)
    {
        var spans = new List<TextSpan>();
        if (needle.Length == 0)
        {
            return spans;
        }

        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            spans.Add(new TextSpan(index, index + needle.Length));
        }

        return spans;
    }

    /// <summary>
    /// Exact occurrences of <paramref name="needle"/>, also trying the needle with its line endings converted to
    /// <paramref name="lineEnding"/> (models usually send LF even for CRLF files).
    /// </summary>
    public static List<TextSpan> FindExactAdaptingLineEndings(string haystack, string needle, string lineEnding)
    {
        var spans = FindExact(haystack, needle);
        if (spans.Count == 0 && needle.Contains('\n', StringComparison.Ordinal))
        {
            var adapted = Environments.TextFileDecoding.NormalizeLineEndings(needle, lineEnding);
            if (!string.Equals(adapted, needle, StringComparison.Ordinal))
            {
                spans = FindExact(haystack, adapted);
            }
        }

        return spans;
    }

    /// <summary>Occurrences of <paramref name="needle"/> after normalizing both texts (see <see cref="Normalize"/>).</summary>
    public static List<TextSpan> FindFuzzy(string haystack, string needle)
    {
        var spans = new List<TextSpan>();
        var normalizedNeedle = Normalize(needle).Text;
        if (normalizedNeedle.Length == 0)
        {
            return spans;
        }

        var normalized = Normalize(haystack);
        for (var index = normalized.Text.IndexOf(normalizedNeedle, StringComparison.Ordinal);
             index >= 0;
             index = normalized.Text.IndexOf(normalizedNeedle, index + normalizedNeedle.Length, StringComparison.Ordinal))
        {
            spans.Add(normalized.ToOriginal(index, normalizedNeedle.Length));
        }

        return spans;
    }

    /// <summary>
    /// Normalizes <paramref name="text"/>: CRLF becomes LF, trailing whitespace on each line is dropped, and typographic
    /// punctuation is replaced by ASCII equivalents. A normalized newline maps back to the original line's trailing
    /// whitespace plus its terminator.
    /// </summary>
    public static NormalizedText Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var starts = new List<int>(text.Length);
        var ends = new List<int>(text.Length);
        var position = 0;
        while (position <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', position);
            var hasNewline = lineEnd >= 0;
            if (!hasNewline)
            {
                lineEnd = text.Length;
            }

            var contentEnd = lineEnd;
            while (contentEnd > position && IsTrailingWhitespace(text[contentEnd - 1]))
            {
                contentEnd--;
            }

            for (var j = position; j < contentEnd; j++)
            {
                var equivalent = Equivalent(text[j]);
                if (equivalent is null)
                {
                    sb.Append(text[j]);
                    starts.Add(j);
                    ends.Add(j + 1);
                }
                else
                {
                    foreach (var ch in equivalent)
                    {
                        sb.Append(ch);
                        starts.Add(j);
                        ends.Add(j + 1);
                    }
                }
            }

            if (!hasNewline)
            {
                break;
            }

            sb.Append('\n');
            starts.Add(contentEnd);
            ends.Add(lineEnd + 1);
            position = lineEnd + 1;
        }

        return new NormalizedText(sb.ToString(), starts.ToArray(), ends.ToArray());
    }

    /// <summary>Replaces <paramref name="spans"/> (sorted, non-overlapping) in <paramref name="text"/>.</summary>
    public static string ReplaceSpans(string text, IReadOnlyList<TextSpan> spans, string replacement)
    {
        var sb = new StringBuilder(text.Length + (spans.Count * Math.Max(0, replacement.Length)));
        var position = 0;
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            sb.Append(text, position, span.Start - position);
            sb.Append(replacement);
            position = span.End;
        }

        sb.Append(text, position, text.Length - position);
        return sb.ToString();
    }

    private static bool IsTrailingWhitespace(char c) => c is ' ' or '\t' or '\r' or '\f' or '\v' || Equivalent(c) == " ";
}

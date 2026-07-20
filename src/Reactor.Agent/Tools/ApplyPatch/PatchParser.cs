using System.Text.RegularExpressions;

namespace Reactor.Agent.Tools.ApplyPatch;

/// <summary>
/// Parses the v4a patch format (<c>*** Begin Patch</c> ... <c>*** End Patch</c> with Add/Delete/Update File operations).
/// It is lenient where models commonly deviate: CRLF input, a missing final newline, a surrounding shell heredoc
/// (<c>apply_patch &lt;&lt;'EOF'</c> ... <c>EOF</c>) or markdown fence, a first hunk without an <c>@@</c> line, blank
/// context lines that lost their leading space, and unified-diff style <c>@@ -1,3 +1,4 @@</c> headers.
/// </summary>
internal static partial class PatchParser
{
    public const string BeginPatch = "*** Begin Patch";
    public const string EndPatch = "*** End Patch";
    public const string AddFile = "*** Add File:";
    public const string DeleteFile = "*** Delete File:";
    public const string UpdateFile = "*** Update File:";
    public const string MoveTo = "*** Move to:";
    public const string EndOfFile = "*** End of File";

    /// <summary>Parses <paramref name="patch"/>; throws <see cref="PatchFormatException"/> with the line number on errors.</summary>
    public static IReadOnlyList<PatchOperation> Parse(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var start = 0;
        var end = lines.Length;
        TrimBlankLines(lines, ref start, ref end);
        if (start >= end)
        {
            throw new PatchFormatException(1, "The patch is empty.");
        }

        var heredoc = HeredocStart().Match(lines[start]);
        if (heredoc.Success)
        {
            start++;
            TrimBlankLines(lines, ref start, ref end);
            if (end > start && lines[end - 1].Trim() == heredoc.Groups["tag"].Value)
            {
                end--;
            }
        }
        else if (lines[start].Trim() is "apply_patch" or "applypatch")
        {
            start++;
        }

        TrimBlankLines(lines, ref start, ref end);
        if (start < end && lines[start].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            start++;
            TrimBlankLines(lines, ref start, ref end);
            if (end > start && lines[end - 1].Trim() == "```")
            {
                end--;
            }

            TrimBlankLines(lines, ref start, ref end);
        }

        if (start >= end || lines[start].Trim() != BeginPatch)
        {
            throw new PatchFormatException(Math.Min(start, lines.Length - 1) + 1, $"The patch must start with '{BeginPatch}'.");
        }

        if (end - 1 <= start || lines[end - 1].Trim() != EndPatch)
        {
            throw new PatchFormatException(end, $"The patch must end with '{EndPatch}'.");
        }

        return ParseOperations(lines, start + 1, end - 1);
    }

    private static List<PatchOperation> ParseOperations(string[] lines, int first, int last)
    {
        var operations = new List<PatchOperation>();
        var i = first;
        while (i < last)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (IsBlank(line) || IsPatchMarker(line))
            {
                i++;
            }
            else if (trimmed.StartsWith(AddFile, StringComparison.Ordinal))
            {
                operations.Add(ParseAdd(lines, ref i, last, HeaderPath(trimmed, AddFile, i)));
            }
            else if (trimmed.StartsWith(DeleteFile, StringComparison.Ordinal))
            {
                var path = HeaderPath(trimmed, DeleteFile, i);
                operations.Add(new PatchOperation(PatchOperationKind.Delete, path, i + 1));
                i++;
                while (i < last && !IsOperationHeader(lines[i].TrimStart()))
                {
                    if (!IsBlank(lines[i]))
                    {
                        throw new PatchFormatException(i + 1, $"Unexpected content after '{DeleteFile} {path}'.");
                    }

                    i++;
                }
            }
            else if (trimmed.StartsWith(UpdateFile, StringComparison.Ordinal))
            {
                operations.Add(ParseUpdate(lines, ref i, last, HeaderPath(trimmed, UpdateFile, i)));
            }
            else
            {
                throw new PatchFormatException(i + 1,
                    $"Unexpected line '{Shorten(line)}'. Expected '{AddFile} <path>', '{DeleteFile} <path>', " +
                    $"'{UpdateFile} <path>', or '{EndPatch}'.");
            }
        }

        return operations;
    }

    private static PatchOperation ParseAdd(string[] lines, ref int i, int last, string path)
    {
        var header = i + 1;
        i++;
        var content = new List<string>();
        var pendingBlanks = 0;
        // Added lines all start with '+', so an indented header or marker can only end the section.
        while (i < last && !IsOperationHeader(lines[i].TrimStart()))
        {
            var line = lines[i];
            if (line.StartsWith('+'))
            {
                // Blank lines without the '+' prefix are kept only when more content follows.
                for (; pendingBlanks > 0; pendingBlanks--)
                {
                    content.Add("");
                }

                content.Add(line[1..]);
            }
            else if (IsBlank(line))
            {
                pendingBlanks++;
            }
            else
            {
                throw new PatchFormatException(i + 1, $"Every line of an added file must start with '+' (in '{AddFile} {path}').");
            }

            i++;
        }

        return new PatchOperation(PatchOperationKind.Add, path, header) { AddedLines = content };
    }

    private static PatchOperation ParseUpdate(string[] lines, ref int i, int last, string path)
    {
        var header = i + 1;
        i++;
        string? moveTo = null;
        while (i < last && IsBlank(lines[i]))
        {
            i++;
        }

        if (i < last && lines[i].TrimStart().StartsWith(MoveTo, StringComparison.Ordinal))
        {
            moveTo = lines[i].TrimStart()[MoveTo.Length..].Trim();
            if (moveTo.Length == 0)
            {
                throw new PatchFormatException(i + 1, $"'{MoveTo}' requires a destination path.");
            }

            i++;
        }

        var hunks = new List<PatchHunk>();
        List<PatchLine>? current = null;
        var hints = new List<string>();
        var hunkLine = 0;
        var trailingBareBlanks = 0;

        void Flush(bool endOfFile, int lineNumber)
        {
            if (current is null)
            {
                if (endOfFile)
                {
                    throw new PatchFormatException(lineNumber, $"'{EndOfFile}' must follow the lines of a hunk.");
                }

                return;
            }

            // Bare blank lines at the end of a hunk are usually separators, not context.
            current.RemoveRange(current.Count - trailingBareBlanks, trailingBareBlanks);
            if (current.Count == 0)
            {
                throw new PatchFormatException(hunkLine,
                    $"Empty hunk in '{UpdateFile} {path}': after '@@', add the lines to change, prefixed with ' ' (unchanged), " +
                    "'-' (removed), or '+' (added).");
            }

            hunks.Add(new PatchHunk(hints, current, endOfFile, hunkLine));
            current = null;
            hints = [];
            trailingBareBlanks = 0;
        }

        while (i < last && !IsOperationHeader(lines[i]) && !IsIndentedMarkerEndingOperation(lines, i, last))
        {
            var line = lines[i];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var hint = ParseHint(line);
                if (current is not null && current.Count == trailingBareBlanks)
                {
                    // Consecutive "@@" lines narrow the location (e.g. a class, then a method in it).
                    current.Clear();
                    trailingBareBlanks = 0;
                }
                else
                {
                    Flush(false, i + 1);
                    current = [];
                    hunkLine = i + 1;
                }

                if (hint is not null)
                {
                    hints.Add(hint);
                }

                i++;
                continue;
            }

            if (line.Trim() == EndOfFile)
            {
                Flush(true, i + 1);
                i++;
                continue;
            }

            if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            PatchLine patchLine;
            var bare = false;
            if (line.Length == 0)
            {
                patchLine = new PatchLine(PatchLineKind.Context, "");
                bare = true;
            }
            else if (line.StartsWith("***", StringComparison.Ordinal))
            {
                throw new PatchFormatException(i + 1,
                    $"Unexpected line '{Shorten(line)}' in '{UpdateFile} {path}'. Hunk lines start with ' ', '-', or '+'; " +
                    $"file operations start with '{AddFile}', '{UpdateFile}', or '{DeleteFile}'.");
            }
            else
            {
                patchLine = line[0] switch
                {
                    ' ' => new PatchLine(PatchLineKind.Context, line[1..]),
                    '-' => new PatchLine(PatchLineKind.Delete, line[1..]),
                    '+' => new PatchLine(PatchLineKind.Add, line[1..]),
                    _ => throw new PatchFormatException(i + 1,
                        $"Invalid hunk line '{Shorten(line)}': each line must start with ' ' (context), '-' (removed), or '+' (added)."),
                };
            }

            if (current is null)
            {
                if (bare)
                {
                    i++;
                    continue;
                }

                // A first hunk without an "@@" line.
                current = [];
                hints = [];
                hunkLine = i + 1;
            }

            current.Add(patchLine);
            trailingBareBlanks = bare ? trailingBareBlanks + 1 : 0;
            i++;
        }

        Flush(false, i + 1);
        if (hunks.Count == 0 && moveTo is null)
        {
            throw new PatchFormatException(header, $"'{UpdateFile} {path}' contains no hunks.");
        }

        return new PatchOperation(PatchOperationKind.Update, path, header) { MoveTo = moveTo, Hunks = hunks };
    }

    private static string? ParseHint(string line)
    {
        var hint = line[2..].Trim();
        var unified = UnifiedHunkHeader().Match(hint);
        if (unified.Success)
        {
            hint = unified.Groups["rest"].Value.Trim();
        }

        if (hint.EndsWith("@@", StringComparison.Ordinal))
        {
            hint = hint[..^2].Trim();
        }

        return hint.Length == 0 ? null : hint;
    }

    private static string HeaderPath(string trimmedLine, string prefix, int index)
    {
        var path = trimmedLine[prefix.Length..].Trim();
        if (path.Length == 0)
        {
            throw new PatchFormatException(index + 1, $"'{prefix}' requires a file path.");
        }

        return path;
    }

    /// <summary>
    /// True for lines that end the current operation: another operation's header, or a stray <c>*** End Patch</c> /
    /// <c>*** Begin Patch</c> between operations (models sometimes concatenate patches; those markers are skipped).
    /// </summary>
    private static bool IsOperationHeader(string line) =>
        line.StartsWith(AddFile, StringComparison.Ordinal) ||
        line.StartsWith(DeleteFile, StringComparison.Ordinal) ||
        line.StartsWith(UpdateFile, StringComparison.Ordinal) ||
        line.TrimEnd() is EndPatch or BeginPatch;

    private static bool IsPatchMarker(string line) => line.Trim() is EndPatch or BeginPatch;

    /// <summary>
    /// True for a space-prefixed <c>*** End Patch</c> (a common model slip) followed only by blank lines before the next
    /// operation or the end of the patch. It would otherwise be read as a context line; at worst, treating it as a marker
    /// drops one trailing context line from the hunk.
    /// </summary>
    private static bool IsIndentedMarkerEndingOperation(string[] lines, int i, int last)
    {
        if (!IsPatchMarker(lines[i]))
        {
            return false;
        }

        for (var j = i + 1; j < last; j++)
        {
            if (!IsBlank(lines[j]))
            {
                return IsOperationHeader(lines[j].TrimStart());
            }
        }

        return true;
    }

    private static bool IsBlank(string line) => line.Trim().Length == 0;

    private static void TrimBlankLines(string[] lines, ref int start, ref int end)
    {
        while (start < end && IsBlank(lines[start]))
        {
            start++;
        }

        while (end > start && IsBlank(lines[end - 1]))
        {
            end--;
        }
    }

    private static string Shorten(string line) => line.Length <= 80 ? line : line[..80] + "...";

    [GeneratedRegex("""^\s*(?:apply_patch|applypatch)?\s*<<-?\s*(['"]?)(?<tag>[A-Za-z_][A-Za-z0-9_]*)\1\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex HeredocStart();

    [GeneratedRegex(@"^-\d+(?:,\d+)?\s+\+\d+(?:,\d+)?\s*@@\s?(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnifiedHunkHeader();
}

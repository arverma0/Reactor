using System.Globalization;
using System.Text;
using Reactor.Agent.Environments;

namespace Reactor.Agent.Tools.ApplyPatch;

/// <summary>
/// Applies parsed patch operations atomically: every hunk of every file is located and the resulting contents computed
/// before anything is written, so a failing hunk leaves all files untouched. Hunks are located in order, each at or after
/// the end of the previous one; an <c>@@</c> hint first moves the search to the next line containing it. Matching tries,
/// in order: exact lines, ignoring trailing whitespace, ignoring surrounding whitespace, and normalizing typographic
/// punctuation. Context lines keep the file's own text, and each file keeps its line-ending style, trailing newline, and
/// UTF-8 byte order mark.
/// </summary>
internal static class PatchApplier
{
    private static readonly Func<string, string>[] Passes =
    [
        static s => s,
        static s => s.TrimEnd(),
        static s => s.Trim(),
        static s => TextMatching.NormalizePunctuation(s).Trim(),
    ];

    /// <summary>Applies <paramref name="operations"/> and returns the summary ("Success. Updated the following files: ...").</summary>
    public static async Task<string> ApplyAsync(
        IReadOnlyList<PatchOperation> operations, IExecutionEnvironment environment, CancellationToken cancellationToken)
    {
        var state = new PatchState(environment, cancellationToken);
        var summary = new List<string>();
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = environment.ResolvePath(operation.Path);
            switch (operation.Kind)
            {
                case PatchOperationKind.Add:
                    if (await state.ExistsAsync(fullPath).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            $"Cannot add '{operation.Path}' (patch line {operation.LineNumber}): the file already exists; use " +
                            "*** Update File to modify it.");
                    }

                    var added = operation.AddedLines.Count == 0 ? "" : string.Join("\n", operation.AddedLines) + "\n";
                    await state.SetAsync(fullPath, new FileContent(added, false)).ConfigureAwait(false);
                    summary.Add("A " + operation.Path);
                    break;

                case PatchOperationKind.Delete:
                    if (!await state.ExistsAsync(fullPath).ConfigureAwait(false))
                    {
                        throw new FileNotFoundException(
                            $"Cannot delete '{operation.Path}' (patch line {operation.LineNumber}): file not found.", fullPath);
                    }

                    await state.SetAsync(fullPath, null).ConfigureAwait(false);
                    summary.Add("D " + operation.Path);
                    break;

                case PatchOperationKind.Update:
                    var current = await state.LoadAsync(fullPath).ConfigureAwait(false) ?? throw new FileNotFoundException(
                        $"Cannot update '{operation.Path}' (patch line {operation.LineNumber}): file not found. To create a new " +
                        $"file, use '*** Add File: {operation.Path}' and prefix every line of its content with '+'.", fullPath);
                    var updated = ApplyHunks(current, operation);
                    if (operation.MoveTo is { } moveTo && !PathDisplay.Comparer.Equals(environment.ResolvePath(moveTo), fullPath))
                    {
                        var destination = environment.ResolvePath(moveTo);
                        if (await state.ExistsAsync(destination).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"Cannot move '{operation.Path}' to '{moveTo}' (patch line {operation.LineNumber}): the destination already exists.");
                        }

                        await state.SetAsync(destination, updated).ConfigureAwait(false);
                        await state.SetAsync(fullPath, null).ConfigureAwait(false);
                        summary.Add($"M {operation.Path} -> {moveTo}");
                    }
                    else
                    {
                        await state.SetAsync(fullPath, updated).ConfigureAwait(false);
                        summary.Add("M " + operation.Path);
                    }

                    break;
            }
        }

        await state.CommitAsync().ConfigureAwait(false);
        return "Success. Updated the following files:\n" + string.Join("\n", summary);
    }

    /// <summary>Applies the hunks of an Update operation to <paramref name="file"/>.</summary>
    internal static FileContent ApplyHunks(FileContent file, PatchOperation operation)
    {
        var text = file.Text;
        var lineEnding = TextFileDecoding.DetectLineEnding(text);
        var endsWithNewline = text.Length == 0 || TextFileDecoding.EndsWithNewline(text);
        var lines = TextFileDecoding.SplitLines(text);
        var replacements = new List<(int Start, int OldCount, List<string> NewLines)>();
        var cursor = 0;

        for (var h = 0; h < operation.Hunks.Count; h++)
        {
            var hunk = operation.Hunks[h];
            var oldLines = hunk.OldLines();
            var hintIndex = FindHints(lines, hunk.ContextHints, cursor);
            int start;
            if (oldLines.Count == 0)
            {
                if (hintIndex < 0 && hunk.ContextHints.Count > 0 && !hunk.IsEndOfFile)
                {
                    throw new InvalidOperationException(NotFoundMessage(operation, h, hunk, oldLines, hintIndex));
                }

                start = hunk.IsEndOfFile || hintIndex < 0 ? lines.Count : hintIndex + 1;
            }
            else
            {
                start = -1;
                if (hunk.IsEndOfFile)
                {
                    start = MatchAtEnd(lines, oldLines, cursor);
                }

                if (start < 0 && hintIndex >= 0)
                {
                    start = Find(lines, oldLines, hintIndex);
                }

                if (start < 0)
                {
                    start = Find(lines, oldLines, cursor);
                }

                if (start < 0)
                {
                    throw new InvalidOperationException(NotFoundMessage(operation, h, hunk, oldLines, hintIndex));
                }
            }

            var newLines = new List<string>();
            var oldIndex = 0;
            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case PatchLineKind.Context:
                        newLines.Add(lines[start + oldIndex]);
                        oldIndex++;
                        break;
                    case PatchLineKind.Delete:
                        oldIndex++;
                        break;
                    default:
                        newLines.Add(line.Text);
                        break;
                }
            }

            replacements.Add((start, oldLines.Count, newLines));
            cursor = start + oldLines.Count;
        }

        for (var r = replacements.Count - 1; r >= 0; r--)
        {
            var (start, oldCount, newLines) = replacements[r];
            lines.RemoveRange(start, oldCount);
            lines.InsertRange(start, newLines);
        }

        var result = string.Join(lineEnding, lines);
        if (endsWithNewline && lines.Count > 0)
        {
            result += lineEnding;
        }

        return file with { Text = result };
    }

    private static int Find(List<string> lines, List<string> pattern, int from)
    {
        foreach (var normalize in Passes)
        {
            var target = pattern.Select(normalize).ToList();
            for (var i = Math.Max(0, from); i + target.Count <= lines.Count; i++)
            {
                if (MatchesAt(lines, target, i, normalize))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int MatchAtEnd(List<string> lines, List<string> pattern, int from)
    {
        var start = lines.Count - pattern.Count;
        if (start < Math.Max(0, from))
        {
            return -1;
        }

        foreach (var normalize in Passes)
        {
            if (MatchesAt(lines, pattern.Select(normalize).ToList(), start, normalize))
            {
                return start;
            }
        }

        return -1;
    }

    private static bool MatchesAt(List<string> lines, List<string> target, int start, Func<string, string> normalize)
    {
        for (var j = 0; j < target.Count; j++)
        {
            if (!string.Equals(normalize(lines[start + j]), target[j], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The line of the innermost hint, finding each hint after the previous one; -1 when there are none or one is missing.</summary>
    private static int FindHints(List<string> lines, IReadOnlyList<string> hints, int from)
    {
        var index = -1;
        foreach (var hint in hints)
        {
            index = FindHint(lines, hint, from);
            if (index < 0)
            {
                return -1;
            }

            from = index + 1;
        }

        return index;
    }

    private static int FindHint(List<string> lines, string hint, int from)
    {
        for (var i = Math.Max(0, from); i < lines.Count; i++)
        {
            if (lines[i].Contains(hint, StringComparison.Ordinal))
            {
                return i;
            }
        }

        var normalizedHint = CollapseWhitespace(TextMatching.NormalizePunctuation(hint));
        for (var i = Math.Max(0, from); i < lines.Count; i++)
        {
            if (CollapseWhitespace(TextMatching.NormalizePunctuation(lines[i])).Contains(normalizedHint, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inSpace = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace)
                {
                    sb.Append(' ');
                }

                inSpace = true;
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }

        return sb.ToString();
    }

    private static string NotFoundMessage(PatchOperation operation, int hunkIndex, PatchHunk hunk, List<string> oldLines, int hintIndex)
    {
        var sb = new StringBuilder()
            .Append("Failed to apply hunk ").Append((hunkIndex + 1).ToString(CultureInfo.InvariantCulture))
            .Append(" of ").Append(operation.Hunks.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" to '").Append(operation.Path).Append("' (patch line ")
            .Append(hunk.LineNumber.ToString(CultureInfo.InvariantCulture)).Append(')');
        if (oldLines.Count == 0)
        {
            var hints = string.Join(" > ", hunk.ContextHints.Select(h => $"'@@ {h}'"));
            return sb.Append(": the context hint ").Append(hints)
                .Append(" was not found, so there is nowhere to insert the added lines.\nNo files were changed. ")
                .Append("Re-read the file and use an @@ line copied from it (or a few unchanged context lines) to anchor the addition.")
                .ToString();
        }

        if (hunk.ContextHints.Count > 0)
        {
            var hints = string.Join(" > ", hunk.ContextHints.Select(h => $"'@@ {h}'"));
            sb.Append(hintIndex < 0
                ? $"; the context hint {hints} was not found either"
                : $" after the context hint {hints}");
        }

        sb.Append(". Could not find these lines in the file:\n").AppendJoin('\n', oldLines)
            .Append("\nNo files were changed. Re-read the file and regenerate the patch with exact context lines.");
        return sb.ToString();
    }

    /// <summary>Decoded file text plus whether it carried a UTF-8 byte order mark.</summary>
    internal sealed record FileContent(string Text, bool HasBom);

    /// <summary>
    /// The virtual file system for one patch: pending contents (null = deleted) plus snapshots of the original files so a
    /// failed write can be rolled back.
    /// </summary>
    private sealed class PatchState(IExecutionEnvironment environment, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, FileContent?> _pending = new(PathDisplay.Comparer);
        private readonly Dictionary<string, Snapshot> _originals = new(PathDisplay.Comparer);

        public async Task<bool> ExistsAsync(string fullPath) =>
            _pending.TryGetValue(fullPath, out var pending)
                ? pending is not null
                : await environment.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);

        /// <summary>The current (possibly already patched) content of a text file, or null when it does not exist.</summary>
        public async Task<FileContent?> LoadAsync(string fullPath)
        {
            if (_pending.TryGetValue(fullPath, out var pending))
            {
                return pending;
            }

            var snapshot = await ReadSnapshotAsync(fullPath).ConfigureAwait(false);
            if (snapshot.Existed && snapshot.Content is null)
            {
                throw new InvalidDataException($"Cannot patch binary file: {fullPath}");
            }

            return snapshot.Content;
        }

        public async Task SetAsync(string fullPath, FileContent? content)
        {
            if (!_originals.ContainsKey(fullPath))
            {
                _originals[fullPath] = await ReadSnapshotAsync(fullPath).ConfigureAwait(false);
            }

            _pending[fullPath] = content;
        }

        public async Task CommitAsync()
        {
            var written = new List<string>();
            foreach (var (fullPath, content) in _pending)
            {
                try
                {
                    await WriteAsync(fullPath, content).ConfigureAwait(false);
                    written.Add(fullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await RollbackAsync(written).ConfigureAwait(false);
                    throw new IOException(
                        $"Failed to write {fullPath}: {ex.Message}. Files already written by this patch were restored.", ex);
                }
            }
        }

        private async Task RollbackAsync(IEnumerable<string> written)
        {
            foreach (var fullPath in written)
            {
                var original = _originals[fullPath];
                try
                {
                    if (!original.Existed)
                    {
                        await environment.DeleteFileAsync(fullPath, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (original.Content is not null)
                    {
                        await WriteAsync(fullPath, original.Content).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Best effort: keep restoring the remaining files.
                }
            }
        }

        // Writes are not cancellable so that a commit is never left half-done by cancellation.
        private Task WriteAsync(string fullPath, FileContent? content) =>
            content is null
                ? environment.DeleteFileAsync(fullPath, CancellationToken.None)
                : FileToolHelpers.SaveTextAsync(environment, fullPath, content.Text, content.HasBom, CancellationToken.None);

        private async Task<Snapshot> ReadSnapshotAsync(string fullPath)
        {
            if (!await environment.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                return new Snapshot(false, null);
            }

            var bytes = await environment.ReadFileBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (TextFileDecoding.IsBinary(bytes))
            {
                return new Snapshot(true, null);
            }

            var decoded = TextFileDecoding.Decode(bytes);
            return new Snapshot(true, new FileContent(decoded.Text, decoded.HasUtf8Bom));
        }

        /// <summary>An original file: whether it existed and its text (null for binary files, which cannot be restored).</summary>
        private sealed record Snapshot(bool Existed, FileContent? Content);
    }
}

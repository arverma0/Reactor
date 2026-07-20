namespace Reactor.Agent.Tools.ApplyPatch;

/// <summary>The kind of file operation in a patch.</summary>
internal enum PatchOperationKind
{
    Add,
    Delete,
    Update,
}

/// <summary>The kind of a hunk line.</summary>
internal enum PatchLineKind
{
    Context,
    Delete,
    Add,
}

/// <summary>One line of a hunk (without its prefix character).</summary>
internal readonly record struct PatchLine(PatchLineKind Kind, string Text);

/// <summary>A hunk of an Update File operation.</summary>
/// <param name="ContextHints">
/// Text after each <c>@@</c> used to locate the change, outermost first: consecutive <c>@@</c> lines narrow the location
/// (e.g. a class, then a method in it), each searched after the previous one.
/// </param>
/// <param name="Lines">Context, deleted, and added lines in patch order.</param>
/// <param name="IsEndOfFile">True when the hunk was followed by <c>*** End of File</c> (anchored at the end of the file).</param>
/// <param name="LineNumber">1-based line of the hunk in the patch text.</param>
internal sealed record PatchHunk(IReadOnlyList<string> ContextHints, IReadOnlyList<PatchLine> Lines, bool IsEndOfFile, int LineNumber)
{
    /// <summary>The innermost context hint, if any.</summary>
    public string? ContextHint => ContextHints.Count == 0 ? null : ContextHints[^1];

    /// <summary>The lines the hunk expects to find in the file (context and deletions).</summary>
    public List<string> OldLines() => Lines.Where(l => l.Kind != PatchLineKind.Add).Select(l => l.Text).ToList();
}

/// <summary>A file operation in a patch.</summary>
/// <param name="Kind">Add, Delete, or Update.</param>
/// <param name="Path">The path as written in the patch.</param>
/// <param name="LineNumber">1-based line of the operation header in the patch text.</param>
internal sealed record PatchOperation(PatchOperationKind Kind, string Path, int LineNumber)
{
    /// <summary>The new path for an Update with <c>*** Move to:</c>.</summary>
    public string? MoveTo { get; init; }

    /// <summary>The lines of an added file.</summary>
    public IReadOnlyList<string> AddedLines { get; init; } = [];

    /// <summary>The hunks of an Update.</summary>
    public IReadOnlyList<PatchHunk> Hunks { get; init; } = [];
}

/// <summary>A malformed patch; the message includes the offending line number.</summary>
internal sealed class PatchFormatException : FormatException
{
    public PatchFormatException(int lineNumber, string message)
        : base($"Invalid patch (line {lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}): {message}")
    {
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
}

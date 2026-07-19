using System.Globalization;
using System.Text.Json.Nodes;
using Reactor.Agent.Environments;
using Reactor.Llm;

namespace Reactor.Agent.Tools;

/// <summary>
/// The two <c>edit_file</c> variants: Claude Code style (<c>replace_all</c>) and gemini-cli style
/// (<c>expected_replacements</c>). Both try an exact match first (adapting line endings to the file), then a fuzzy match
/// that ignores CRLF/LF differences, trailing whitespace, and typographic punctuation, applying the edit to the span
/// actually present in the file. Replacement text is written with the file's line-ending style.
/// </summary>
internal static class EditFileTools
{
    public const string Name = "edit_file";
    public const string FuzzyNote = " (matched after normalizing whitespace/punctuation)";

    private const string ClaudeDescription =
        "Replace an exact string in a file. `old_string` must match the file's current text exactly, including " +
        "indentation and whitespace, so read the file before editing it. The edit fails if `old_string` is not found, " +
        "or if it occurs more than once while `replace_all` is false; in that case include more surrounding lines to " +
        "make it unique. Set `replace_all` to true to change every occurrence (for example, to rename a variable). " +
        "`new_string` must differ from `old_string`. Use write_file to create new files.";

    private const string GeminiDescription =
        "Replace text within an existing file. By default exactly one occurrence of `old_string` is replaced; when you " +
        "intend to replace several identical occurrences set `expected_replacements` to their exact number. " +
        "`old_string` must match the file's current text exactly, including whitespace and indentation, and should " +
        "include enough surrounding context (a few lines before and after the change) to identify a single location. " +
        "To create a new file, pass an empty `old_string` and the file content as `new_string`.";

    public static RegisteredTool CreateClaude() => new(
        new ToolDefinition(Name, ClaudeDescription, ToolSchema.Object(
            [
                ("file_path", ToolSchema.String("Path of the file to modify (absolute, or relative to the working directory).")),
                ("old_string", ToolSchema.String("The exact text to replace.")),
                ("new_string", ToolSchema.String("The text to replace it with (must be different from old_string).")),
                ("replace_all", ToolSchema.Boolean("Replace every occurrence of old_string instead of requiring a unique match. Defaults to false.")),
            ],
            "file_path", "old_string", "new_string")),
        ExecuteClaudeAsync);

    public static RegisteredTool CreateGemini() => new(
        new ToolDefinition(Name, GeminiDescription, ToolSchema.Object(
            [
                ("file_path", ToolSchema.String("Path of the file to modify (absolute, or relative to the working directory).")),
                ("old_string", ToolSchema.String("The exact text to replace, including several lines of surrounding context. Empty to create a new file.")),
                ("new_string", ToolSchema.String("The replacement text.")),
                ("expected_replacements", ToolSchema.Integer("Number of occurrences to replace. Defaults to 1.")),
            ],
            "file_path", "old_string", "new_string")),
        ExecuteGeminiAsync);

    private static async Task<ToolOutput> ExecuteClaudeAsync(JsonObject arguments, ToolContext context)
    {
        var path = ToolArguments.RequireString(arguments, "file_path");
        var oldString = ToolArguments.RequireString(arguments, "old_string");
        var newString = ToolArguments.RequireString(arguments, "new_string");
        var replaceAll = ToolArguments.OptionalBool(arguments, "replace_all") ?? false;
        if (oldString.Length == 0)
        {
            throw new ArgumentException(
                "old_string must not be empty (use write_file to create files or to replace a file's entire content).");
        }

        EnsureDifferent(oldString, newString);
        var environment = context.Environment;
        var cancellationToken = context.CancellationToken;
        var fullPath = environment.ResolvePath(path);
        var file = await FileToolHelpers.LoadTextAsync(environment, fullPath, cancellationToken).ConfigureAwait(false);

        var (spans, fuzzy) = Locate(file, oldString, allowFuzzy: true);
        if (spans.Count == 0)
        {
            throw new InvalidOperationException(NotFoundMessage(fullPath));
        }

        if (spans.Count > 1 && !replaceAll)
        {
            throw new InvalidOperationException(
                $"old_string matches {spans.Count.ToString(CultureInfo.InvariantCulture)} locations; include more surrounding " +
                "context to make it unique, or set replace_all=true.");
        }

        return await ApplyAsync(environment, fullPath, file, spans, newString, fuzzy, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ToolOutput> ExecuteGeminiAsync(JsonObject arguments, ToolContext context)
    {
        var path = ToolArguments.RequireString(arguments, "file_path");
        var oldString = ToolArguments.RequireString(arguments, "old_string");
        var newString = ToolArguments.RequireString(arguments, "new_string");
        var expected = ToolArguments.OptionalInt(arguments, "expected_replacements") ?? 1;
        if (expected < 1)
        {
            throw new ArgumentException("expected_replacements must be at least 1.");
        }

        var environment = context.Environment;
        var cancellationToken = context.CancellationToken;
        var fullPath = environment.ResolvePath(path);
        if (oldString.Length == 0)
        {
            return await CreateFileAsync(environment, fullPath, newString, cancellationToken).ConfigureAwait(false);
        }

        EnsureDifferent(oldString, newString);
        var file = await FileToolHelpers.LoadTextAsync(environment, fullPath, cancellationToken).ConfigureAwait(false);
        var (spans, fuzzy) = Locate(file, oldString, allowFuzzy: expected == 1);
        if (spans.Count == 0)
        {
            throw new InvalidOperationException(NotFoundMessage(fullPath) +
                $" (expected {expected.ToString(CultureInfo.InvariantCulture)} occurrence(s), found 0)");
        }

        if (spans.Count != expected)
        {
            var actual = spans.Count.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"Expected {expected.ToString(CultureInfo.InvariantCulture)} occurrence(s) of old_string in {fullPath} but " +
                $"found {actual}. Include more surrounding context to identify the intended location, or set " +
                $"expected_replacements to {actual} to replace all of them.");
        }

        return await ApplyAsync(environment, fullPath, file, spans, newString, fuzzy, cancellationToken).ConfigureAwait(false);
    }

    private static (List<TextSpan> Spans, bool Fuzzy) Locate(EditableFile file, string oldString, bool allowFuzzy)
    {
        var spans = TextMatching.FindExactAdaptingLineEndings(file.Text, oldString, file.LineEnding);
        if (spans.Count > 0 || !allowFuzzy)
        {
            return (spans, false);
        }

        spans = TextMatching.FindFuzzy(file.Text, oldString);
        return (spans, spans.Count > 0);
    }

    private static async Task<ToolOutput> ApplyAsync(
        IExecutionEnvironment environment,
        string fullPath,
        EditableFile file,
        IReadOnlyList<TextSpan> spans,
        string newString,
        bool fuzzy,
        CancellationToken cancellationToken)
    {
        var replacement = TextFileDecoding.NormalizeLineEndings(newString, file.LineEnding);
        var updated = TextMatching.ReplaceSpans(file.Text, spans, replacement);
        await FileToolHelpers.SaveTextAsync(environment, fullPath, updated, file.HasBom, cancellationToken).ConfigureAwait(false);
        return $"Replaced {spans.Count.ToString(CultureInfo.InvariantCulture)} occurrence(s) in {fullPath}" + (fuzzy ? FuzzyNote : "");
    }

    private static async Task<ToolOutput> CreateFileAsync(
        IExecutionEnvironment environment, string fullPath, string content, CancellationToken cancellationToken)
    {
        if (await environment.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"old_string is empty but {fullPath} already exists. Provide the text to replace, or use write_file to " +
                "overwrite the whole file.");
        }

        if (await environment.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException($"Cannot create {fullPath}: it is a directory.");
        }

        await environment.WriteFileAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
        return $"Created new file {fullPath}";
    }

    private static void EnsureDifferent(string oldString, string newString)
    {
        if (string.Equals(oldString, newString, StringComparison.Ordinal))
        {
            throw new ArgumentException("old_string and new_string are identical; there is nothing to change.");
        }
    }

    private static string NotFoundMessage(string fullPath) =>
        $"old_string was not found in {fullPath}. It must match the file's current content exactly, including " +
        "whitespace and indentation; read the file again and copy the exact text to replace.";
}

using System.Text.Json.Nodes;
using Reactor.Llm;

namespace Reactor.Agent.Tools.ApplyPatch;

/// <summary><c>apply_patch</c>: applies a v4a patch (add, delete, update, and rename files) atomically.</summary>
internal static class ApplyPatchTool
{
    public const string Name = "apply_patch";

    private const string Description =
        "Apply a patch to create, delete, update, or rename files. The patch must use this format:\n\n" +
        "*** Begin Patch\n" +
        "*** Add File: path/to/new_file.py\n" +
        "+first line of the new file\n" +
        "+second line\n" +
        "*** Update File: path/to/existing.py\n" +
        "*** Move to: path/to/renamed.py   (optional: rename the file)\n" +
        "@@ def some_function():   (optional: a line near the change, to locate it)\n" +
        " unchanged context line (prefix: one space)\n" +
        "-line to remove (prefix: -)\n" +
        "+line to add (prefix: +)\n" +
        " unchanged context line\n" +
        "*** Delete File: path/to/obsolete.py\n" +
        "*** End Patch\n\n" +
        "Show about 3 unchanged lines of context before and after each change so it can be located unambiguously, and " +
        "start a new @@ hunk for each separate change in the same file. Add a line \"*** End of File\" after a hunk that " +
        "must match at the end of the file. Paths are relative to the working directory. The patch is applied atomically: " +
        "if any hunk cannot be matched, no file is changed.";

    public static RegisteredTool Create() => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("patch", ToolSchema.String("The full patch text, from '*** Begin Patch' to '*** End Patch'.")),
            ],
            "patch")),
        ExecuteAsync);

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context)
    {
        // Codex's freeform variant names the argument "input"; accept it as an alias.
        var patch = ToolArguments.OptionalString(arguments, "patch") ?? ToolArguments.OptionalString(arguments, "input")
            ?? throw new ArgumentException("Missing required parameter 'patch'.");
        var operations = PatchParser.Parse(patch);
        if (operations.Count == 0)
        {
            throw new FormatException("Invalid patch: it contains no file operations.");
        }

        return await PatchApplier.ApplyAsync(operations, context.Environment, context.CancellationToken).ConfigureAwait(false);
    }
}

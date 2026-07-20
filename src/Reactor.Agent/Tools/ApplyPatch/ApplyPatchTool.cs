using System.Text.Json.Nodes;
using Reactor.Llm;

namespace Reactor.Agent.Tools.ApplyPatch;

/// <summary><c>apply_patch</c>: applies a v4a patch (add, delete, update, and rename files) atomically.</summary>
internal static class ApplyPatchTool
{
    public const string Name = "apply_patch";

    private const string Description =
        "Apply a patch to create, delete, update, or rename files. Example:\n\n" +
        "*** Begin Patch\n" +
        "*** Add File: src/greet.py\n" +
        "+def greet(name):\n" +
        "+    return f\"Hello, {name}!\"\n" +
        "*** Update File: src/app.py\n" +
        "@@ def main():\n" +
        "     config = load_config()\n" +
        "-    run(config, retries=1)\n" +
        "+    run(config, retries=3)\n" +
        "     print(\"done\")\n" +
        "*** Delete File: src/old_app.py\n" +
        "*** End Patch\n\n" +
        "Rules: the patch starts with '*** Begin Patch' and ends with '*** End Patch', with no other text around them. " +
        "Each file operation starts with '*** Add File: <path>', '*** Update File: <path>', or '*** Delete File: <path>'. " +
        "Every line of an added file starts with '+'. In an update, each change is a hunk that starts with an '@@' line " +
        "(optionally followed by a nearby line copied from the file, such as a function signature) and every hunk line " +
        "starts with ' ' (one space: an unchanged line copied exactly from the file), '-' (a line to remove, copied exactly), " +
        "or '+' (a line to add). Show about 3 unchanged lines before and after each change so it can be located " +
        "unambiguously, and start a new @@ hunk for each separate change in the same file. To narrow down a repeated " +
        "snippet, stack @@ lines (e.g. '@@ class Foo' then '@@     def bar():'). To rename a file, put " +
        "'*** Move to: <new path>' right after its '*** Update File' line. Add a line \"*** End of File\" after a hunk " +
        "that must match at the end of the file. Use *** Add File (not Update) for files that do not exist yet. Paths " +
        "are relative to the working directory. The patch is applied atomically: if any hunk cannot be matched, no file " +
        "is changed.";

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

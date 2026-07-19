using System.Text.Json.Nodes;
using Reactor.Agent.Environments;
using Reactor.Llm;

namespace Reactor.Agent.Tools;

/// <summary><c>grep</c>: regex search over file contents.</summary>
internal static class GrepTool
{
    public const string Name = "grep";
    public const int DefaultMaxResults = 100;
    public const string NoMatches = "No matches found.";

    private const string Description =
        "Search file contents with a regular expression. Searches the working directory recursively by default; pass " +
        "`path` to search a specific directory or file and `glob_filter` (for example \"*.py\" or \"src/**/*.ts\") to " +
        "limit which files are searched. Results are \"path:line:content\" lines with paths relative to the working " +
        "directory, capped at `max_results` (default 100). Version-control directories, node_modules, binary files, and " +
        "files over 10 MB are skipped.";

    private const string OutputModesDescription =
        " Use `output_mode` \"files_with_matches\" to list only the matching files or \"count\" for per-file match " +
        "counts, and `context` to include surrounding lines with each match.";

    public static RegisteredTool Create(bool includeOutputModes)
    {
        var properties = new List<(string, JsonObject)>
        {
            ("pattern", ToolSchema.String("Regular expression to search for (.NET/ripgrep syntax).")),
            ("path", ToolSchema.String("File or directory to search. Defaults to the working directory.")),
            ("glob_filter", ToolSchema.String("Only search files whose names match this glob, e.g. \"*.cs\" or \"src/**/*.{ts,tsx}\".")),
            ("case_insensitive", ToolSchema.Boolean("Match case-insensitively. Defaults to false.")),
            ("max_results", ToolSchema.Integer("Maximum number of results to return. Defaults to 100.")),
        };
        if (includeOutputModes)
        {
            properties.Add(("output_mode", ToolSchema.StringEnum(
                "\"content\" (matching lines, the default), \"files_with_matches\" (file paths only), or \"count\" (matches per file).",
                GrepModes.Content, GrepModes.FilesWithMatches, GrepModes.Count)));
            properties.Add(("context", ToolSchema.Integer("Number of lines to show before and after each match (content mode).")));
        }

        return new RegisteredTool(
            new ToolDefinition(Name, includeOutputModes ? Description + OutputModesDescription : Description,
                ToolSchema.Object(properties, "pattern")),
            (arguments, context) => ExecuteAsync(arguments, context, includeOutputModes));
    }

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context, bool includeOutputModes)
    {
        var pattern = ToolArguments.RequireString(arguments, "pattern");
        if (pattern.Length == 0)
        {
            throw new ArgumentException("pattern must not be empty.");
        }

        var path = ToolArguments.OptionalString(arguments, "path");
        var globFilter = ToolArguments.OptionalString(arguments, "glob_filter");
        var caseInsensitive = ToolArguments.OptionalBool(arguments, "case_insensitive") ?? false;
        var maxResults = ToolArguments.OptionalInt(arguments, "max_results") ?? DefaultMaxResults;
        if (maxResults < 1)
        {
            throw new ArgumentException("max_results must be at least 1.");
        }

        var outputMode = GrepModes.Content;
        var contextLines = 0;
        if (includeOutputModes)
        {
            outputMode = ToolArguments.OptionalString(arguments, "output_mode") is { Length: > 0 } mode ? mode.Trim() : GrepModes.Content;
            GrepModes.Parse(outputMode);
            contextLines = ToolArguments.OptionalInt(arguments, "context") ?? 0;
            if (contextLines < 0)
            {
                throw new ArgumentException("context must not be negative.");
            }
        }

        var environment = context.Environment;
        var target = environment.ResolvePath(string.IsNullOrWhiteSpace(path) ? "." : path);
        var options = new GrepOptions
        {
            GlobFilter = string.IsNullOrWhiteSpace(globFilter) ? null : globFilter.Trim(),
            CaseInsensitive = caseInsensitive,
            MaxResults = maxResults,
            OutputMode = outputMode,
            ContextBefore = contextLines,
            ContextAfter = contextLines,
        };
        var result = await environment.GrepAsync(pattern, target, options, context.CancellationToken).ConfigureAwait(false);
        return result.Length == 0 ? NoMatches : result;
    }
}

/// <summary><c>glob</c>: file name pattern matching, newest first.</summary>
internal static class GlobTool
{
    public const string Name = "glob";
    public const string NoMatches = "No files matched the pattern.";

    private const string Description =
        "Find files whose paths match a glob pattern such as \"**/*.cs\" or \"src/**/*.{ts,tsx}\". Supports `**` (any " +
        "number of directories), `*` and `?` within a name, character classes like `[abc]`, and brace alternatives " +
        "`{a,b}`. The pattern is matched relative to `path` (default: the working directory). Returns matching file " +
        "paths, most recently modified first.";

    public static RegisteredTool Create() => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("pattern", ToolSchema.String("Glob pattern to match, e.g. \"**/*.py\".")),
                ("path", ToolSchema.String("Directory to search from. Defaults to the working directory.")),
            ],
            "pattern")),
        ExecuteAsync);

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context)
    {
        var pattern = ToolArguments.RequireString(arguments, "pattern");
        var path = ToolArguments.OptionalString(arguments, "path");
        var environment = context.Environment;
        var baseDirectory = environment.ResolvePath(string.IsNullOrWhiteSpace(path) ? "." : path);
        var matches = await environment.GlobAsync(pattern, baseDirectory, context.CancellationToken).ConfigureAwait(false);
        return matches.Count == 0 ? NoMatches : string.Join("\n", matches);
    }
}

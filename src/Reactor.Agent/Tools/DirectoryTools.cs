using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Agent.Environments;
using Reactor.Llm;

namespace Reactor.Agent.Tools;

/// <summary><c>list_dir</c>: an indented directory tree.</summary>
internal static class ListDirTool
{
    public const string Name = "list_dir";
    public const int MaxDepth = 5;

    private const string Description =
        "List the contents of a directory as an indented tree. Directories end with \"/\" and files show their size. " +
        "`depth` controls how many levels are expanded (default 1, maximum 5). Version-control internals such as .git " +
        "are not expanded.";

    public static RegisteredTool Create() => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("path", ToolSchema.String("Directory to list. Defaults to the working directory.")),
                ("depth", ToolSchema.Integer("Number of levels to expand (1-5). Defaults to 1.")),
            ])),
        ExecuteAsync);

    /// <summary>Human-readable size: bytes below 1 KB, otherwise KB/MB/GB with one decimal.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context)
    {
        var path = ToolArguments.OptionalString(arguments, "path");
        var depth = ToolArguments.OptionalInt(arguments, "depth") ?? 1;
        if (depth < 1)
        {
            throw new ArgumentException("depth must be at least 1.");
        }

        depth = Math.Min(depth, MaxDepth);
        var environment = context.Environment;
        var cancellationToken = context.CancellationToken;
        var fullPath = environment.ResolvePath(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!await environment.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            if (await environment.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException($"{fullPath} is a file, not a directory.");
            }

            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");
        }

        var entries = await environment.ListDirectoryAsync(fullPath, depth, cancellationToken).ConfigureAwait(false);
        var root = new Node("", true, null);
        foreach (var entry in entries)
        {
            root.Add(entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries), entry);
        }

        var output = new StringBuilder(Path.TrimEndingDirectorySeparator(fullPath)).Append(Path.DirectorySeparatorChar);
        if (root.Children.Count == 0)
        {
            output.Append("\n  (empty directory)");
        }
        else
        {
            root.Render(output, 1);
        }

        return output.ToString();
    }

    private sealed class Node(string name, bool isDirectory, long? size)
    {
        public string Name { get; } = name;

        public bool IsDirectory { get; set; } = isDirectory;

        public long? Size { get; set; } = size;

        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        public void Add(IReadOnlyList<string> parts, DirEntry entry)
        {
            var node = this;
            for (var i = 0; i < parts.Count; i++)
            {
                var last = i == parts.Count - 1;
                if (!node.Children.TryGetValue(parts[i], out var child))
                {
                    child = new Node(parts[i], !last || entry.IsDir, null);
                    node.Children[parts[i]] = child;
                }

                if (last)
                {
                    child.IsDirectory = entry.IsDir;
                    child.Size = entry.Size;
                }

                node = child;
            }
        }

        public void Render(StringBuilder output, int level)
        {
            var ordered = Children.Values
                .OrderBy(c => c.IsDirectory ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.Ordinal);
            foreach (var child in ordered)
            {
                output.Append('\n').Append(' ', level * 2).Append(child.Name);
                if (child.IsDirectory)
                {
                    output.Append('/');
                    child.Render(output, level + 1);
                }
                else if (child.Size is { } size)
                {
                    output.Append(" (").Append(FormatSize(size)).Append(')');
                }
            }
        }
    }
}

/// <summary><c>read_many_files</c>: reads several files (paths, directories, or glob patterns) in one call.</summary>
internal static class ReadManyFilesTool
{
    public const string Name = "read_many_files";

    /// <summary>At most this many files are read per call.</summary>
    public const int MaxFiles = 200;

    private const string Description =
        "Read several files in one call. `paths` may contain file paths, directories (every file beneath them is read), " +
        "and glob patterns such as \"src/**/*.py\". Each file is returned under a \"--- path ---\" header with its plain " +
        "content (no line numbers). Binary and image files are skipped, and entries that match nothing are listed at the " +
        "end. Use read_file when you need line numbers or a specific range of lines.";

    public static RegisteredTool Create() => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("paths", ToolSchema.StringArray("File paths, directories, or glob patterns, relative to the working directory or absolute.")),
            ],
            "paths")),
        ExecuteAsync);

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context)
    {
        var items = ToolArguments.RequireStringArray(arguments, "paths");
        if (items.All(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("paths must contain at least one file path or glob pattern.");
        }

        var environment = context.Environment;
        var cancellationToken = context.CancellationToken;
        var files = new List<string>();
        var seen = new HashSet<string>(PathDisplay.Comparer);
        var unmatched = new List<string>();
        foreach (var raw in items)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var item = raw.Trim();
            var matches = await ExpandAsync(environment, item, cancellationToken).ConfigureAwait(false);
            if (matches.Count == 0)
            {
                unmatched.Add(item);
                continue;
            }

            foreach (var match in matches)
            {
                if (seen.Add(match))
                {
                    files.Add(match);
                }
            }
        }

        if (files.Count == 0)
        {
            return "No files found matching: " + string.Join(", ", unmatched);
        }

        var blocks = new List<string>();
        foreach (var fullPath in files.Take(MaxFiles))
        {
            blocks.Add(await ReadBlockAsync(environment, fullPath, cancellationToken).ConfigureAwait(false));
        }

        var output = new StringBuilder(string.Join("\n", blocks));
        if (files.Count > MaxFiles)
        {
            output.Append("\n[").Append((files.Count - MaxFiles).ToString(CultureInfo.InvariantCulture))
                .Append(" more matching files were not read (limit ").Append(MaxFiles.ToString(CultureInfo.InvariantCulture))
                .Append(" per call); narrow the paths or patterns to read them.]");
        }

        if (unmatched.Count > 0)
        {
            output.Append("\n[No files found for: ").Append(string.Join(", ", unmatched)).Append(']');
        }

        return output.ToString();
    }

    private static async Task<IReadOnlyList<string>> ExpandAsync(IExecutionEnvironment environment, string item, CancellationToken cancellationToken)
    {
        var fullPath = environment.ResolvePath(item);
        if (await environment.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            return [fullPath];
        }

        IReadOnlyList<string> matches;
        if (await environment.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            matches = await environment.GlobAsync("**/*", fullPath, cancellationToken).ConfigureAwait(false);
        }
        else if (GlobPattern.ContainsGlobCharacters(item))
        {
            try
            {
                matches = await environment.GlobAsync(item, environment.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                matches = [];
            }
        }
        else
        {
            matches = [];
        }

        return matches
            .Select(environment.ResolvePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<string> ReadBlockAsync(IExecutionEnvironment environment, string fullPath, CancellationToken cancellationToken)
    {
        var header = "--- " + PathDisplay.ForDisplay(fullPath, environment.WorkingDirectory) + " ---\n";
        byte[] bytes;
        try
        {
            bytes = await environment.ReadFileBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return header + "[Error reading file: " + ex.Message + "]\n";
        }

        var size = bytes.Length.ToString(CultureInfo.InvariantCulture);
        if (ReadFileTool.ImageMediaType(fullPath) is not null)
        {
            return header + "[Skipped image file (" + size + " bytes); use read_file to view it.]\n";
        }

        if (TextFileDecoding.IsBinary(bytes))
        {
            return header + "[Skipped binary file (" + size + " bytes).]\n";
        }

        var text = TextFileDecoding.NormalizeLineEndings(TextFileDecoding.Decode(bytes).Text, "\n");
        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        return header + text + "\n";
    }
}

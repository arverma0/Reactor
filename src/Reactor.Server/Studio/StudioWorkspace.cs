using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Graphs;

namespace Reactor.Server.Studio;

/// <summary>A pipeline file in the workspace.</summary>
public sealed record WorkspaceFile(
    string Path,
    string Name,
    long Size,
    DateTimeOffset Modified,
    bool HasLayout,
    string? GraphName,
    string? Goal,
    int? NodeCount,
    string? Error);

/// <summary>Result of a workspace operation that can fail with an HTTP-mappable reason.</summary>
public sealed record WorkspaceResult<T>(T? Value, int Status, string? Error)
{
    public bool Succeeded => Error is null;

    public static WorkspaceResult<T> Ok(T value) => new(value, 200, null);

    public static WorkspaceResult<T> Fail(int status, string error) => new(default, status, error);
}

/// <summary>A pipeline file's content as read from the workspace.</summary>
public sealed record WorkspaceDocument(string Path, string Dot, JsonNode? Layout, DateTimeOffset Modified);

/// <summary>
/// The Studio's view of the server working directory: lists, reads, and saves pipeline files (<c>.dot</c>) together with
/// their layout sidecars (<c>name.layout.json</c>). Paths are relative to the workspace root, use forward slashes, and
/// may not leave the root or touch hidden/tooling directories.
/// </summary>
public sealed class StudioWorkspace
{
    /// <summary>Directories never listed or written.</summary>
    public static readonly IReadOnlySet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".reactor", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "TestResults", "packages",
    };

    public const string LayoutSuffix = ".layout.json";
    private const int MaxListedFiles = 500;
    private const int MaxDepth = 8;
    private const long MaxSummarizedBytes = 512 * 1024;
    private const int MaxLayoutBytes = 4 * 1024 * 1024;

    public StudioWorkspace(string root, int maxDotBytes)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        MaxDotBytes = maxDotBytes;
    }

    public string Root { get; }

    public int MaxDotBytes { get; }

    /// <summary>The sidecar path for a pipeline file (<c>a/b.dot</c> → <c>a/b.layout.json</c>).</summary>
    public static string LayoutPathFor(string dotPath)
    {
        ArgumentNullException.ThrowIfNull(dotPath);
        return dotPath.EndsWith(".dot", StringComparison.OrdinalIgnoreCase) ? dotPath[..^4] + LayoutSuffix : dotPath + LayoutSuffix;
    }

    /// <summary>All <c>.dot</c> files under the root (depth- and count-limited), sorted by path.</summary>
    public IReadOnlyList<WorkspaceFile> List()
    {
        var files = new List<WorkspaceFile>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((Root, 0));
        while (pending.Count > 0 && files.Count < MaxListedFiles)
        {
            var (directory, depth) = pending.Pop();
            List<string> entries;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(sub);
                    if (depth < MaxDepth && !name.StartsWith('.') && !SkippedDirectories.Contains(name) && !IsReparsePoint(sub))
                    {
                        pending.Push((sub, depth + 1));
                    }
                }

                entries = Directory.EnumerateFiles(directory, "*.dot").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in entries)
            {
                if (files.Count >= MaxListedFiles)
                {
                    break;
                }

                files.Add(Describe(file));
            }
        }

        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads a pipeline file and its layout sidecar (null when absent or unreadable).</summary>
    public WorkspaceResult<WorkspaceDocument> Read(string? relativePath)
    {
        var resolved = Resolve(relativePath, requireDot: true);
        if (!resolved.Succeeded)
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(resolved.Status, resolved.Error!);
        }

        var full = resolved.Value!;
        if (!File.Exists(full))
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(404, $"'{relativePath}' does not exist.");
        }

        var info = new FileInfo(full);
        if (info.Length > MaxDotBytes)
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(413, $"'{relativePath}' is larger than {MaxDotBytes} bytes.");
        }

        var dot = ReadShared(full);
        JsonNode? layout = null;
        var layoutPath = LayoutPathFor(full);
        if (File.Exists(layoutPath) && new FileInfo(layoutPath).Length <= MaxLayoutBytes)
        {
            try
            {
                layout = JsonNode.Parse(ReadShared(layoutPath));
            }
            catch (JsonException)
            {
                layout = null;
            }
        }

        return WorkspaceResult<WorkspaceDocument>.Ok(new WorkspaceDocument(Relative(full), dot, layout, info.LastWriteTimeUtc));
    }

    /// <summary>
    /// Writes a pipeline file (creating directories) and, when <paramref name="layout"/> is not null, its sidecar. With
    /// <paramref name="baseModified"/>, the save fails with 409 if the file changed on disk since that time.
    /// </summary>
    public WorkspaceResult<WorkspaceDocument> Write(string? relativePath, string? dot, JsonNode? layout, DateTimeOffset? baseModified)
    {
        var resolved = Resolve(relativePath, requireDot: true);
        if (!resolved.Succeeded)
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(resolved.Status, resolved.Error!);
        }

        if (dot is null)
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(400, "The 'dot' field is required.");
        }

        if (Encoding.UTF8.GetByteCount(dot) > MaxDotBytes)
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(413, $"The pipeline is larger than {MaxDotBytes} bytes.");
        }

        var layoutText = layout?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (layoutText is not null && Encoding.UTF8.GetByteCount(layoutText) > MaxLayoutBytes)
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(413, $"The layout is larger than {MaxLayoutBytes} bytes.");
        }

        var full = resolved.Value!;
        if (baseModified is { } expected && File.Exists(full))
        {
            var current = new FileInfo(full).LastWriteTimeUtc;
            if (Math.Abs((current - expected.UtcDateTime).TotalMilliseconds) > 1)
            {
                return WorkspaceResult<WorkspaceDocument>.Fail(409, $"'{Relative(full)}' changed on disk since it was opened.");
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            WriteAtomic(full, dot);
            if (layoutText is not null)
            {
                WriteAtomic(LayoutPathFor(full), layoutText + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorkspaceResult<WorkspaceDocument>.Fail(500, $"Could not save '{relativePath}': {ex.Message}");
        }

        return WorkspaceResult<WorkspaceDocument>.Ok(new WorkspaceDocument(Relative(full), dot, layout, new FileInfo(full).LastWriteTimeUtc));
    }

    /// <summary>Resolves a workspace-relative path to a full path inside the root, or explains why it is not allowed.</summary>
    public WorkspaceResult<string> Resolve(string? relativePath, bool requireDot)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return WorkspaceResult<string>.Fail(400, "A 'path' is required.");
        }

        var normalized = relativePath.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal))
        {
            return WorkspaceResult<string>.Fail(400, "Paths must be relative to the workspace.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".." || s.StartsWith('.') || SkippedDirectories.Contains(s)))
        {
            return WorkspaceResult<string>.Fail(400, $"'{relativePath}' is not an allowed workspace path.");
        }

        if (requireDot && !normalized.EndsWith(".dot", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceResult<string>.Fail(400, "Only .dot pipeline files can be opened or saved.");
        }

        var full = Path.GetFullPath(Path.Combine([Root, .. segments]));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceResult<string>.Fail(400, $"'{relativePath}' is outside the workspace.");
        }

        return WorkspaceResult<string>.Ok(full);
    }

    /// <summary>The workspace-relative form (forward slashes) of a full path inside the root.</summary>
    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private WorkspaceFile Describe(string file)
    {
        var info = new FileInfo(file);
        string? name = null, goal = null, error = null;
        int? nodes = null;
        if (info.Length <= MaxSummarizedBytes)
        {
            try
            {
                var graph = DotParser.Parse(ReadShared(file));
                name = graph.Name;
                goal = graph.Goal.Length > 0 ? graph.Goal : null;
                nodes = graph.Nodes.Count;
            }
            catch (DotParseException ex)
            {
                error = ex.Message;
            }
            catch (IOException ex)
            {
                error = ex.Message;
            }
        }

        return new WorkspaceFile(Relative(file), info.Name, info.Length, info.LastWriteTimeUtc, File.Exists(LayoutPathFor(file)), name, goal, nodes, error);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    internal static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

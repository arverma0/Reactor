using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Agent.Environments;
using Reactor.Llm;

namespace Reactor.Agent.Tools;

/// <summary><c>read_file</c>: line-numbered file content, paging with offset/limit, images as image data.</summary>
internal static class ReadFileTool
{
    public const string Name = "read_file";
    public const int DefaultLimit = 2000;
    public const int MaxLineLength = 2000;
    public const string LineTruncatedMarker = "... [line truncated]";

    private const string Description =
        "Read a file from the filesystem and return its contents with line numbers, one line per row formatted as " +
        "\"LINE_NUMBER | content\". By default up to 2000 lines are returned starting at line 1; for large files pass " +
        "`offset` (1-based line number to start at) and `limit` (maximum number of lines) to read a specific range. " +
        "Lines longer than 2000 characters are cut off. Image files (PNG, JPEG, GIF, WebP, BMP) are returned as images; " +
        "other binary files cannot be displayed. Relative paths are resolved against the working directory.";

    private static readonly Dictionary<string, string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };

    public static RegisteredTool Create() => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("file_path", ToolSchema.String("Path of the file to read (absolute, or relative to the working directory).")),
                ("offset", ToolSchema.Integer("1-based line number to start reading from. Defaults to 1.")),
                ("limit", ToolSchema.Integer("Maximum number of lines to return. Defaults to 2000.")),
            ],
            "file_path")),
        ExecuteAsync);

    /// <summary>The image media type for <paramref name="path"/>'s extension, or null when it is not a supported image.</summary>
    public static string? ImageMediaType(string path) =>
        ImageMediaTypes.TryGetValue(Path.GetExtension(path), out var mediaType) ? mediaType : null;

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context)
    {
        var path = ToolArguments.RequireString(arguments, "file_path");
        var offset = ToolArguments.OptionalInt(arguments, "offset");
        var limit = ToolArguments.OptionalInt(arguments, "limit") ?? DefaultLimit;
        if (offset is < 0)
        {
            throw new ArgumentException("offset must be a 1-based line number (1 or greater).");
        }

        if (limit < 1)
        {
            throw new ArgumentException("limit must be at least 1.");
        }

        var environment = context.Environment;
        var cancellationToken = context.CancellationToken;
        var fullPath = environment.ResolvePath(path);
        await FileToolHelpers.EnsureFileAsync(environment, fullPath, cancellationToken).ConfigureAwait(false);
        var bytes = await environment.ReadFileBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        if (ImageMediaType(fullPath) is { } mediaType)
        {
            return new ToolOutput($"Image file: {fullPath} ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)")
            {
                ImageData = bytes,
                ImageMediaType = mediaType,
            };
        }

        if (TextFileDecoding.IsBinary(bytes))
        {
            throw new InvalidDataException($"Cannot display binary file: {fullPath}");
        }

        var lines = TextFileDecoding.SplitLines(TextFileDecoding.Decode(bytes).Text);
        if (lines.Count == 0)
        {
            return "(empty file)";
        }

        var start = Math.Max(1, offset ?? 1);
        if (start > lines.Count)
        {
            throw new ArgumentException(
                $"offset {start.ToString(CultureInfo.InvariantCulture)} is past the end of {fullPath}, which has " +
                $"{lines.Count.ToString(CultureInfo.InvariantCulture)} line(s).");
        }

        var end = (int)Math.Min(lines.Count, (long)start + limit - 1);
        var width = end.ToString(CultureInfo.InvariantCulture).Length;
        var output = new StringBuilder();
        for (var number = start; number <= end; number++)
        {
            if (number > start)
            {
                output.Append('\n');
            }

            output.Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append(" | ").Append(Clip(lines[number - 1]));
        }

        if (end < lines.Count)
        {
            output.Append("\n[... ").Append((lines.Count - end).ToString(CultureInfo.InvariantCulture))
                .Append(" more lines. Use offset=").Append((end + 1).ToString(CultureInfo.InvariantCulture))
                .Append(" to continue reading.]");
        }

        return output.ToString();
    }

    private static string Clip(string line)
    {
        if (line.Length <= MaxLineLength)
        {
            return line;
        }

        var cut = char.IsHighSurrogate(line[MaxLineLength - 1]) ? MaxLineLength - 1 : MaxLineLength;
        return line[..cut] + LineTruncatedMarker;
    }
}

/// <summary><c>write_file</c>: writes a whole file, creating parent directories.</summary>
internal static class WriteFileTool
{
    public const string Name = "write_file";

    private const string Description =
        "Write content to a file, creating it (and any missing parent directories) or replacing its entire contents if it " +
        "already exists. Use it to create new files or to rewrite a file completely; prefer the editing tool for targeted " +
        "changes to existing files. Relative paths are resolved against the working directory.";

    public static RegisteredTool Create() => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("file_path", ToolSchema.String("Path of the file to write (absolute, or relative to the working directory).")),
                ("content", ToolSchema.String("The complete content to write to the file.")),
            ],
            "file_path", "content")),
        ExecuteAsync);

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context)
    {
        var path = ToolArguments.RequireString(arguments, "file_path");
        var content = ToolArguments.RequireString(arguments, "content");
        var environment = context.Environment;
        var cancellationToken = context.CancellationToken;
        var fullPath = environment.ResolvePath(path);
        if (await environment.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException($"Cannot write to {fullPath}: it is a directory.");
        }

        var existed = await environment.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await environment.WriteFileAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetByteCount(content);
        return $"Wrote {bytes.ToString(CultureInfo.InvariantCulture)} bytes to {fullPath} " +
               (existed ? "(overwrote existing file)" : "(created)");
    }
}

/// <summary>Helpers shared by the file tools.</summary>
internal static class FileToolHelpers
{
    /// <summary>Throws clear errors when <paramref name="fullPath"/> is a directory or does not exist.</summary>
    public static async Task EnsureFileAsync(IExecutionEnvironment environment, string fullPath, CancellationToken cancellationToken)
    {
        if (await environment.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await environment.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException($"{fullPath} is a directory, not a file.");
        }

        throw new FileNotFoundException($"File not found: {fullPath}", fullPath);
    }

    /// <summary>Loads a text file for editing: decoded text, whether it had a UTF-8 BOM, and its line-ending style.</summary>
    public static async Task<EditableFile> LoadTextAsync(IExecutionEnvironment environment, string fullPath, CancellationToken cancellationToken)
    {
        await EnsureFileAsync(environment, fullPath, cancellationToken).ConfigureAwait(false);
        var bytes = await environment.ReadFileBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (TextFileDecoding.IsBinary(bytes))
        {
            throw new InvalidDataException($"Cannot edit binary file: {fullPath}");
        }

        var decoded = TextFileDecoding.Decode(bytes);
        return new EditableFile(decoded.Text, decoded.HasUtf8Bom, TextFileDecoding.DetectLineEnding(decoded.Text));
    }

    /// <summary>Writes edited text back, restoring a UTF-8 byte order mark when the original had one.</summary>
    public static Task SaveTextAsync(IExecutionEnvironment environment, string fullPath, string text, bool hasBom, CancellationToken cancellationToken) =>
        environment.WriteFileAsync(fullPath, hasBom ? TextFileDecoding.ByteOrderMark + text : text, cancellationToken);
}

/// <summary>A text file loaded for editing.</summary>
/// <param name="Text">Decoded content.</param>
/// <param name="HasBom">Whether the file started with a UTF-8 byte order mark.</param>
/// <param name="LineEnding">The file's dominant line terminator.</param>
internal sealed record EditableFile(string Text, bool HasBom, string LineEnding);

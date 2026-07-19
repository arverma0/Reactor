namespace Reactor.Llm.Utilities;

/// <summary>MIME type inference and local-file helpers for multimodal content.</summary>
public static class MediaTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".webm"] = "audio/webm",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".json"] = "application/json",
    };

    /// <summary>Infers a MIME type from a path or URL extension, or null when unknown.</summary>
    public static string? FromExtension(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return null;
        }

        var path = pathOrUrl;
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0 && !IsLocalPath(path))
        {
            path = path[..query];
        }

        var ext = Path.GetExtension(path);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var mime) ? mime : null;
    }

    /// <summary>True for image MIME types.</summary>
    public static bool IsImage(string? mediaType) => mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// True when <paramref name="url"/> looks like a local file path: starts with <c>/</c>, <c>./</c>, <c>../</c>, <c>~</c>,
    /// a Windows drive (<c>C:\</c>), a UNC path, or the <c>file://</c> scheme.
    /// </summary>
    public static bool IsLocalPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return url.StartsWith('/') || url.StartsWith("./", StringComparison.Ordinal) || url.StartsWith("../", StringComparison.Ordinal) ||
               url.StartsWith(".\\", StringComparison.Ordinal) || url.StartsWith("..\\", StringComparison.Ordinal) ||
               url.StartsWith('~') || url.StartsWith("\\\\", StringComparison.Ordinal) ||
               (url.Length >= 3 && char.IsAsciiLetter(url[0]) && url[1] == ':' && (url[2] == '\\' || url[2] == '/'));
    }

    /// <summary>Expands <c>~</c> and <c>file://</c> into a filesystem path.</summary>
    public static string ExpandPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(path).LocalPath;
        }

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    /// <summary>Reads a local file and infers its media type from the extension.</summary>
    public static (byte[] Data, string MediaType) LoadLocalFile(string path, string fallbackMediaType)
    {
        var expanded = ExpandPath(path);
        if (!File.Exists(expanded))
        {
            throw new InvalidRequestError($"Local file not found: {path}", provider: null);
        }

        return (File.ReadAllBytes(expanded), FromExtension(expanded) ?? fallbackMediaType);
    }

    /// <summary>Builds a <c>data:</c> URI.</summary>
    public static string ToDataUri(byte[] data, string mediaType) => $"data:{mediaType};base64,{Convert.ToBase64String(data)}";
}

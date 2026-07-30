namespace Reactor.Cli;

/// <summary>Chooses the output format of <c>reactor render</c> from <c>--format</c> and the <c>--output</c> file name.</summary>
internal static class RenderFormat
{
    /// <summary>The format written when neither <c>--format</c> nor the output file name says otherwise.</summary>
    public const string Default = "svg";

    /// <summary>Formats inferred from output file extensions. <c>dot</c> is the display DOT, written without Graphviz.</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".svg"] = "svg",
        [".png"] = "png",
        [".pdf"] = "pdf",
        [".jpg"] = "jpg",
        [".jpeg"] = "jpg",
        [".gif"] = "gif",
        [".json"] = "json",
        [".ps"] = "ps",
        [".eps"] = "eps",
        [".dot"] = "dot",
        [".gv"] = "dot",
    };

    /// <summary>Extensions <c>reactor render</c> recognizes, for messages.</summary>
    public static string KnownExtensions => string.Join(", ", ByExtension.Keys);

    /// <summary>
    /// The format to write: <paramref name="format"/> when given (it must agree with a recognized extension of
    /// <paramref name="output"/>), else the one the extension implies, else <see cref="Default"/> when the output has no
    /// extension or is standard output (<c>-</c>). Any other Graphviz output format may be named with
    /// <paramref name="format"/>; an output file with an unrecognized extension then needs it.
    /// </summary>
    /// <returns>False with <paramref name="error"/> when the format is invalid, disagrees with the output file, or cannot be told.</returns>
    public static bool TryResolve(string? format, string? output, out string resolved, out string? error)
    {
        resolved = Default;
        error = null;
        var explicitFormat = Normalize(format);
        if (explicitFormat is not null && (explicitFormat.Length == 0 || !explicitFormat.All(char.IsAsciiLetterOrDigit)))
        {
            error = $"Invalid --format '{format}': use a Graphviz output format such as svg, png, or pdf, or dot for the display DOT.";
            return false;
        }

        var extension = output is null or "-" ? "" : Path.GetExtension(output);
        var inferred = ByExtension.GetValueOrDefault(extension);
        if (explicitFormat is not null)
        {
            if (inferred is not null && inferred != explicitFormat)
            {
                error = $"--format {explicitFormat} does not match the output file '{output}' ({inferred}). " +
                    $"Name the file *.{explicitFormat}, or drop --format to write {inferred}.";
                return false;
            }

            resolved = explicitFormat;
            return true;
        }

        if (inferred is not null)
        {
            resolved = inferred;
            return true;
        }

        if (extension.Length > 1)
        {
            error = $"Cannot tell the output format from '{output}'. Pass --format, or use one of these extensions: {KnownExtensions}.";
            return false;
        }

        return true;
    }

    /// <summary>The default output path for <paramref name="source"/> (<c>pipeline.svg</c>; <c>pipeline.rendered.dot</c> for dot).</summary>
    public static string DefaultOutput(string source, string format) =>
        Path.ChangeExtension(source, format == "dot" ? ".rendered.dot" : "." + format);

    private static string? Normalize(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "jpeg" => "jpg",
        "gv" => "dot",
        var name => name,
    };
}

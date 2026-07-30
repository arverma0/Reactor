namespace Reactor.Graphs;

/// <summary>How a declared artifact is carried.</summary>
public enum ArtifactKind
{
    /// <summary>The stage's final response text (codergen) or standard output (tool).</summary>
    Text,

    /// <summary>A file the stage writes, relative to the run's working directory.</summary>
    File,
}

/// <summary>One declared output of a stage (an entry of the node's <c>outputs</c> attribute).</summary>
/// <param name="Name">The artifact name (an identifier).</param>
/// <param name="Kind">Text or file.</param>
/// <param name="Path">For file artifacts, the normalized working-directory-relative path with <c>/</c> separators; null for text.</param>
public sealed record ArtifactPin(string Name, ArtifactKind Kind, string? Path)
{
    /// <summary>The wire name of <see cref="Kind"/>: <c>file</c> or <c>text</c>.</summary>
    public string KindName => ArtifactPins.KindName(Kind);
}

/// <summary>A malformed <c>inputs</c>/<c>outputs</c> entry.</summary>
/// <param name="Attribute"><c>inputs</c> or <c>outputs</c>.</param>
/// <param name="Entry">The entry as written (trimmed).</param>
/// <param name="Message">What is wrong with it.</param>
/// <param name="Fix">How to correct it.</param>
public sealed record ArtifactPinError(string Attribute, string Entry, string Message, string Fix);

/// <summary>The artifact pins a node declares. Malformed entries are left out of the lists and reported in <see cref="Errors"/>.</summary>
public sealed record ArtifactDeclarations(IReadOnlyList<string> Inputs, IReadOnlyList<ArtifactPin> Outputs, IReadOnlyList<ArtifactPinError> Errors)
{
    /// <summary>True when the node declares no (valid) inputs or outputs.</summary>
    public bool IsEmpty => Inputs.Count == 0 && Outputs.Count == 0;
}

/// <summary>
/// Reactor extension: typed artifact pins. A codergen or tool node declares the artifacts it consumes
/// (<c>inputs="spec, design"</c>) and produces (<c>outputs="design:docs/DESIGN.md, verdict"</c>): <c>name</c> is a text
/// artifact (the stage's final response, or a tool's stdout) and <c>name:path</c> a file the stage must write, relative to
/// the run's working directory. Produced artifacts are recorded in the context under <c>artifacts.&lt;name&gt;</c>.
/// Unrelated to <c>ArtifactStore</c>, the engine's internal store for large values.
/// </summary>
public static class ArtifactPins
{
    /// <summary>Prefix of the context keys holding artifact records (<c>artifacts.&lt;name&gt;</c>).</summary>
    public const string ContextPrefix = "artifacts.";

    /// <summary>The context key holding the latest record of <paramref name="name"/>.</summary>
    public static string ContextKey(string name) => ContextPrefix + name;

    /// <summary>The wire name of a kind: <c>file</c> or <c>text</c>.</summary>
    public static string KindName(ArtifactKind kind) => kind == ArtifactKind.File ? "file" : "text";

    /// <summary>
    /// True for the handler types that consume and produce artifacts: codergen, tool, and fan-in (whose LLM judge — a fan-in
    /// with a <c>prompt</c> — reads its inputs and records its outputs).
    /// </summary>
    public static bool IsSupportedHandlerType(string handlerType) => handlerType is HandlerTypes.Codergen or HandlerTypes.Tool or HandlerTypes.FanIn;

    /// <summary>True when the node has a non-blank <c>inputs</c> or <c>outputs</c> attribute.</summary>
    public static bool Declares(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return !string.IsNullOrWhiteSpace(node[AttrKeys.Inputs]) || !string.IsNullOrWhiteSpace(node[AttrKeys.Outputs]);
    }

    /// <summary>Parses a node's <c>inputs</c> and <c>outputs</c> attributes.</summary>
    public static ArtifactDeclarations Parse(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Parse(node[AttrKeys.Inputs], node[AttrKeys.Outputs]);
    }

    /// <summary>
    /// Parses <c>inputs</c> (comma-separated artifact names) and <c>outputs</c> (comma-separated <c>name</c> or
    /// <c>name:path</c> entries). Whitespace around entries and around the colon is ignored, and empty entries are skipped.
    /// </summary>
    public static ArtifactDeclarations Parse(string? inputs, string? outputs)
    {
        var errors = new List<ArtifactPinError>();
        var inputNames = new List<string>();
        foreach (var entry in Entries(inputs))
        {
            if (entry.Contains(':', StringComparison.Ordinal))
            {
                errors.Add(new ArtifactPinError(AttrKeys.Inputs, entry, $"inputs entry '{Display(entry)}' has a path; inputs list artifact names only.",
                    "Remove the path: the producing stage's outputs declare where the file is."));
            }
            else if (!AttributeValues.IsIdentifier(entry))
            {
                errors.Add(InvalidName(AttrKeys.Inputs, entry, entry));
            }
            else if (inputNames.Contains(entry, StringComparer.Ordinal))
            {
                errors.Add(new ArtifactPinError(AttrKeys.Inputs, entry, $"inputs lists '{entry}' more than once.", "List each input once."));
            }
            else
            {
                inputNames.Add(entry);
            }
        }

        var pins = new List<ArtifactPin>();
        foreach (var entry in Entries(outputs))
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            var name = colon < 0 ? entry : entry[..colon].Trim();
            if (!AttributeValues.IsIdentifier(name))
            {
                errors.Add(InvalidName(AttrKeys.Outputs, entry, name));
                continue;
            }

            if (pins.Any(p => p.Name == name))
            {
                errors.Add(new ArtifactPinError(AttrKeys.Outputs, entry, $"outputs declares '{name}' more than once.", "Declare each output once per stage."));
                continue;
            }

            if (colon < 0)
            {
                pins.Add(new ArtifactPin(name, ArtifactKind.Text, null));
                continue;
            }

            if (!TryNormalizePath(entry[(colon + 1)..], out var path, out var problem, out var fix))
            {
                errors.Add(new ArtifactPinError(AttrKeys.Outputs, entry, $"outputs entry '{Display(entry)}': {problem}.", fix));
                continue;
            }

            pins.Add(new ArtifactPin(name, ArtifactKind.File, path));
        }

        return new ArtifactDeclarations(inputNames, pins, errors);
    }

    /// <summary>
    /// Normalizes a working-directory-relative file path: <c>/</c> or <c>\</c> separators become <c>/</c>, <c>.</c> segments
    /// are removed, and <c>..</c> segments are resolved. Fails for empty, absolute, or escaping paths, directories,
    /// wildcards, and characters that are invalid in file names.
    /// </summary>
    /// <param name="path">The path as declared.</param>
    /// <param name="normalized">The normalized path (empty on failure).</param>
    /// <param name="problem">On failure, what is wrong (e.g. <c>the path leads outside the working directory</c>).</param>
    public static bool TryNormalizePath(string path, out string normalized, out string? problem) =>
        TryNormalizePath(path, out normalized, out problem, out _);

    private static bool TryNormalizePath(string path, out string normalized, out string? problem, out string fix)
    {
        ArgumentNullException.ThrowIfNull(path);
        normalized = "";
        path = path.Trim();
        (problem, fix) = (null, "");
        if (path.Length == 0)
        {
            (problem, fix) = ("the path is empty", "Write name:path (e.g. design:docs/DESIGN.md), or only the name for a text artifact.");
            return false;
        }

        if (path[0] is '/' or '\\' || path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            (problem, fix) = ("the path is not relative to the working directory", "Use a path relative to the working directory, e.g. docs/DESIGN.md.");
            return false;
        }

        if (path.IndexOfAny(['*', '?']) >= 0)
        {
            (problem, fix) = ("the path contains a wildcard; an output is a single file", "Declare each file as its own output.");
            return false;
        }

        if (path.Any(char.IsControl))
        {
            (problem, fix) = ("the path contains a control character (in DOT strings \\n, \\t, and \\r are escapes)", "Use / as the path separator.");
            return false;
        }

        if (path.IndexOfAny(['<', '>', ':', '"', '|']) >= 0)
        {
            (problem, fix) = ("the path contains a character that is not valid in file names", "Remove < > : \" | from the path.");
            return false;
        }

        if (path[^1] is '/' or '\\')
        {
            (problem, fix) = ("the path names a directory; an output is a file", "Name a file inside the directory.");
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    (problem, fix) = ("the path leads outside the working directory", "Keep output files inside the working directory.");
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            (problem, fix) = ("the path does not name a file", "Name a file, e.g. docs/DESIGN.md.");
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    private static ArtifactPinError InvalidName(string attribute, string entry, string name) => new(
        attribute,
        entry,
        name.Length == 0
            ? $"{attribute} entry '{Display(entry)}' has no artifact name."
            : $"{attribute} entry '{Display(entry)}': '{Display(name)}' is not a valid artifact name.",
        attribute == AttrKeys.Outputs && name.IndexOfAny(['/', '\\', '.']) >= 0
            ? "Name the artifact and give the path after a colon, e.g. design:docs/DESIGN.md."
            : "Artifact names are identifiers: letters, digits, and underscores, not starting with a digit.");

    private static string Display(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static IEnumerable<string> Entries(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

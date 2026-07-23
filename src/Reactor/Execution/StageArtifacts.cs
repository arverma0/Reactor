using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Reactor.Events;
using Reactor.Graphs;

namespace Reactor.Execution;

/// <summary>
/// A produced artifact as recorded in the context under <c>artifacts.&lt;name&gt;</c> (see <see cref="ArtifactPins"/>).
/// The context holds the JSON-compatible form (<see cref="ToContextValue"/>): an object with <c>name</c>, <c>kind</c>,
/// <c>path</c>, <c>text</c>, <c>producer</c>, <c>version</c>, <c>bytes</c>, <c>sha256</c>, and <c>produced_at</c>.
/// </summary>
public sealed record ArtifactRecord
{
    public required string Name { get; init; }

    /// <summary><c>file</c> or <c>text</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>File artifacts: the working-directory-relative path with <c>/</c> separators.</summary>
    public string? Path { get; init; }

    /// <summary>Text artifacts: the text, truncated to <see cref="StageArtifacts.MaxRecordedTextChars"/> characters with a marker.</summary>
    public string? Text { get; init; }

    /// <summary>Id of the node that produced this version (empty for values seeded into the context).</summary>
    public string Producer { get; init; } = "";

    /// <summary>How many times the artifact has been produced in this run, this time included (0 for seeded values).</summary>
    public int Version { get; init; }

    /// <summary>Size of the file, or of the full (untruncated) text in UTF-8.</summary>
    public long Bytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the file or the full text; null for files over 16 MB or files that could not be read.</summary>
    public string? Sha256 { get; init; }

    public DateTimeOffset? ProducedAt { get; init; }

    public bool IsFile => Kind == "file";

    /// <summary>The JSON-compatible value stored in the context (all fields present; null where not applicable).</summary>
    public Dictionary<string, object?> ToContextValue() => new(StringComparer.Ordinal)
    {
        ["name"] = Name,
        ["kind"] = Kind,
        ["path"] = Path,
        ["text"] = Text,
        ["producer"] = Producer,
        ["version"] = (long)Version,
        ["bytes"] = Bytes,
        ["sha256"] = Sha256,
        ["produced_at"] = ProducedAt?.ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Reads a context value. Objects are read field by field; any other non-null value (for example a string seeded with
    /// <c>--set artifacts.ticket="..."</c>) is a version-0 text artifact without a producer.
    /// </summary>
    public static ArtifactRecord? FromContextValue(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        switch (value)
        {
            case null:
                return null;
            case Dictionary<string, object?> map:
                var path = Field(map, "path");
                var kind = Field(map, "kind") ?? (path is null ? "text" : "file");
                return new ArtifactRecord
                {
                    Name = Field(map, "name") ?? name,
                    Kind = kind == "file" ? "file" : "text",
                    Path = path,
                    Text = Field(map, "text"),
                    Producer = Field(map, "producer") ?? "",
                    Version = (int)Math.Clamp(Number(map, "version") ?? 0, 0, int.MaxValue),
                    Bytes = Number(map, "bytes") ?? 0,
                    Sha256 = Field(map, "sha256"),
                    ProducedAt = DateTimeOffset.TryParse(Field(map, "produced_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null,
                };
            default:
                var text = ContextValues.ToDisplayString(value);
                return new ArtifactRecord { Name = name, Kind = "text", Text = text, Bytes = Encoding.UTF8.GetByteCount(text) };
        }
    }

    private static string? Field(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? ContextValues.ToDisplayString(value) : null;

    private static long? Number(Dictionary<string, object?> map, string key) => map.GetValueOrDefault(key) switch
    {
        long l => l,
        double d => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}

/// <summary>
/// Runtime side of artifact pins (Reactor extension, see <see cref="ArtifactPins"/>): the prompt sections that list a
/// stage's inputs and outputs, the output contract, artifact records, and <see cref="ArtifactProduced"/> events. Used by
/// the codergen and tool handlers; custom handlers can opt in with <see cref="CompleteAsync"/>.
/// </summary>
public static class StageArtifacts
{
    /// <summary>Maximum characters of a text artifact kept in its record (the beginning and the end are kept).</summary>
    public const int MaxRecordedTextChars = 16 * 1024;

    /// <summary>Approximate maximum characters of a text input quoted in a prompt.</summary>
    public const int MaxPromptTextChars = 4000;

    /// <summary>Files larger than this are recorded without a SHA-256 digest.</summary>
    public const long MaxHashedFileBytes = 16L * 1024 * 1024;

    /// <summary>Marks where text was cut.</summary>
    public const string TruncationMarker = "[… truncated]";

    /// <summary>Prefix of the environment variables that expose a tool stage's inputs.</summary>
    public const string ToolInputVariablePrefix = "REACTOR_INPUT_";

    /// <summary>The latest record of <paramref name="name"/> in the context, or null when it has not been produced.</summary>
    public static ArtifactRecord? Read(PipelineContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(name);
        return context.TryGet(ArtifactPins.ContextKey(name), out var value) ? ArtifactRecord.FromContextValue(name, value) : null;
    }

    /// <summary>
    /// The <c>## Inputs</c> and <c>## Outputs</c> prompt sections for a codergen stage, or an empty string when the node
    /// declares neither. Inputs resolve from the context's <c>artifacts.&lt;name&gt;</c> records (the latest version);
    /// files are referenced by path and text is quoted (up to about <see cref="MaxPromptTextChars"/> characters).
    /// </summary>
    public static string PromptSections(Node node, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        var declared = ArtifactPins.Parse(node);
        if (declared.IsEmpty)
        {
            return "";
        }

        var sb = new StringBuilder();
        if (declared.Inputs.Count > 0)
        {
            sb.Append("## Inputs\n");
            foreach (var name in declared.Inputs)
            {
                AppendInput(sb, name, Read(context, name));
            }
        }

        if (declared.Outputs.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append("## Outputs\nProduce these before you finish:\n");
            foreach (var pin in declared.Outputs)
            {
                sb.Append("- `").Append(pin.Name).Append("` → ");
                sb.Append(pin.Kind == ArtifactKind.File
                    ? $"write the file `{pin.Path}` (relative to the working directory)"
                    : "your final response is recorded as this artifact");
                sb.Append('\n');
            }

            var missing = MissingAfterPreviousAttempt(node, context, declared.Outputs);
            if (missing.Count > 0)
            {
                sb.Append("Your previous attempt finished without writing ").Append(string.Join(", ", missing.Select(p => $"`{p.Path}`")))
                    .Append(". Write ").Append(missing.Count == 1 ? "it" : "them").Append(" this time.\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Applies the output contract and records the outputs. Only a <c>success</c> or <c>partial_success</c> outcome is
    /// affected: if a declared file output does not exist under the run's working directory, the outcome becomes FAIL
    /// (<c>Declared output `x` (path) was not written.</c>), which the engine retries and routes like any failure.
    /// Otherwise every output is added to the outcome's context updates as <c>artifacts.&lt;name&gt;</c> (text outputs record
    /// <paramref name="text"/>) and an <see cref="ArtifactProduced"/> event is emitted for each.
    /// </summary>
    /// <param name="node">The stage.</param>
    /// <param name="outcome">The stage's outcome as determined so far.</param>
    /// <param name="context">The run context (records are versioned against it; events go to <see cref="PipelineContext.Run"/>).</param>
    /// <param name="text">The stage's text result: a codergen stage's final response, or a tool's stdout.</param>
    /// <param name="simulated">Simulation mode: file outputs are recorded without requiring the file to exist.</param>
    /// <param name="cancellationToken">Cancels hashing.</param>
    public static async Task<Outcome> CompleteAsync(
        Node node,
        Outcome outcome,
        PipelineContext context,
        string? text,
        bool simulated = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(context);
        if (!outcome.IsSuccessful)
        {
            return outcome;
        }

        var pins = ArtifactPins.Parse(node).Outputs;
        if (pins.Count == 0)
        {
            return outcome;
        }

        var run = context.Run;
        var workingDirectory = WorkingDirectory(context);
        if (!simulated)
        {
            var problems = pins.Where(p => p.Kind == ArtifactKind.File).Select(p => MissingFile(p, workingDirectory)).OfType<string>().ToList();
            if (problems.Count > 0)
            {
                // Routing hints belonged to the outcome the agent reported, not to this failure.
                return outcome with
                {
                    Status = StageStatus.Fail,
                    FailureReason = string.Join(" ", problems),
                    PreferredLabel = "",
                    SuggestedNextIds = [],
                    JumpTo = null,
                };
            }
        }

        var updates = new Dictionary<string, object?>(outcome.ContextUpdates, StringComparer.Ordinal);
        var records = new List<ArtifactRecord>();
        var producedAt = DateTimeOffset.UtcNow;
        foreach (var pin in pins)
        {
            var version = (Read(context, pin.Name)?.Version ?? 0) + 1;
            var record = pin.Kind == ArtifactKind.File
                ? await FileRecordAsync(pin, workingDirectory, cancellationToken).ConfigureAwait(false)
                : TextRecord(pin, text ?? "");
            record = record with { Producer = node.Id, Version = version, ProducedAt = producedAt };
            updates[ArtifactPins.ContextKey(pin.Name)] = record.ToContextValue();
            records.Add(record);
        }

        if (run is not null)
        {
            foreach (var record in records)
            {
                run.Events.Emit(new ArtifactProduced(node.Id, record.Name, record.Kind, record.Path, record.Bytes, record.Version, record.Sha256) { RunId = run.RunId });
            }
        }

        return outcome with { ContextUpdates = updates };
    }

    /// <summary>
    /// Environment variables exposing a tool stage's inputs: <c>REACTOR_INPUT_&lt;NAME&gt;</c> (name upper-cased) is the
    /// absolute path of a file artifact, or of <c>inputs/&lt;name&gt;.txt</c> in the stage directory, which receives a text
    /// artifact's recorded text. Inputs that have not been produced are left unset.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ToolInputEnvironmentAsync(
        Node node,
        PipelineContext context,
        string stageDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stageDir);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ArtifactPins.Parse(node).Inputs)
        {
            if (Read(context, name) is not { } record)
            {
                continue;
            }

            string path;
            if (record is { IsFile: true, Path: { Length: > 0 } relative })
            {
                path = System.IO.Path.GetFullPath(System.IO.Path.Combine(WorkingDirectory(context), relative));
            }
            else
            {
                var directory = System.IO.Path.Combine(stageDir, "inputs");
                Directory.CreateDirectory(directory);
                path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, name + ".txt"));
                await File.WriteAllTextAsync(path, record.Text ?? "", cancellationToken).ConfigureAwait(false);
            }

            environment[ToolInputVariablePrefix + name.ToUpperInvariant()] = path;
        }

        return environment;
    }

    /// <summary>
    /// Shortens <paramref name="text"/> to at most <paramref name="max"/> characters, keeping the beginning and the end
    /// around a <see cref="TruncationMarker"/> line.
    /// </summary>
    public static string Clip(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        const string marker = "\n" + TruncationMarker + "\n";
        var available = Math.Max(0, max - marker.Length);
        var head = available * 3 / 4;
        var tail = available - head;
        if (head > 0 && char.IsHighSurrogate(text[head - 1]))
        {
            head--;
        }

        if (tail > 0 && char.IsLowSurrogate(text[^tail]))
        {
            tail--;
        }

        return text[..head].TrimEnd('\r', '\n') + marker + text[^tail..].TrimStart('\r', '\n');
    }

    private static void AppendInput(StringBuilder sb, string name, ArtifactRecord? record)
    {
        sb.Append("- `").Append(name).Append("` — ");
        if (record is null)
        {
            sb.Append("not produced yet in this run.\n");
            return;
        }

        var origin = new List<string>();
        if (record.Version > 1)
        {
            origin.Add("version " + record.Version.ToString(CultureInfo.InvariantCulture));
        }

        if (record.IsFile)
        {
            if (record.Producer.Length > 0)
            {
                origin.Add($"written by stage `{record.Producer}`");
            }

            sb.Append("file `").Append(record.Path).Append('`');
            if (origin.Count > 0)
            {
                sb.Append(" (").AppendJoin(", ", origin).Append(')');
            }

            sb.Append(". Read it.\n");
            return;
        }

        sb.Append("text");
        if (record.Producer.Length > 0)
        {
            sb.Append(" from stage `").Append(record.Producer).Append('`');
        }

        if (origin.Count > 0)
        {
            sb.Append(" (").AppendJoin(", ", origin).Append(')');
        }

        var text = (record.Text ?? "").ReplaceLineEndings("\n").TrimEnd();
        if (text.Length == 0)
        {
            sb.Append(" (empty).\n");
            return;
        }

        sb.Append(":\n");
        foreach (var line in Clip(text, MaxPromptTextChars).Split('\n'))
        {
            sb.Append(line.Length == 0 ? "  >" : "  > " + line).Append('\n');
        }
    }

    private static List<ArtifactPin> MissingAfterPreviousAttempt(Node node, PipelineContext context, IReadOnlyList<ArtifactPin> outputs)
    {
        // Only during an engine run: preview prompts have no working directory or attempt history.
        if (context.Run is null || !int.TryParse(context.GetString($"internal.retry_count.{node.Id}"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries) || retries < 1)
        {
            return [];
        }

        var workingDirectory = WorkingDirectory(context);
        return outputs.Where(p => p.Kind == ArtifactKind.File && MissingFile(p, workingDirectory) is not null).ToList();
    }

    private static string WorkingDirectory(PipelineContext context) => context.Run?.WorkingDirectory ?? Directory.GetCurrentDirectory();

    private static string? MissingFile(ArtifactPin pin, string workingDirectory)
    {
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workingDirectory, pin.Path!));
        if (File.Exists(fullPath))
        {
            return null;
        }

        return Directory.Exists(fullPath)
            ? $"Declared output `{pin.Name}` ({pin.Path}) is a directory, not a file."
            : $"Declared output `{pin.Name}` ({pin.Path}) was not written.";
    }

    private static async Task<ArtifactRecord> FileRecordAsync(ArtifactPin pin, string workingDirectory, CancellationToken cancellationToken)
    {
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workingDirectory, pin.Path!));
        long bytes = 0;
        string? sha256 = null;
        if (File.Exists(fullPath))
        {
            try
            {
                bytes = new FileInfo(fullPath).Length;
                if (bytes <= MaxHashedFileBytes)
                {
                    var stream = File.OpenRead(fullPath);
                    await using (stream.ConfigureAwait(false))
                    {
                        sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file exists (the contract holds); its digest is best-effort.
                sha256 = null;
            }
        }

        return new ArtifactRecord { Name = pin.Name, Kind = pin.KindName, Path = pin.Path, Bytes = bytes, Sha256 = sha256 };
    }

    private static ArtifactRecord TextRecord(ArtifactPin pin, string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        return new ArtifactRecord
        {
            Name = pin.Name,
            Kind = pin.KindName,
            Text = Clip(text, MaxRecordedTextChars),
            Bytes = utf8.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(utf8)),
        };
    }
}

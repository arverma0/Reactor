using System.Globalization;
using System.Text;
using Reactor.Graphs;

namespace Reactor.Execution;

/// <summary>How an attempt that the engine is about to retry ended.</summary>
public static class AttemptEndings
{
    /// <summary>The stage hit its <c>timeout</c> and was stopped.</summary>
    public const string Timeout = "timeout";

    /// <summary>The handler or backend threw (a transient error).</summary>
    public const string Error = "error";

    /// <summary>The stage reported <c>fail</c>.</summary>
    public const string Fail = "fail";

    /// <summary>The stage asked to be retried.</summary>
    public const string Retry = "retry";
}

/// <summary>
/// Reactor extension: what the engine tells the next attempt of a stage about the previous one (stored in the context as
/// <c>internal.previous_attempt.&lt;node&gt;</c> while the stage is being retried). <see cref="ArchiveDirectory"/> holds the
/// previous attempt's stage files (prompt, response or <c>response.partial.md</c>, <c>backend.json</c>, transcripts).
/// </summary>
public sealed record PreviousAttempt(int Attempt, int MaxAttempts, string Ending, string Reason, long DurationMs, string? ArchiveDirectory)
{
    /// <summary>Context key prefix (followed by the node id).</summary>
    public const string ContextPrefix = "internal.previous_attempt.";

    /// <summary>Most characters of the previous attempt's last message carried into the next prompt.</summary>
    public const int MaxLastMessageChars = 4000;

    public static string ContextKey(string nodeId) => ContextPrefix + nodeId;

    public Dictionary<string, object?> ToContextValue() => new(StringComparer.Ordinal)
    {
        ["attempt"] = (long)Attempt,
        ["max_attempts"] = (long)MaxAttempts,
        ["ending"] = Ending,
        ["reason"] = Reason,
        ["duration_ms"] = DurationMs,
        ["archive"] = ArchiveDirectory,
    };

    /// <summary>The previous attempt recorded for <paramref name="nodeId"/> in <paramref name="context"/>, if any.</summary>
    public static PreviousAttempt? From(PipelineContext context, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Get(ContextKey(nodeId)) is not Dictionary<string, object?> map)
        {
            return null;
        }

        static long Number(object? value) => value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

        var archive = map.GetValueOrDefault("archive") as string;
        return new PreviousAttempt(
            (int)Number(map.GetValueOrDefault("attempt")),
            (int)Number(map.GetValueOrDefault("max_attempts")),
            map.GetValueOrDefault("ending") as string ?? AttemptEndings.Error,
            map.GetValueOrDefault("reason") as string ?? "",
            Number(map.GetValueOrDefault("duration_ms")),
            string.IsNullOrEmpty(archive) ? null : archive);
    }

    /// <summary>
    /// The agent's last words in the previous attempt: <c>response.partial.md</c> (an attempt stopped mid-way) or
    /// <c>response.md</c> from the archive, clipped to its last <see cref="MaxLastMessageChars"/> characters.
    /// </summary>
    public string? ReadLastMessage()
    {
        if (ArchiveDirectory is null)
        {
            return null;
        }

        foreach (var name in new[] { "response.partial.md", "response.md" })
        {
            try
            {
                var path = Path.Combine(ArchiveDirectory, name);
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (text.Length > 0)
                    {
                        return text.Length <= MaxLastMessageChars ? text : "…" + text[^MaxLastMessageChars..];
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>The "Previous attempt" prompt section that hands the stage over to its next attempt.</summary>
    public string PromptSection()
    {
        var sb = new StringBuilder();
        var next = Attempt + 1;
        sb.AppendLine(MaxAttempts > 1
            ? string.Create(CultureInfo.InvariantCulture, $"## Previous attempt (this is attempt {next} of {MaxAttempts})")
            : string.Create(CultureInfo.InvariantCulture, $"## Previous attempt (this is attempt {next})"));
        var took = DurationMs > 0 ? " after " + AttributeValues.FormatDuration(TimeSpan.FromMilliseconds(DurationMs)) : "";
        sb.AppendLine(Ending switch
        {
            AttemptEndings.Timeout => $"The previous attempt of this stage ran out of time{took} and was stopped before it finished; it did not fail.",
            AttemptEndings.Fail => $"The previous attempt of this stage reported failure{took}: {OneLine(Reason)}",
            AttemptEndings.Retry => $"The previous attempt of this stage asked to be retried{took}: {OneLine(Reason)}",
            _ => $"The previous attempt of this stage ended with an error{took}: {OneLine(Reason)}",
        });
        sb.AppendLine("Whatever it changed is still in the working directory. Start by checking what is already done, then continue");
        sb.AppendLine("from there; do not start over or redo finished work.");
        if (Ending == AttemptEndings.Timeout)
        {
            sb.AppendLine("Mind the time limit this time: finish the remaining work, verify it, and end with a summary before it runs out.");
        }

        if (ReadLastMessage() is { } last)
        {
            sb.AppendLine().AppendLine("Its last message was:");
            foreach (var line in last.ReplaceLineEndings("\n").Split('\n'))
            {
                sb.Append("> ").AppendLine(line);
            }
        }

        if (ArchiveDirectory is not null)
        {
            sb.AppendLine().Append("Its full record (prompt, transcript, usage) is in ").Append(ArchiveDirectory).AppendLine(" if you need details.");
        }

        return sb.ToString().ReplaceLineEndings("\n").TrimEnd();
    }

    private static string OneLine(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length == 0 ? "(no reason given)" : single.Length <= 600 ? single : single[..600] + "…";
    }
}

/// <summary>Reactor extension: per-attempt archives of a stage directory.</summary>
public static class StageAttemptArchive
{
    /// <summary>Subdirectory of a stage directory holding archived attempts.</summary>
    public const string DirectoryName = "attempts";

    /// <summary>The archive directory of attempt <paramref name="attempt"/> (1-based).</summary>
    public static string PathFor(string stageDirectory, int attempt) =>
        Path.Combine(stageDirectory, DirectoryName, "attempt-" + attempt.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Moves the stage directory's files (not subdirectories) into the archive of <paramref name="attempt"/>, so the next
    /// attempt starts with fresh prompt, response, status, usage, and transcript files. A pending <c>steering.md</c> stays
    /// for the next attempt. Returns the archive directory, or null when there was nothing to archive or it failed.
    /// </summary>
    public static string? Archive(string stageDirectory, int attempt)
    {
        try
        {
            if (!Directory.Exists(stageDirectory))
            {
                return null;
            }

            var files = Directory.GetFiles(stageDirectory)
                .Where(f => !string.Equals(Path.GetFileName(f), SteeringMessages.FileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count == 0)
            {
                return null;
            }

            var archive = PathFor(stageDirectory, attempt);
            Directory.CreateDirectory(archive);
            foreach (var file in files)
            {
                try
                {
                    File.Move(file, Path.Combine(archive, Path.GetFileName(file)), overwrite: true);
                }
                catch (IOException)
                {
                    // Still held open (e.g. a transcript a backend has not closed yet): keep a copy instead.
                    File.Copy(file, Path.Combine(archive, Path.GetFileName(file)), overwrite: true);
                }
            }

            return archive;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Writes steering messages for a running stage: appends to <c>{stage}/steering.md</c>, which a steerable backend takes and
/// passes to its agent (see <see cref="Handlers.ICodergenBackend.SupportsSteering"/>). The file is held without delete
/// sharing while it is written, so a backend taking it (by renaming it) never sees half a message.
/// </summary>
public static class SteeringMessages
{
    public const string FileName = "steering.md";

    public static async Task<string> AppendAsync(string stageDirectory, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stageDirectory);
        ArgumentNullException.ThrowIfNull(message);
        Directory.CreateDirectory(stageDirectory);
        var path = Path.Combine(stageDirectory, FileName);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var text = (stream.Length > 0 ? "\n" : "") + message + (message.EndsWith('\n') ? "" : "\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
                return path;
            }
            catch (IOException) when (attempt < 10)
            {
                // Another writer (the server, a supervisor) has the file open; try again shortly.
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

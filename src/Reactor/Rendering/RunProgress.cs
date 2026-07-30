using System.Text.Json;
using Reactor.Execution;

namespace Reactor.Rendering;

/// <summary>
/// Where a run stands, read from its run directory: the latest status of every stage that finished and the stages
/// executing right now (pass both to <see cref="GraphRenderer.ToDisplayDot"/>).
/// </summary>
public sealed record RunProgress
{
    /// <summary>The event log in a run directory.</summary>
    public const string EventsFileName = "events.jsonl";

    /// <summary>Latest status per finished stage (wire names: <c>success</c>, <c>partial_success</c>, <c>fail</c>, <c>skipped</c>, …).</summary>
    public IReadOnlyDictionary<string, string> Statuses { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Stages executing now, oldest first: several while parallel branches run, none once the run has ended.</summary>
    public IReadOnlyList<string> Running { get; init; } = [];

    /// <summary>The run has ended (completed, failed, or cancelled).</summary>
    public bool Finished { get; init; }

    /// <summary>
    /// Reads <paramref name="runDirectory"/>: finished stages and their outcomes from <c>checkpoint.json</c>
    /// (<c>completed_nodes</c>, <c>node_outcomes</c>), updated by the <c>StageCompleted</c> events in <c>events.jsonl</c>
    /// (parallel branch stages finish between checkpoints); running stages are those with a <c>StageStarted</c> event and
    /// no later <c>StageCompleted</c> in the current session of the run. A session ends with <c>PipelineCompleted</c> or
    /// <c>PipelineFailed</c> (nothing is running afterwards); a stage left open by an earlier session (a killed process
    /// that was later resumed) is not running. Without <c>events.jsonl</c> no stage is reported as running.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="runDirectory"/> does not exist.</exception>
    /// <exception cref="FileNotFoundException">The directory has neither <c>checkpoint.json</c> nor <c>events.jsonl</c>.</exception>
    /// <exception cref="JsonException"><c>checkpoint.json</c> is not valid JSON.</exception>
    /// <exception cref="IOException">A file could not be read.</exception>
    public static RunProgress Load(string runDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(runDirectory);
        if (!Directory.Exists(runDirectory))
        {
            throw new DirectoryNotFoundException($"Run directory not found: {Path.GetFullPath(runDirectory)}");
        }

        var checkpointPath = Path.Combine(runDirectory, Checkpoint.FileName);
        var eventsPath = Path.Combine(runDirectory, EventsFileName);
        var hasCheckpoint = File.Exists(checkpointPath);
        var hasEvents = File.Exists(eventsPath);
        if (!hasCheckpoint && !hasEvents)
        {
            throw new FileNotFoundException(
                $"{Path.GetFullPath(runDirectory)} has no {Checkpoint.FileName} or {EventsFileName}; is it a run directory?", checkpointPath);
        }

        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var finished = false;
        if (hasCheckpoint)
        {
            var checkpoint = Checkpoint.Load(checkpointPath);
            foreach (var node in checkpoint.CompletedNodes)
            {
                // The engine records every outcome except skipped ones, so a completed stage without one was skipped.
                statuses[node] = checkpoint.NodeOutcomes.TryGetValue(node, out var outcome) ? outcome.Status.ToWire() : "skipped";
            }

            foreach (var (node, outcome) in checkpoint.NodeOutcomes)
            {
                statuses[node] = outcome.Status.ToWire();
            }

            finished = checkpoint.FinalStatus is not null;
        }

        var running = new List<string>();
        if (hasEvents)
        {
            finished = ReadEvents(eventsPath, statuses, running);
        }

        return new RunProgress { Statuses = statuses, Running = running, Finished = finished };
    }

    /// <summary>Applies the event log; returns whether the run's last session has ended.</summary>
    private static bool ReadEvents(string path, Dictionary<string, string> statuses, List<string> running)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var finished = false;
        var completed = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaining = bytes.AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf((byte)'\n');
            var line = end < 0 ? remaining : remaining[..end];
            remaining = end < 0 ? [] : remaining[(end + 1)..];
            if (!TryReadEvent(line, out var type, out var nodeId, out var status))
            {
                // Blank, corrupt, or still being written (a partial last line).
                continue;
            }

            switch (type)
            {
                case "PipelineStarted":
                    // A new session (a resume): stages still open from the previous one were interrupted.
                    running.Clear();
                    finished = false;
                    break;
                case "PipelineCompleted" or "PipelineFailed":
                    running.Clear();
                    finished = true;
                    break;
                case "LoopRestarted":
                    running.Clear();
                    completed.Clear();
                    break;
                case "StageStarted" when nodeId is not null:
                    if (!running.Contains(nodeId, StringComparer.Ordinal))
                    {
                        running.Add(nodeId);
                    }

                    break;
                case "StageCompleted" when nodeId is not null:
                    running.Remove(nodeId);
                    if (!string.IsNullOrEmpty(status))
                    {
                        completed[nodeId] = status;
                    }

                    break;
            }
        }

        foreach (var (node, status) in completed)
        {
            statuses[node] = status;
        }

        return finished;
    }

    /// <summary>Reads the top-level <c>type</c>, <c>node_id</c>, and <c>status</c> of one event line, skipping every other value.</summary>
    private static bool TryReadEvent(ReadOnlySpan<byte> line, out string? type, out string? nodeId, out string? status)
    {
        type = nodeId = status = null;
        if (line.Trim(" \t\r"u8).IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = reader.ValueTextEquals("type"u8) ? 1 : reader.ValueTextEquals("node_id"u8) ? 2 : reader.ValueTextEquals("status"u8) ? 3 : 0;
                if (!reader.Read())
                {
                    return false;
                }

                if (field == 0 || reader.TokenType != JsonTokenType.String)
                {
                    reader.Skip();
                    continue;
                }

                var value = reader.GetString();
                switch (field)
                {
                    case 1:
                        type = value;
                        break;
                    case 2:
                        nodeId = value;
                        break;
                    default:
                        status = value;
                        break;
                }
            }

            return reader.TokenType == JsonTokenType.EndObject && type is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

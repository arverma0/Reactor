using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Reactor.Execution;
using Reactor.Human;

namespace Reactor.Events;

/// <summary>Base type for typed engine events (spec 9.6).</summary>
public abstract record PipelineEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string RunId { get; init; } = "";

    /// <summary>Event type name, e.g. <c>StageStarted</c>.</summary>
    [JsonIgnore]
    public string Type => GetType().Name;

    /// <summary>One-line human-readable description.</summary>
    [JsonIgnore]
    public abstract string Description { get; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>JSON form (snake_case): all properties plus <c>type</c> and <c>description</c>.</summary>
    public JsonObject ToJson()
    {
        var json = JsonSerializer.SerializeToNode(this, GetType(), SerializerOptions) as JsonObject ?? [];
        json["type"] = Type;
        json["description"] = Description;
        return json;
    }
}

public sealed record PipelineStarted(string Name, string Goal, string LogsRoot) : PipelineEvent
{
    public override string Description => $"Pipeline '{Name}' started (run {RunId})";
}

public sealed record PipelineCompleted(TimeSpan Duration, int ArtifactCount, string Status, string Notes) : PipelineEvent
{
    public override string Description => $"Pipeline completed ({Status}) in {Duration.TotalSeconds:0.0}s";
}

public sealed record PipelineFailed(string Error, TimeSpan Duration) : PipelineEvent
{
    public override string Description => $"Pipeline failed after {Duration.TotalSeconds:0.0}s: {Error}";
}

public sealed record StageStarted(string NodeId, string Label, int Index, string HandlerType, int Attempt) : PipelineEvent
{
    public override string Description => Attempt > 1
        ? $"Stage {NodeId} started (attempt {Attempt})"
        : $"Stage {NodeId} started";
}

public sealed record StageCompleted(string NodeId, int Index, TimeSpan Duration, string Status, string Notes) : PipelineEvent
{
    public override string Description => $"Stage {NodeId} completed: {Status} ({Duration.TotalSeconds:0.0}s)";
}

public sealed record StageFailed(string NodeId, int Index, string Error, bool WillRetry) : PipelineEvent
{
    public override string Description => WillRetry ? $"Stage {NodeId} failed (will retry): {Error}" : $"Stage {NodeId} failed: {Error}";
}

public sealed record StageRetrying(string NodeId, int Index, int Attempt, TimeSpan Delay) : PipelineEvent
{
    public override string Description => $"Stage {NodeId} retrying (attempt {Attempt}) after {Delay.TotalMilliseconds:0}ms";
}

/// <summary>Progress/log line from a handler or backend (e.g. an agent's tool call).</summary>
public sealed record StageLog(string NodeId, string Message, string Level = "info") : PipelineEvent
{
    public override string Description => $"[{NodeId}] {Message}";
}

public sealed record EdgeSelected(string From, string To, string Reason, string Label) : PipelineEvent
{
    public override string Description => $"{From} -> {To} ({Reason})";
}

public sealed record GoalGateUnsatisfied(string NodeId, string Status, string? RetryTarget) : PipelineEvent
{
    public override string Description => RetryTarget is null
        ? $"Goal gate {NodeId} unsatisfied ({Status}); no retry target"
        : $"Goal gate {NodeId} unsatisfied ({Status}); jumping to {RetryTarget}";
}

public sealed record LoopRestarted(string TargetNodeId, int RestartCount, string LogsRoot) : PipelineEvent
{
    public override string Description => $"Loop restart #{RestartCount} at {TargetNodeId}";
}

public sealed record ParallelStarted(string NodeId, int BranchCount) : PipelineEvent
{
    public override string Description => $"Parallel {NodeId} started with {BranchCount} branches";
}

public sealed record ParallelBranchStarted(string NodeId, string Branch, int Index) : PipelineEvent
{
    public override string Description => $"Branch {Branch} started";
}

public sealed record ParallelBranchCompleted(string NodeId, string Branch, int Index, TimeSpan Duration, bool Success) : PipelineEvent
{
    public override string Description => $"Branch {Branch} {(Success ? "succeeded" : "failed")} ({Duration.TotalSeconds:0.0}s)";
}

public sealed record ParallelCompleted(string NodeId, TimeSpan Duration, int SuccessCount, int FailureCount) : PipelineEvent
{
    public override string Description => $"Parallel {NodeId} completed: {SuccessCount} succeeded, {FailureCount} failed";
}

public sealed record InterviewStarted(string QuestionId, string Question, string Stage, IReadOnlyList<Option> Options, string QuestionType) : PipelineEvent
{
    public override string Description => $"Question for {Stage}: {Question}";
}

public sealed record InterviewCompleted(string QuestionId, string Question, string Answer, TimeSpan Duration) : PipelineEvent
{
    public override string Description => $"Answered '{Answer}'";
}

public sealed record InterviewTimeout(string QuestionId, string Question, string Stage, TimeSpan Duration) : PipelineEvent
{
    public override string Description => $"Question for {Stage} timed out after {Duration.TotalSeconds:0}s";
}

public sealed record CheckpointSaved(string NodeId, string Path) : PipelineEvent
{
    public override string Description => $"Checkpoint saved after {NodeId}";
}

/// <summary>
/// Reactor extension: one of a stage's declared <c>outputs</c> was recorded in the context as <c>artifacts.&lt;name&gt;</c>
/// (emitted by the engine when it records the stage's outcome, after <see cref="StageCompleted"/>). <see cref="Kind"/> is
/// <c>file</c> or <c>text</c>; <see cref="Path"/> is the working-directory-relative path of a file artifact (null for
/// text); <see cref="Version"/> counts how often the artifact has been produced in this run; <see cref="Sha256"/> is the
/// hex digest of the content (null for files over 16 MB).
/// </summary>
public sealed record ArtifactProduced(string NodeId, string Name, string Kind, string? Path, long Bytes, int Version, string? Sha256) : PipelineEvent
{
    public override string Description => Path is null
        ? $"Artifact {Name} v{Version} produced by {NodeId} ({Bytes} bytes of text)"
        : $"Artifact {Name} v{Version} produced by {NodeId}: {Path} ({Bytes} bytes)";
}

/// <summary>
/// Reactor extension: a stage failed and nothing routed the failure explicitly (no matching condition, preferred label,
/// suggested id, or retry target), so the engine followed an unconditional edge to <see cref="To"/> as the spec requires —
/// the run goes on as if the stage had succeeded. Emitted for top-level stages and for stages inside parallel branches.
/// Set <c>on_fail="stop"</c> on the node (or <c>default_on_fail="stop"</c> on the graph) to end the run instead, or add an
/// <c>outcome=fail</c> edge to handle the failure.
/// </summary>
public sealed record FailedStageContinued(string NodeId, string To, string FailureReason) : PipelineEvent
{
    public override string Description => $"Stage {NodeId} failed; continuing to {To} along an unconditional edge (no failure route)";
}

/// <summary>
/// Reactor extension: a stage reported outcome metrics (numbers describing the product, e.g. <c>qa_failed</c> or
/// <c>visual_min_score</c>) through <c>status.json</c> <c>metrics</c> or a metrics file. Emitted when the engine records the
/// outcome (after <see cref="StageCompleted"/>); the values are also in the context as <c>metrics.&lt;name&gt;</c>.
/// </summary>
public sealed record MetricsRecorded(string NodeId, IReadOnlyDictionary<string, double> Metrics) : PipelineEvent
{
    public override string Description =>
        $"Stage {NodeId} reported metrics: " + string.Join(", ", Metrics.Select(kv => kv.Key + "=" + kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>
/// Reactor extension: the run was about to visit <see cref="NodeId"/> again, but the node has run <see cref="MaxVisits"/>
/// times already (<c>max_visits</c>). The engine continues at <see cref="Target"/> (<c>on_max_visits</c>), followed by an
/// <see cref="EdgeSelected"/> event with reason <c>max_visits</c>, or fails the run when there is no target.
/// </summary>
public sealed record VisitLimitReached(string NodeId, int MaxVisits, string? Target) : PipelineEvent
{
    public override string Description => Target is null
        ? $"Stage {NodeId} reached its visit limit ({MaxVisits}); no on_max_visits target"
        : $"Stage {NodeId} reached its visit limit ({MaxVisits}); continuing at {Target}";
}

/// <summary>
/// Reactor extension: the run's recorded cost reached its budget (<c>max_cost</c> / <see cref="Execution.RunOptions.MaxCostUsd"/>),
/// so it stopped before <see cref="NextNodeId"/>. The checkpoint is left resumable: resume with a higher budget to go on.
/// </summary>
public sealed record BudgetExceeded(double SpentUsd, double LimitUsd, string NextNodeId) : PipelineEvent
{
    public override string Description =>
        $"Budget exceeded: {UsageLedger.FormatUsd(SpentUsd)} spent of {UsageLedger.FormatUsd(LimitUsd)}; stopped before {NextNodeId}";
}

/// <summary>Receives pipeline events.</summary>
public interface IPipelineEventSink
{
    void Emit(PipelineEvent pipelineEvent);
}

/// <summary>
/// Fan-out event bus: synchronous observers (<see cref="Subscribe"/>) and asynchronous streams (<see cref="ReadAllAsync"/>).
/// Observer exceptions are swallowed so a faulty UI cannot break a run.
/// </summary>
public sealed class PipelineEventBus : IPipelineEventSink
{
    private readonly object _gate = new();
    private readonly List<Action<PipelineEvent>> _observers = [];
    private readonly List<Channel<PipelineEvent>> _channels = [];
    private bool _completed;

    /// <summary>Registers an observer; dispose the result to unsubscribe.</summary>
    public IDisposable Subscribe(Action<PipelineEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _observers.Add(observer);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _observers.Remove(observer);
            }
        });
    }

    public void Emit(PipelineEvent pipelineEvent)
    {
        ArgumentNullException.ThrowIfNull(pipelineEvent);
        Action<PipelineEvent>[] observers;
        Channel<PipelineEvent>[] channels;
        lock (_gate)
        {
            observers = _observers.ToArray();
            channels = _channels.ToArray();
        }

        foreach (var observer in observers)
        {
            try
            {
                observer(pipelineEvent);
            }
            catch (Exception)
            {
                // Observers must never break execution.
            }
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(pipelineEvent);
        }
    }

    /// <summary>Streams events emitted after this call until <see cref="Complete"/> is called.</summary>
    public async IAsyncEnumerable<PipelineEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<PipelineEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            if (_completed)
            {
                yield break;
            }

            _channels.Add(channel);
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_gate)
            {
                _channels.Remove(channel);
            }
        }
    }

    /// <summary>Ends all async streams.</summary>
    public void Complete()
    {
        Channel<PipelineEvent>[] channels;
        lock (_gate)
        {
            _completed = true;
            channels = _channels.ToArray();
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>An event sink that appends each event as one JSON line to a file.</summary>
public sealed class JsonlEventSink(string path) : IPipelineEventSink, IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public string Path { get; } = path;

    public void Emit(PipelineEvent pipelineEvent)
    {
        ArgumentNullException.ThrowIfNull(pipelineEvent);
        var line = pipelineEvent.ToJson().ToJsonString();
        lock (_gate)
        {
            try
            {
                if (_writer is null)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                    _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }

                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Event logging is best-effort.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Helpers to describe stage statuses in events.</summary>
internal static class EventText
{
    public static string Status(StageStatus status) => status.ToWire();
}

using System.Text.Json.Nodes;
using Reactor.Events;

namespace Reactor.Execution;

/// <summary>Starts child pipelines for supervisor (manager loop) nodes.</summary>
public interface IChildPipelineLauncher
{
    /// <summary>Starts the pipeline in <paramref name="dotFilePath"/> in the background.</summary>
    ChildPipelineHandle Start(string dotFilePath, string logsRoot, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>Live telemetry and control for a child pipeline.</summary>
public sealed class ChildPipelineHandle
{
    private readonly object _gate = new();
    private string? _currentNode;
    private string? _lastEvent;
    private int _completedStages;
    private int _retries;

    public ChildPipelineHandle(string logsRoot)
    {
        LogsRoot = logsRoot;
    }

    public string LogsRoot { get; }

    /// <summary>Completes with the child's result (faults if the child could not start, e.g. validation errors).</summary>
    public Task<PipelineResult> Completion { get; internal set; } = Task.FromException<PipelineResult>(new InvalidOperationException("Child pipeline not started."));

    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary><c>running</c>, <c>completed</c>, or <c>failed</c>.</summary>
    public string Status => Completion.IsCompletedSuccessfully
        ? Completion.Result.Succeeded ? "completed" : "failed"
        : Completion.IsCompleted ? "failed" : "running";

    /// <summary><c>success</c>/<c>fail</c> once finished, else empty.</summary>
    public string Outcome => Completion.IsCompletedSuccessfully
        ? Completion.Result.Status.ToWire()
        : Completion.IsCompleted ? "fail" : "";

    public string FailureReason => Completion.IsCompletedSuccessfully
        ? Completion.Result.FailureReason
        : Completion.Exception?.GetBaseException().Message ?? "";

    public string? CurrentNode
    {
        get
        {
            lock (_gate)
            {
                return _currentNode;
            }
        }
    }

    public string? LastEvent
    {
        get
        {
            lock (_gate)
            {
                return _lastEvent;
            }
        }
    }

    public int CompletedStages => Volatile.Read(ref _completedStages);

    public int Retries => Volatile.Read(ref _retries);

    /// <summary>Requests cancellation of the child run (no-op once it has finished).</summary>
    public void Cancel()
    {
        try
        {
            Cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The child already finished.
        }
    }

    internal void Observe(PipelineEvent pipelineEvent)
    {
        lock (_gate)
        {
            _lastEvent = pipelineEvent.Description;
            if (pipelineEvent is StageStarted started)
            {
                _currentNode = started.NodeId;
            }
        }

        switch (pipelineEvent)
        {
            case StageCompleted:
                Interlocked.Increment(ref _completedStages);
                break;
            case StageRetrying:
                Interlocked.Increment(ref _retries);
                break;
        }
    }

    /// <summary>Reads telemetry for an externally running child from its run directory (result.json / checkpoint.json).</summary>
    public static (string Status, string Outcome, string? CurrentNode, int Completed, string FailureReason) ReadExternal(string logsRoot)
    {
        var resultPath = Path.Combine(logsRoot, "result.json");
        if (File.Exists(resultPath) && JsonNode.Parse(File.ReadAllText(resultPath)) is JsonObject result)
        {
            var status = result["status"]?.GetValue<string>() ?? "fail";
            return (status == "success" ? "completed" : "failed", status, null,
                (result["completed_nodes"] as JsonArray)?.Count ?? 0, result["failure_reason"]?.GetValue<string>() ?? "");
        }

        var checkpoint = Checkpoint.TryLoadFromRunDirectory(logsRoot);
        return checkpoint is null
            ? ("running", "", null, 0, "")
            : ("running", "", checkpoint.CurrentNode, checkpoint.CompletedNodes.Count, "");
    }
}

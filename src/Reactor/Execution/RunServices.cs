using Reactor.Events;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Human;

namespace Reactor.Execution;

/// <summary>
/// Per-run services available to handlers through <see cref="PipelineContext.Run"/>: identity, directories, events,
/// artifacts, the interviewer, the codergen backend, branch execution, and run history. Not part of checkpoints.
/// </summary>
public sealed class RunServices
{
    public required string RunId { get; init; }

    /// <summary>The run's log/artifact directory.</summary>
    public required string LogsRoot { get; init; }

    public required Graph Graph { get; init; }

    public required IPipelineEventSink Events { get; init; }

    public required ArtifactStore Artifacts { get; init; }

    public required IInterviewer Interviewer { get; init; }

    /// <summary>The LLM backend for codergen stages; null = simulation mode.</summary>
    public ICodergenBackend? Backend { get; init; }

    /// <summary>Runs parallel branches using the engine's traversal machinery.</summary>
    public required IBranchExecutor BranchExecutor { get; init; }

    /// <summary>Starts child pipelines (supervisor nodes) with the same runner, handlers, and backends.</summary>
    public IChildPipelineLauncher? ChildLauncher { get; init; }

    /// <summary>Working directory for tools, CLI agents, and child pipelines.</summary>
    public required string WorkingDirectory { get; init; }

    public required RunOptions Options { get; init; }

    public required PipelineRunnerOptions RunnerOptions { get; init; }

    /// <summary>Completed stages in order (top-level traversal).</summary>
    public IReadOnlyList<StageRecord> History => HistoryList;

    internal List<StageRecord> HistoryList { get; init; } = [];

    /// <summary>Directory for a node's prompt/response/status files.</summary>
    public string StageDirectory(string nodeId) => Path.Combine(LogsRoot, nodeId);

    /// <summary>Emits a <see cref="StageLog"/> event.</summary>
    public void Log(string nodeId, string message, string level = "info") =>
        Events.Emit(new StageLog(nodeId, message, level) { RunId = RunId });
}

/// <summary>Executes a parallel branch: a sub-traversal from a branch's first node until it reaches a join point.</summary>
public interface IBranchExecutor
{
    /// <summary>
    /// Runs nodes starting at <paramref name="startNodeId"/> with <paramref name="branchContext"/> (an isolated clone) until
    /// the traversal reaches a fan-in node, the exit node, or a dead end. The stop node itself is not executed.
    /// </summary>
    Task<BranchResult> ExecuteBranchAsync(string startNodeId, PipelineContext branchContext, CancellationToken cancellationToken);
}

/// <summary>The result of one parallel branch.</summary>
public sealed record BranchResult
{
    /// <summary>Branch id (the branch's first node).</summary>
    public required string BranchId { get; init; }

    public required StageStatus Status { get; init; }

    /// <summary>The node where the branch stopped (fan-in/exit), or null for a dead end or failure.</summary>
    public string? StopNodeId { get; init; }

    public string Notes { get; init; } = "";

    public string FailureReason { get; init; } = "";

    public IReadOnlyList<string> CompletedNodes { get; init; } = [];

    /// <summary>The branch's final context values.</summary>
    public IReadOnlyDictionary<string, object?> Context { get; init; } = new Dictionary<string, object?>();

    public TimeSpan Duration { get; init; }
}

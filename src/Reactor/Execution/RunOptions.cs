using Reactor.Events;
using Reactor.Handlers;
using Reactor.Human;
using Reactor.Validation;

namespace Reactor.Execution;

/// <summary>Per-run options.</summary>
public sealed record RunOptions
{
    /// <summary>Explicit run directory. Default: <c>{LogsBaseDirectory}/{timestamp}-{graph}-{id}</c>.</summary>
    public string? LogsRoot { get; init; }

    public string? RunId { get; init; }

    /// <summary>Values seeded into the context before execution.</summary>
    public IReadOnlyDictionary<string, object?>? InitialContext { get; init; }

    /// <summary>Working directory for tools, CLI agents, and child pipelines (default: current directory).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Overrides the runner's interviewer for this run.</summary>
    public IInterviewer? Interviewer { get; init; }

    /// <summary>Overrides the runner's codergen backend for this run.</summary>
    public ICodergenBackend? Backend { get; init; }

    /// <summary>Maximum node executions in the top-level traversal (0 = unlimited).</summary>
    public int MaxSteps { get; init; }

    /// <summary>Maximum <c>loop_restart</c> relaunches.</summary>
    public int MaxLoopRestarts { get; init; } = 100;

    /// <summary>Overrides the retry backoff for every node (e.g. <see cref="BackoffConfig.Immediate"/> in tests).</summary>
    public BackoffConfig? Backoff { get; init; }

    /// <summary>Observer for this run's events.</summary>
    public Action<PipelineEvent>? OnEvent { get; init; }

    /// <summary>Start at this node instead of the start node.</summary>
    public string? StartAt { get; init; }

    /// <summary>Additional event sinks (e.g. a server's per-run event stream).</summary>
    public IReadOnlyList<IPipelineEventSink>? EventSinks { get; init; }

    /// <summary>
    /// Also keep a copy of the checkpoint saved after each top-level stage, as
    /// <c>{logs_root}/checkpoints/{index:D4}-{node}.json</c> (<c>index</c> is the stage's execution index, as in
    /// <see cref="StageStarted.Index"/>), so the run can later be forked from any stage. Off by default. Skipped for
    /// graphs with a node named <c>checkpoints</c>, whose stage directory would collide with the snapshots.
    /// </summary>
    public bool KeepCheckpointHistory { get; init; }
}

/// <summary>Runner-wide configuration.</summary>
public sealed record PipelineRunnerOptions
{
    /// <summary>LLM backend for codergen stages; null runs in simulation mode.</summary>
    public ICodergenBackend? Backend { get; init; }

    /// <summary>Human-in-the-loop frontend. Defaults to auto-approve.</summary>
    public IInterviewer Interviewer { get; init; } = new AutoApproveInterviewer();

    /// <summary>Parent directory for run directories.</summary>
    public string LogsBaseDirectory { get; init; } = Path.Combine(".reactor", "runs");

    /// <summary>Append every event to <c>{logs_root}/events.jsonl</c>.</summary>
    public bool WriteEventLog { get; init; } = true;

    /// <summary>Default branch concurrency for parallel nodes without <c>max_parallel</c>.</summary>
    public int DefaultMaxParallel { get; init; } = 4;

    /// <summary>
    /// Append the status-file protocol to codergen prompts so agents can report fail/retry outcomes and routing
    /// preferences by writing <c>status.json</c>.
    /// </summary>
    public bool IncludeStatusProtocol { get; init; } = true;

    /// <summary>Safety limit on node executions within one parallel branch.</summary>
    public int BranchMaxSteps { get; init; } = 1000;

    /// <summary>Timeout for tool nodes without a <c>timeout</c> attribute.</summary>
    public TimeSpan DefaultToolTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>A parsed, transformed, validated pipeline.</summary>
public sealed record PreparedPipeline(Graphs.Graph Graph, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);

    public IReadOnlyList<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
}

/// <summary>The final result of a pipeline run.</summary>
public sealed record PipelineResult
{
    public required string RunId { get; init; }

    public required string LogsRoot { get; init; }

    /// <summary><see cref="StageStatus.Success"/> or <see cref="StageStatus.Fail"/>.</summary>
    public required StageStatus Status { get; init; }

    public required Outcome FinalOutcome { get; init; }

    public string FailureReason => FinalOutcome.FailureReason;

    public IReadOnlyList<string> CompletedNodes { get; init; } = [];

    public IReadOnlyDictionary<string, Outcome> NodeOutcomes { get; init; } = new Dictionary<string, Outcome>();

    public IReadOnlyDictionary<string, object?> Context { get; init; } = new Dictionary<string, object?>();

    public TimeSpan Duration { get; init; }

    public int LoopRestarts { get; init; }

    public bool Cancelled { get; init; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    public bool Succeeded => Status.IsSuccessful();
}

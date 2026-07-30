namespace Reactor.Events;

/// <summary>
/// Reactor extension: a stage visit changed files in its working directory (all its attempts together). Emitted after the
/// stage's <see cref="StageCompleted"/>, only when something changed or the stage wrote outside its <c>write_scope</c>;
/// the full record is the stage directory's <c>changes.json</c>. Counts are exact; <see cref="Sample"/> holds the first
/// few changed paths (relative to the stage's working directory, <c>/</c>-separated). <see cref="Concurrent"/> means
/// other stages ran in the same working directory at the same time, so their changes may be attributed to this stage too.
/// </summary>
public sealed record WorkspaceChanged(string NodeId, int Index, int Added, int Modified, int Deleted, IReadOnlyList<string> Sample) : PipelineEvent
{
    /// <summary>Changed paths outside the stage's <c>write_scope</c> (at most 20).</summary>
    public IReadOnlyList<string> OutOfScope { get; init; } = [];

    /// <summary>Other stages changed the same working directory while this one ran.</summary>
    public bool Concurrent { get; init; }

    /// <summary>The snapshot taken after the stage (a git commit id, or <c>walk:…</c>).</summary>
    public string? Snapshot { get; init; }

    public override string Description
    {
        get
        {
            var total = Added + Modified + Deleted;
            var text = $"Stage {NodeId} changed {total} file{(total == 1 ? "" : "s")} (+{Added} ~{Modified} -{Deleted})";
            if (OutOfScope.Count > 0)
            {
                text += $"; {OutOfScope.Count} outside its write_scope";
            }

            return Concurrent ? text + " (concurrent stages)" : text;
        }
    }
}

/// <summary>
/// Reactor extension: a fan-in with <c>promote="best"</c> applied the changes of the winning branch's workspace
/// (<c>branch_workdir="worktree"</c> or <c>"copy"</c>) to its own working directory. The fan-in's <c>changes.json</c>
/// lists the files under <c>promotion</c>.
/// </summary>
public sealed record WorkspacePromoted(string NodeId, string Branch, string Workdir, int Files) : PipelineEvent
{
    public override string Description => $"Promoted branch {Branch} ({Files} file{(Files == 1 ? "" : "s")}) into the working directory";
}

using Reactor.Graphs;

namespace Reactor.Workspaces;

/// <summary>Workspace-related attributes (Reactor extension).</summary>
public static class WorkspaceAttributes
{
    /// <summary>Graph or node: <c>false</c> stops recording <c>changes.json</c> (a node value overrides the graph's).</summary>
    public const string TrackChanges = "track_changes";

    /// <summary>Node: comma-separated globs (relative to the working directory) the stage may change.</summary>
    public const string WriteScope = "write_scope";

    /// <summary>Node: <c>true</c> fails a stage that changed files outside its <c>write_scope</c> (without retrying it).</summary>
    public const string WriteScopeEnforce = "write_scope_enforce";

    /// <summary>Node: stages with the same group name never run at the same time within a run.</summary>
    public const string ConcurrencyGroup = "concurrency_group";

    /// <summary>Parallel node: <c>shared</c> (default), <c>worktree</c>, or <c>copy</c>.</summary>
    public const string BranchWorkdir = "branch_workdir";

    /// <summary>Fan-in node: <c>best</c> applies the winning branch's changes to the fan-in's working directory.</summary>
    public const string Promote = "promote";

    /// <summary>The <c>branch_workdir</c> values.</summary>
    public static IReadOnlyList<string> BranchWorkdirModes { get; } = [BranchModes.Shared, BranchModes.Worktree, BranchModes.Copy];

    /// <summary>The <c>promote</c> values (<c>none</c> = no promotion, the default).</summary>
    public static IReadOnlyList<string> PromoteModes { get; } = ["best", "none"];

    /// <summary>A node's <c>branch_workdir</c> (lower-cased; <c>shared</c> when unset).</summary>
    public static string BranchWorkdirOf(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.IsNullOrWhiteSpace(node[BranchWorkdir]) ? BranchModes.Shared : node[BranchWorkdir]!.Trim().ToLowerInvariant();
    }

    /// <summary>True when a fan-in node promotes its best branch.</summary>
    public static bool Promotes(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.Equals(node[Promote]?.Trim(), "best", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A node's <c>concurrency_group</c>, or null.</summary>
    public static string? ConcurrencyGroupOf(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.IsNullOrWhiteSpace(node[ConcurrencyGroup]) ? null : node[ConcurrencyGroup]!.Trim();
    }

    /// <summary>The <c>branch_workdir</c> modes.</summary>
    public static class BranchModes
    {
        public const string Shared = "shared";
        public const string Worktree = "worktree";
        public const string Copy = "copy";
    }
}

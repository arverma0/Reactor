using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Workspaces;

namespace Reactor.Validation;

/// <summary>
/// <c>branch_isolation</c> (INFO, Reactor extension): a parallel node without <c>branch_workdir</c> whose branches (at
/// least two of them) contain stages that write files — codergen, tool, or manager stages — runs them all in one working
/// directory, where they can overwrite each other's files.
/// </summary>
public sealed class BranchIsolationRule : ILintRule
{
    public string Name => "branch_isolation";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (HandlerTypes.Resolve(node) != HandlerTypes.Parallel || !string.IsNullOrWhiteSpace(node[WorkspaceAttributes.BranchWorkdir]))
            {
                continue;
            }

            var writing = graph.OutgoingEdges(node.Id)
                .Select(e => e.To)
                .Distinct(StringComparer.Ordinal)
                .Count(start => BranchNodes(graph, node.Id, start).Any(IsWriter));
            if (writing >= 2)
            {
                yield return new Diagnostic(Name, Severity.Info,
                    "Parallel branches share one working directory, so agents and tools in different branches can overwrite each other's files.")
                {
                    NodeId = node.Id,
                    Fix = "Set branch_workdir=\"worktree\" (git) or branch_workdir=\"copy\" to give each branch its own workspace, or branch_workdir=\"shared\" if the branches only read.",
                };
            }
        }
    }

    /// <summary>The nodes of a branch: reachable from its first node without passing a fan-in, the exit, or the parallel node.</summary>
    internal static IEnumerable<Node> BranchNodes(Graph graph, string parallelId, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { parallelId };
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id) || graph.GetNode(id) is not { } node || HandlerTypes.Resolve(node) == HandlerTypes.FanIn || Graph.IsExitNode(node))
            {
                continue;
            }

            yield return node;
            foreach (var edge in graph.OutgoingEdges(id))
            {
                queue.Enqueue(edge.To);
            }
        }
    }

    private static bool IsWriter(Node node) => HandlerTypes.Resolve(node) is HandlerTypes.Codergen or HandlerTypes.Tool or HandlerTypes.ManagerLoop;
}

/// <summary>
/// <c>workspace_attributes</c> (WARNING, Reactor extension): workspace attributes must have valid values and sit on node
/// types that use them — <c>track_changes</c>/<c>write_scope_enforce</c> are booleans; <c>branch_workdir</c> is
/// <c>shared</c>, <c>worktree</c>, or <c>copy</c> on a parallel node; <c>promote</c> is <c>best</c> or <c>none</c> on a fan-in
/// downstream of a parallel node with its own branch workspaces; <c>write_scope</c> patterns are relative and stay inside
/// the working directory; <c>concurrency_group</c> is not set on a parallel node.
/// </summary>
public sealed class WorkspaceAttributesRule : ILintRule
{
    public string Name => "workspace_attributes";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        if (graph[WorkspaceAttributes.TrackChanges] is { } graphTrack && AttributeValues.ParseBool(graphTrack) is null)
        {
            yield return new Diagnostic(Name, Severity.Warning, $"Graph attribute track_changes='{graphTrack}' must be true or false.");
        }

        foreach (var node in graph.Nodes.Values)
        {
            var type = HandlerTypes.Resolve(node);
            foreach (var key in new[] { WorkspaceAttributes.TrackChanges, WorkspaceAttributes.WriteScopeEnforce })
            {
                if (node[key] is { } flag && AttributeValues.ParseBool(flag) is null)
                {
                    yield return new Diagnostic(Name, Severity.Warning, $"{key}='{flag}' must be true or false.") { NodeId = node.Id };
                }
            }

            if (node[WorkspaceAttributes.BranchWorkdir] is { } workdir)
            {
                var mode = workdir.Trim().ToLowerInvariant();
                if (!WorkspaceAttributes.BranchWorkdirModes.Contains(mode))
                {
                    yield return new Diagnostic(Name, Severity.Warning, $"branch_workdir='{workdir}' must be one of: {string.Join(", ", WorkspaceAttributes.BranchWorkdirModes)}.") { NodeId = node.Id };
                }
                else if (type != HandlerTypes.Parallel)
                {
                    yield return new Diagnostic(Name, Severity.Warning, "branch_workdir is ignored except on parallel (component) nodes.") { NodeId = node.Id };
                }
            }

            if (node[WorkspaceAttributes.Promote] is { } promote)
            {
                var value = promote.Trim().ToLowerInvariant();
                if (!WorkspaceAttributes.PromoteModes.Contains(value))
                {
                    yield return new Diagnostic(Name, Severity.Warning, $"promote='{promote}' must be one of: {string.Join(", ", WorkspaceAttributes.PromoteModes)}.") { NodeId = node.Id };
                }
                else if (type != HandlerTypes.FanIn)
                {
                    yield return new Diagnostic(Name, Severity.Warning, "promote is ignored except on fan-in (tripleoctagon) nodes.") { NodeId = node.Id };
                }
                else if (value == "best" && !HasIsolatedBranchesUpstream(graph, node))
                {
                    yield return new Diagnostic(Name, Severity.Warning,
                        "promote=\"best\" has nothing to promote: no parallel node before this fan-in gives its branches their own workspaces.")
                    {
                        NodeId = node.Id,
                        Fix = "Set branch_workdir=\"worktree\" or \"copy\" on the parallel node.",
                    };
                }
            }

            var scope = node[WorkspaceAttributes.WriteScope];
            foreach (var problem in WriteScope.Split(scope).Select(WriteScope.Problem).OfType<string>())
            {
                yield return new Diagnostic(Name, Severity.Warning, "write_scope: " + problem) { NodeId = node.Id };
            }

            if (!string.IsNullOrWhiteSpace(scope) && type is HandlerTypes.Start or HandlerTypes.Exit or HandlerTypes.Conditional or HandlerTypes.WaitHuman or HandlerTypes.Parallel)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"write_scope is ignored on {type} nodes, which do not change files.") { NodeId = node.Id };
            }

            if (AttributeValues.ParseBool(node[WorkspaceAttributes.WriteScopeEnforce]) == true && string.IsNullOrWhiteSpace(scope))
            {
                yield return new Diagnostic(Name, Severity.Warning, "write_scope_enforce=true has no effect without a write_scope.") { NodeId = node.Id };
            }

            if (type == HandlerTypes.Parallel && WorkspaceAttributes.ConcurrencyGroupOf(node) is not null)
            {
                yield return new Diagnostic(Name, Severity.Warning, "concurrency_group is ignored on parallel nodes; set it on the stages inside the branches.") { NodeId = node.Id };
            }
        }
    }

    private static bool HasIsolatedBranchesUpstream(Graph graph, Node fanIn) =>
        graph.Nodes.Values
            .Where(n => HandlerTypes.Resolve(n) == HandlerTypes.Parallel && WorkspaceAttributes.BranchWorkdirOf(n) is WorkspaceAttributes.BranchModes.Worktree or WorkspaceAttributes.BranchModes.Copy)
            .Any(p => ReachabilityRule.Reachable(graph, p.Id).Contains(fanIn.Id));
}

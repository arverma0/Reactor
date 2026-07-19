using Reactor.Conditions;
using Reactor.Graphs;
using Reactor.Human;

namespace Reactor.Execution;

/// <summary>The chosen edge and the selection step that chose it.</summary>
public sealed record EdgeSelection(Edge Edge, string Reason);

/// <summary>
/// Deterministic edge selection (spec 3.3): (1) condition-matching edges, (2) preferred label among unconditional edges,
/// (3) suggested next ids, (4) highest weight, (5) lexical tiebreak on the target id.
/// </summary>
public static class EdgeSelector
{
    public const string ByCondition = "condition";
    public const string ByPreferredLabel = "preferred_label";
    public const string BySuggestedId = "suggested_next_id";
    public const string ByWeight = "weight";

    /// <summary>Selects the next edge, or null when no edge is eligible.</summary>
    /// <param name="node">The node that just completed.</param>
    /// <param name="outcome">Its outcome.</param>
    /// <param name="context">The run context (for conditions).</param>
    /// <param name="graph">The graph.</param>
    /// <param name="explicitRoutingOnly">
    /// When true (used for FAIL outcomes), only steps 1–3 apply: unconditional edges are not followed implicitly.
    /// </param>
    public static EdgeSelection? Select(Node node, Outcome outcome, PipelineContext context, Graph graph, bool explicitRoutingOnly = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);

        var edges = graph.OutgoingEdges(node.Id);
        if (edges.Count == 0)
        {
            return null;
        }

        var matched = edges.Where(e => e.IsConditional && SafeEvaluate(e.Condition, outcome, context)).ToList();
        if (matched.Count > 0)
        {
            return new EdgeSelection(BestByWeightThenLexical(matched), ByCondition);
        }

        var unconditional = edges.Where(e => !e.IsConditional).ToList();
        if (outcome.PreferredLabel.Length > 0)
        {
            var preferred = Accelerators.NormalizeLabel(outcome.PreferredLabel);
            var byLabel = unconditional.FirstOrDefault(e => Accelerators.NormalizeLabel(e.Label) == preferred);
            if (byLabel is not null)
            {
                return new EdgeSelection(byLabel, ByPreferredLabel);
            }
        }

        foreach (var suggested in outcome.SuggestedNextIds)
        {
            var byId = unconditional.FirstOrDefault(e => e.To == suggested);
            if (byId is not null)
            {
                return new EdgeSelection(byId, BySuggestedId);
            }
        }

        if (explicitRoutingOnly || unconditional.Count == 0)
        {
            return null;
        }

        return new EdgeSelection(BestByWeightThenLexical(unconditional), ByWeight);
    }

    /// <summary>Highest weight first; ties broken by target id (ordinal).</summary>
    public static Edge BestByWeightThenLexical(IEnumerable<Edge> edges) =>
        edges.OrderByDescending(e => e.Weight).ThenBy(e => e.To, StringComparer.Ordinal).First();

    private static bool SafeEvaluate(string condition, Outcome outcome, PipelineContext context)
    {
        try
        {
            return ConditionEvaluator.Evaluate(condition, outcome, context);
        }
        catch (ConditionSyntaxException)
        {
            return false;
        }
    }
}

/// <summary>Fidelity and thread resolution for LLM stages (spec 5.4).</summary>
public static class FidelityResolver
{
    /// <summary>Edge <c>fidelity</c> → node <c>fidelity</c> → graph <c>default_fidelity</c> → <c>compact</c>. Invalid values are skipped.</summary>
    public static string Resolve(Edge? incoming, Node node, Graph graph)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        foreach (var candidate in new[] { incoming?.Fidelity, node.Fidelity, graph.DefaultFidelity })
        {
            if (FidelityModes.IsValid(candidate))
            {
                return candidate!;
            }
        }

        return FidelityModes.Default;
    }

    /// <summary>
    /// Thread key for <c>full</c> fidelity: node <c>thread_id</c> → edge <c>thread_id</c> → graph default thread
    /// (<c>thread_id</c>/<c>default_thread_id</c>) → enclosing subgraph class → the previous node's thread (or its id).
    /// </summary>
    public static string ResolveThread(Edge? incoming, Node node, Graph graph, string? previousNodeId, string? previousThreadId)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        if (node.ThreadId.Length > 0)
        {
            return node.ThreadId;
        }

        if (incoming is { ThreadId.Length: > 0 })
        {
            return incoming.ThreadId;
        }

        if (graph[AttrKeys.ThreadId] is { Length: > 0 } graphThread)
        {
            return graphThread;
        }

        if (graph[AttrKeys.DefaultThreadId] is { Length: > 0 } defaultThread)
        {
            return defaultThread;
        }

        if (!string.IsNullOrEmpty(node.SubgraphScope))
        {
            return node.SubgraphScope;
        }

        return previousThreadId ?? previousNodeId ?? node.Id;
    }
}

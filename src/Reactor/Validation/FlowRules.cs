using System.Globalization;
using Reactor.Conditions;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Validation;

/// <summary>
/// <c>fail_flows_onward</c> (WARNING, Reactor extension): a stage whose failure nothing routes. Per the spec, a failed stage
/// follows an unconditional edge like a successful one, so the run carries on (and can "succeed") without the stage's work.
/// Not reported when an edge handles <c>outcome=fail</c> (or <c>outcome!=success</c>), when the unconditional edges lead to a
/// diamond that inspects the outcome or to a fan-in (which receives the branch's failure as its result), or when
/// <c>on_fail</c> (or the graph's <c>default_on_fail</c>) is set — <c>stop</c>
/// ends the run on an unrouted failure, <c>continue</c> acknowledges the behavior.
/// </summary>
public sealed class FailFlowsOnwardRule : ILintRule
{
    private static readonly HashSet<string> RoutingTypes = new(StringComparer.Ordinal)
    {
        HandlerTypes.Start, HandlerTypes.Exit, HandlerTypes.Conditional, HandlerTypes.Parallel, HandlerTypes.FanIn, HandlerTypes.WaitHuman,
    };

    public string Name => "fail_flows_onward";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        if (!string.IsNullOrWhiteSpace(graph[AttrKeys.DefaultOnFail]))
        {
            yield break;
        }

        foreach (var node in graph.Nodes.Values)
        {
            if (RoutingTypes.Contains(HandlerTypes.Resolve(node)) || !string.IsNullOrWhiteSpace(node[AttrKeys.OnFail]))
            {
                continue;
            }

            var edges = graph.OutgoingEdges(node.Id);
            var unconditional = edges.Where(e => !e.IsConditional).ToList();
            if (unconditional.Count == 0 || edges.Any(e => e.IsConditional && HandlesFailure(e.Condition)))
            {
                continue;
            }

            if (unconditional.All(e => graph.GetNode(e.To) is { } target && HandlerTypes.Resolve(target) is HandlerTypes.Conditional or HandlerTypes.FanIn))
            {
                continue;
            }

            var next = EdgeSelector.BestByWeightThenLexical(unconditional).To;
            var shadowed = node.RetryTarget.Length > 0 || node.FallbackRetryTarget.Length > 0
                ? " Its retry_target is never used while an unconditional edge exists."
                : "";
            yield return new Diagnostic(Name, Severity.Warning,
                $"If '{node.Id}' fails, the run continues to '{next}' as if it had succeeded: nothing routes outcome=fail.{shadowed}")
            {
                NodeId = node.Id,
                Fix = $"Put condition=\"outcome=success\" on the edge to '{next}' and add an outcome=fail edge (or a retry_target); or set " +
                      "on_fail=\"stop\" (graph: default_on_fail=\"stop\") so an unrouted failure ends the run; or on_fail=\"continue\" to accept it.",
            };
        }
    }

    /// <summary>True when a condition can select the edge for a failed outcome (it tests <c>outcome=fail</c> or <c>outcome!=…</c>).</summary>
    internal static bool HandlesFailure(string condition) =>
        ConditionParser.TryParse(condition, out var expression, out _) &&
        expression.Clauses.Any(c => c.Key == "outcome" &&
                                    (c is { Operator: ConditionOperator.Equals, Value: "fail" } ||
                                     c is { Operator: ConditionOperator.NotEquals } && c.Value != "fail"));
}

/// <summary>
/// <c>default_route</c> (WARNING, Reactor extension): a stage with several labeled unconditional edges where the
/// highest weight is shared, so a stage that reports no (or an unknown) preferred label is routed by the lexical order of
/// the target ids — rarely the intended default. Human gates are exempt (their edges are the human's choices).
/// </summary>
public sealed class DefaultRouteRule : ILintRule
{
    public string Name => "default_route";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (HandlerTypes.Resolve(node) is HandlerTypes.WaitHuman or HandlerTypes.Parallel or HandlerTypes.Start or HandlerTypes.Exit)
            {
                continue;
            }

            var unconditional = graph.OutgoingEdges(node.Id).Where(e => !e.IsConditional).ToList();
            if (unconditional.Count(e => e.Label.Length > 0) < 2)
            {
                continue;
            }

            var top = unconditional.Max(e => e.Weight);
            var tied = unconditional.Where(e => e.Weight == top).ToList();
            if (tied.Count < 2)
            {
                continue;
            }

            var chosen = EdgeSelector.BestByWeightThenLexical(tied);
            var name = chosen.Label.Length > 0 ? $"'{chosen.Label}' ({chosen.To})" : $"'{chosen.To}'";
            yield return new Diagnostic(Name, Severity.Warning,
                $"When '{node.Id}' reports no matching preferred_label, the run takes {name} only because its id sorts first among edges of equal weight.")
            {
                NodeId = node.Id,
                Fix = "Give the safe default route the highest weight (e.g. weight=1 on the rework or human-review edge).",
            };
        }
    }
}

/// <summary>
/// <c>unbounded_loop</c> (INFO, Reactor extension): a cycle with no bound the engine enforces — no stage in it has
/// <c>max_visits</c>, no edge in it tests <c>visits.&lt;node&gt;</c>, and no human gate is in it — so it can repeat until
/// the run's step limit if its exit condition is never met.
/// </summary>
public sealed class UnboundedLoopRule : ILintRule
{
    public string Name => "unbounded_loop";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var component in StronglyConnected(graph))
        {
            var members = new HashSet<string>(component, StringComparer.Ordinal);
            var isCycle = component.Count > 1 || graph.OutgoingEdges(component[0]).Any(e => e.To == component[0]);
            if (!isCycle)
            {
                continue;
            }

            var nodes = component.Select(id => graph.GetNode(id)).OfType<Node>().ToList();
            var bounded = nodes.Any(n => n.MaxVisits is not null || HandlerTypes.Resolve(n) == HandlerTypes.WaitHuman)
                          || graph.Edges.Any(e => members.Contains(e.From) && TestsVisits(e.Condition));
            if (bounded)
            {
                continue;
            }

            var ordered = graph.Nodes.Keys.Where(members.Contains).ToList();
            var shown = ordered.Count <= 6 ? string.Join(" → ", ordered) : string.Join(" → ", ordered.Take(6)) + " → …";
            yield return new Diagnostic(Name, Severity.Info,
                $"The loop {shown} has no bound the engine enforces; if its exit condition is never met it runs until the step limit.")
            {
                NodeId = ordered[0],
                Fix = "Add max_visits=N (with on_max_visits=\"<escalation stage>\") to a stage in the loop, or a visits.<node> condition (e.g. visits.fix<3) on the edge that repeats it.",
            };
        }
    }

    private static bool TestsVisits(string condition) =>
        ConditionParser.TryParse(condition, out var expression, out _) && expression.Clauses.Any(c => ConditionEvaluator.TryVisitsNode(c.Key, out _));

    /// <summary>Strongly connected components (Tarjan), each in discovery order.</summary>
    internal static List<List<string>> StronglyConnected(Graph graph)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var components = new List<List<string>>();

        foreach (var root in graph.Nodes.Keys)
        {
            if (indices.ContainsKey(root))
            {
                continue;
            }

            // Iterative DFS so deep graphs cannot overflow the stack.
            var work = new Stack<(string Node, int Edge)>();
            work.Push((root, 0));
            indices[root] = lowlinks[root] = index++;
            stack.Push(root);
            onStack.Add(root);
            while (work.Count > 0)
            {
                var (current, edgeIndex) = work.Pop();
                var edges = graph.OutgoingEdges(current).Where(e => graph.ContainsNode(e.To)).ToList();
                if (edgeIndex < edges.Count)
                {
                    work.Push((current, edgeIndex + 1));
                    var next = edges[edgeIndex].To;
                    if (!indices.ContainsKey(next))
                    {
                        indices[next] = lowlinks[next] = index++;
                        stack.Push(next);
                        onStack.Add(next);
                        work.Push((next, 0));
                    }
                    else if (onStack.Contains(next))
                    {
                        lowlinks[current] = Math.Min(lowlinks[current], indices[next]);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    lowlinks[parent] = Math.Min(lowlinks[parent], lowlinks[current]);
                }

                if (lowlinks[current] == indices[current])
                {
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (member != current);

                    component.Reverse();
                    components.Add(component);
                }
            }
        }

        return components;
    }
}

/// <summary>
/// <c>failure_evidence</c> (WARNING, Reactor extension): a failure route from a stage that declares <c>outputs</c> (e.g. a
/// test or re-check tool's report) to a stage that reads none of them — the fixer is sent back without the evidence of
/// what failed and has to regenerate it. Routes through a diamond are followed one hop.
/// </summary>
public sealed class FailureEvidenceRule(IEnumerable<string>? knownTypes = null) : ILintRule
{
    private readonly ArtifactFlow.Types _types = new(knownTypes);

    public string Name => "failure_evidence";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var source in graph.Nodes.Values)
        {
            if (!ArtifactPins.IsSupportedHandlerType(_types.Of(source)))
            {
                continue;
            }

            var outputs = ArtifactPins.Parse(source).Outputs.Select(o => o.Name).ToList();
            if (outputs.Count == 0)
            {
                continue;
            }

            foreach (var target in FailureTargets(graph, source.Id).Distinct(StringComparer.Ordinal))
            {
                if (graph.GetNode(target) is not { } fixer || !ArtifactPins.IsSupportedHandlerType(_types.Of(fixer)) || fixer.Id == source.Id)
                {
                    continue;
                }

                var inputs = ArtifactPins.Parse(fixer).Inputs;
                if (outputs.Any(o => inputs.Contains(o, StringComparer.Ordinal)))
                {
                    continue;
                }

                var names = string.Join(", ", outputs.Select(o => $"'{o}'"));
                yield return new Diagnostic(Name, Severity.Warning,
                    $"When '{source.Id}' fails the run goes to '{fixer.Id}', which does not read what '{source.Id}' produced ({names}), so it starts without the evidence of what failed.")
                {
                    NodeId = fixer.Id,
                    Edge = (source.Id, fixer.Id),
                    Fix = $"Add {outputs[0]} to the inputs of '{fixer.Id}' (inputs=\"…, {outputs[0]}\").",
                };
            }
        }
    }

    /// <summary>Targets of the failure routes out of <paramref name="nodeId"/>, looking through diamonds one hop.</summary>
    private static IEnumerable<string> FailureTargets(Graph graph, string nodeId)
    {
        foreach (var edge in graph.OutgoingEdges(nodeId))
        {
            var target = graph.GetNode(edge.To);
            if (edge.IsConditional && FailFlowsOnwardRule.HandlesFailure(edge.Condition))
            {
                yield return edge.To;
            }
            else if (!edge.IsConditional && target is not null && HandlerTypes.Resolve(target) == HandlerTypes.Conditional)
            {
                foreach (var next in graph.OutgoingEdges(target.Id).Where(e => e.IsConditional && FailFlowsOnwardRule.HandlesFailure(e.Condition)))
                {
                    yield return next.To;
                }
            }
        }
    }
}

/// <summary>
/// <c>visit_limits</c> (WARNING, Reactor extension): <c>on_max_visits</c> must name another existing stage, and needs
/// <c>max_visits</c> to have any effect.
/// </summary>
public sealed class VisitLimitRule : ILintRule
{
    public string Name => "visit_limits";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            var target = node.OnMaxVisits;
            if (target.Length == 0)
            {
                continue;
            }

            if (node.MaxVisits is null)
            {
                yield return new Diagnostic(Name, Severity.Warning, "on_max_visits has no effect without max_visits.")
                {
                    NodeId = node.Id,
                    Fix = "Set max_visits=N (the most times the stage may run).",
                };
            }

            if (target == node.Id)
            {
                yield return new Diagnostic(Name, Severity.Warning, "on_max_visits points at the stage itself; when the limit is reached the run fails instead.")
                {
                    NodeId = node.Id,
                    Fix = "Point on_max_visits at the stage that should take over (e.g. a human review gate).",
                };
            }
            else if (!graph.ContainsNode(target))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"on_max_visits '{target}' does not reference an existing node; when the limit is reached the run fails.")
                {
                    NodeId = node.Id,
                };
            }
        }
    }
}

/// <summary><c>attribute_types</c> (WARNING) for Reactor's engine extension attributes.</summary>
public sealed class ExtensionAttributeTypesRule : ILintRule
{
    private static readonly string[] OnFailValues = ["continue", "stop"];

    public string Name => "attribute_types";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        if (graph[AttrKeys.DefaultOnFail] is { } defaultOnFail && !OnFailValues.Contains(defaultOnFail.Trim().ToLowerInvariant()))
        {
            yield return new Diagnostic(Name, Severity.Warning, $"Graph attribute default_on_fail='{defaultOnFail}' must be continue or stop.");
        }

        if (graph[AttrKeys.MaxCost] is { } maxCost &&
            !(double.TryParse(maxCost.Trim().TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cost) && cost >= 0))
        {
            yield return new Diagnostic(Name, Severity.Warning, $"Graph attribute max_cost='{maxCost}' must be a non-negative number of US dollars (e.g. 25 or 7.5).");
        }

        foreach (var node in graph.Nodes.Values)
        {
            if (node[AttrKeys.MaxVisits] is { } maxVisits && AttributeValues.ParseInt(maxVisits) is not > 0)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"max_visits='{maxVisits}' must be a positive integer.") { NodeId = node.Id };
            }

            if (node[AttrKeys.OnFail] is { } onFail && !OnFailValues.Contains(onFail.Trim().ToLowerInvariant()))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"on_fail='{onFail}' must be continue or stop.") { NodeId = node.Id };
            }

            if (node[AttrKeys.OnTimeout] is { } onTimeout && !StageTiming.OnTimeoutValues.Contains(onTimeout.Trim().ToLowerInvariant()))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"on_timeout='{onTimeout}' must be one of: {string.Join(", ", StageTiming.OnTimeoutValues)}.") { NodeId = node.Id };
            }

            if (node[AttrKeys.SoftTimeout] is { } soft && !StageTiming.IsOff(soft) && AttributeValues.ParseDuration(soft) is null)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"soft_timeout='{soft}' must be a duration (e.g. 70m) or off.") { NodeId = node.Id };
            }

            if (node[AttrKeys.TimeoutGrace] is { } grace && AttributeValues.ParseDuration(grace) is null)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"timeout_grace='{grace}' must be a duration (e.g. 5m).") { NodeId = node.Id };
            }

            var timed = new[] { AttrKeys.OnTimeout, AttrKeys.SoftTimeout, AttrKeys.TimeoutGrace }.Where(k => node[k] is not null).ToList();
            if (timed.Count > 0 && node.Timeout is null)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"{string.Join(", ", timed)} {(timed.Count > 1 ? "have" : "has")} no effect without a timeout.")
                {
                    NodeId = node.Id,
                    Fix = "Set timeout (e.g. timeout=\"90m\").",
                };
            }

            if (node[AttrKeys.MetricsFile] is { } metricsFile &&
                (string.IsNullOrWhiteSpace(metricsFile) || Path.IsPathRooted(metricsFile.Trim()) || metricsFile.Replace('\\', '/').Split('/').Contains("..")))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"metrics_file='{metricsFile}' must be a path inside the working directory (relative, without '..').") { NodeId = node.Id };
            }
        }
    }
}

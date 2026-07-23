using Reactor.Conditions;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Stylesheets;

namespace Reactor.Validation;

/// <summary><c>start_node</c> (ERROR): exactly one start node (shape=Mdiamond or id start/Start).</summary>
public sealed class StartNodeRule : ILintRule
{
    public string Name => "start_node";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        var starts = graph.StartNodes;
        if (starts.Count == 0)
        {
            yield return new Diagnostic(Name, Severity.Error, "Pipeline has no start node.")
            {
                Fix = "Add a node with shape=Mdiamond (e.g. start [shape=Mdiamond]).",
            };
        }
        else if (starts.Count > 1)
        {
            yield return new Diagnostic(Name, Severity.Error,
                $"Pipeline has {starts.Count} start nodes ({string.Join(", ", starts.Select(n => n.Id))}); exactly one is required.")
            {
                Fix = "Keep a single shape=Mdiamond node.",
            };
        }
    }
}

/// <summary><c>terminal_node</c> (ERROR): exactly one exit node (shape=Msquare or id exit/end).</summary>
public sealed class TerminalNodeRule : ILintRule
{
    public string Name => "terminal_node";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        var exits = graph.ExitNodes;
        if (exits.Count == 0)
        {
            yield return new Diagnostic(Name, Severity.Error, "Pipeline has no exit node.")
            {
                Fix = "Add a node with shape=Msquare (e.g. exit [shape=Msquare]).",
            };
        }
        else if (exits.Count > 1)
        {
            yield return new Diagnostic(Name, Severity.Error,
                $"Pipeline has {exits.Count} exit nodes ({string.Join(", ", exits.Select(n => n.Id))}); exactly one is required.")
            {
                Fix = "Route all terminal paths to a single shape=Msquare node.",
            };
        }
    }
}

/// <summary><c>reachability</c> (ERROR): every node must be reachable from the start node.</summary>
public sealed class ReachabilityRule : ILintRule
{
    public string Name => "reachability";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        var start = graph.FindStartNode();
        if (start is null)
        {
            yield break;
        }

        var reachable = Reachable(graph, start.Id);
        foreach (var node in graph.Nodes.Values)
        {
            if (!reachable.Contains(node.Id))
            {
                yield return new Diagnostic(Name, Severity.Error, $"Node '{node.Id}' is not reachable from the start node '{start.Id}'.")
                {
                    NodeId = node.Id,
                    Fix = "Connect it with an incoming edge or remove it.",
                };
            }
        }
    }

    internal static HashSet<string> Reachable(Graph graph, string startId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { startId };
        var queue = new Queue<string>([startId]);
        while (queue.Count > 0)
        {
            foreach (var edge in graph.OutgoingEdges(queue.Dequeue()))
            {
                if (seen.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        return seen;
    }
}

/// <summary><c>edge_target_exists</c> (ERROR): every edge endpoint must reference an existing node.</summary>
public sealed class EdgeTargetExistsRule : ILintRule
{
    public string Name => "edge_target_exists";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var edge in graph.Edges)
        {
            if (!graph.ContainsNode(edge.From))
            {
                yield return new Diagnostic(Name, Severity.Error, $"Edge source '{edge.From}' does not exist.") { Edge = (edge.From, edge.To) };
            }

            if (!graph.ContainsNode(edge.To))
            {
                yield return new Diagnostic(Name, Severity.Error, $"Edge target '{edge.To}' does not exist.") { Edge = (edge.From, edge.To) };
            }
        }
    }
}

/// <summary><c>start_no_incoming</c> (ERROR): the start node must have no incoming edges.</summary>
public sealed class StartNoIncomingRule : ILintRule
{
    public string Name => "start_no_incoming";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var start in graph.StartNodes)
        {
            foreach (var edge in graph.IncomingEdges(start.Id))
            {
                yield return new Diagnostic(Name, Severity.Error, $"Start node '{start.Id}' has an incoming edge from '{edge.From}'.")
                {
                    NodeId = start.Id,
                    Edge = (edge.From, edge.To),
                    Fix = "Loop back to the first real stage instead of the start node.",
                };
            }
        }
    }
}

/// <summary><c>exit_no_outgoing</c> (ERROR): the exit node must have no outgoing edges.</summary>
public sealed class ExitNoOutgoingRule : ILintRule
{
    public string Name => "exit_no_outgoing";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var exit in graph.ExitNodes)
        {
            foreach (var edge in graph.OutgoingEdges(exit.Id))
            {
                yield return new Diagnostic(Name, Severity.Error, $"Exit node '{exit.Id}' has an outgoing edge to '{edge.To}'.")
                {
                    NodeId = exit.Id,
                    Edge = (edge.From, edge.To),
                };
            }
        }
    }
}

/// <summary><c>condition_syntax</c> (ERROR): edge conditions must parse.</summary>
public sealed class ConditionSyntaxRule : ILintRule
{
    public string Name => "condition_syntax";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var edge in graph.Edges)
        {
            if (edge.IsConditional && !ConditionParser.TryParse(edge.Condition, out _, out var error))
            {
                yield return new Diagnostic(Name, Severity.Error, $"Invalid condition: {error}") { Edge = (edge.From, edge.To) };
            }
        }

        foreach (var node in graph.Nodes.Values)
        {
            if (node["manager.stop_condition"] is { Length: > 0 } stop && !ConditionParser.TryParse(stop, out _, out var error))
            {
                yield return new Diagnostic(Name, Severity.Error, $"Invalid manager.stop_condition: {error}") { NodeId = node.Id };
            }
        }
    }
}

/// <summary><c>stylesheet_syntax</c> (ERROR): <c>model_stylesheet</c> must parse.</summary>
public sealed class StylesheetSyntaxRule : ILintRule
{
    public string Name => "stylesheet_syntax";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        if (!ModelStylesheet.TryParse(graph.ModelStylesheet, out _, out var error))
        {
            yield return new Diagnostic(Name, Severity.Error, $"Invalid model_stylesheet: {error}");
        }
    }
}

/// <summary><c>type_known</c> (WARNING): explicit node types should be registered handler types.</summary>
public sealed class TypeKnownRule(IEnumerable<string> knownTypes) : ILintRule
{
    private readonly HashSet<string> _known = new(knownTypes, StringComparer.Ordinal);

    public string Name => "type_known";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (node.Type.Length > 0 && !_known.Contains(node.Type))
            {
                yield return new Diagnostic(Name, Severity.Warning,
                    $"Unknown handler type '{node.Type}'; the default (codergen) handler will run instead.")
                {
                    NodeId = node.Id,
                    Fix = $"Register a handler for '{node.Type}' or use one of: {string.Join(", ", _known.Order(StringComparer.Ordinal))}.",
                };
            }
        }
    }
}

/// <summary><c>fidelity_valid</c> (WARNING): fidelity values must be valid modes.</summary>
public sealed class FidelityValidRule : ILintRule
{
    public string Name => "fidelity_valid";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        var allowed = string.Join(", ", FidelityModes.All);
        if (graph.DefaultFidelity.Length > 0 && !FidelityModes.IsValid(graph.DefaultFidelity))
        {
            yield return new Diagnostic(Name, Severity.Warning, $"Invalid default_fidelity '{graph.DefaultFidelity}'; expected one of: {allowed}.");
        }

        foreach (var node in graph.Nodes.Values)
        {
            if (node.Fidelity.Length > 0 && !FidelityModes.IsValid(node.Fidelity))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"Invalid fidelity '{node.Fidelity}'; expected one of: {allowed}.") { NodeId = node.Id };
            }
        }

        foreach (var edge in graph.Edges)
        {
            if (edge.Fidelity.Length > 0 && !FidelityModes.IsValid(edge.Fidelity))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"Invalid fidelity '{edge.Fidelity}'; expected one of: {allowed}.") { Edge = (edge.From, edge.To) };
            }
        }
    }
}

/// <summary><c>retry_target_exists</c> (WARNING): retry targets must reference existing nodes.</summary>
public sealed class RetryTargetExistsRule : ILintRule
{
    public string Name => "retry_target_exists";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var (attribute, value) in new[] { (AttrKeys.RetryTarget, graph.RetryTarget), (AttrKeys.FallbackRetryTarget, graph.FallbackRetryTarget) })
        {
            if (value.Length > 0 && !graph.ContainsNode(value))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"Graph {attribute} '{value}' does not reference an existing node.");
            }
        }

        foreach (var node in graph.Nodes.Values)
        {
            foreach (var (attribute, value) in new[] { (AttrKeys.RetryTarget, node.RetryTarget), (AttrKeys.FallbackRetryTarget, node.FallbackRetryTarget) })
            {
                if (value.Length > 0 && !graph.ContainsNode(value))
                {
                    yield return new Diagnostic(Name, Severity.Warning, $"{attribute} '{value}' does not reference an existing node.") { NodeId = node.Id };
                }
            }
        }
    }
}

/// <summary><c>goal_gate_has_retry</c> (WARNING): goal gates should have a retry target.</summary>
public sealed class GoalGateHasRetryRule : ILintRule
{
    public string Name => "goal_gate_has_retry";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        var graphHasTarget = graph.RetryTarget.Length > 0 || graph.FallbackRetryTarget.Length > 0;
        foreach (var node in graph.Nodes.Values)
        {
            if (node.GoalGate && node.RetryTarget.Length == 0 && node.FallbackRetryTarget.Length == 0 && !graphHasTarget)
            {
                yield return new Diagnostic(Name, Severity.Warning,
                    "Goal gate has no retry_target or fallback_retry_target; an unsatisfied gate will fail the pipeline at exit.")
                {
                    NodeId = node.Id,
                    Fix = "Set retry_target on the node (or on the graph).",
                };
            }
        }
    }
}

/// <summary><c>prompt_on_llm_nodes</c> (WARNING): LLM nodes should have a prompt or label.</summary>
public sealed class PromptOnLlmNodesRule : ILintRule
{
    public string Name => "prompt_on_llm_nodes";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (HandlerTypes.Resolve(node) == HandlerTypes.Codergen &&
                string.IsNullOrWhiteSpace(node[AttrKeys.Prompt]) &&
                string.IsNullOrWhiteSpace(node[AttrKeys.Label]))
            {
                yield return new Diagnostic(Name, Severity.Warning, "LLM stage has neither a prompt nor a label; the node id will be used as the prompt.")
                {
                    NodeId = node.Id,
                    Fix = "Add a prompt attribute describing the stage's task.",
                };
            }
        }
    }
}

/// <summary><c>condition_values</c> (WARNING/INFO): suspicious condition keys and outcome values.</summary>
public sealed class ConditionValuesRule : ILintRule
{
    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal) { "success", "fail", "partial_success", "retry", "skipped" };

    public string Name => "condition_values";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var edge in graph.Edges)
        {
            if (!edge.IsConditional || !ConditionParser.TryParse(edge.Condition, out var expression, out _))
            {
                continue;
            }

            foreach (var clause in expression.Clauses)
            {
                if (clause.Key == "outcome" && clause.Operator != ConditionOperator.Truthy && !Outcomes.Contains(clause.Value))
                {
                    yield return new Diagnostic(Name, Severity.Warning,
                        $"Condition compares outcome to '{clause.Value}', which is never produced (expected one of: {string.Join(", ", Outcomes)}).")
                    {
                        Edge = (edge.From, edge.To),
                    };
                }
                else if (clause.Key is not ("outcome" or "preferred_label") && !clause.Key.StartsWith("context.", StringComparison.Ordinal))
                {
                    yield return new Diagnostic(Name, Severity.Info,
                        $"Condition key '{clause.Key}' is looked up directly in the context; prefer 'context.{clause.Key}'.")
                    {
                        Edge = (edge.From, edge.To),
                    };
                }
            }
        }
    }
}

/// <summary><c>attribute_types</c> (WARNING): typed attributes must parse as their declared types.</summary>
public sealed class AttributeTypesRule : ILintRule
{
    public string Name => "attribute_types";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var key in new[] { AttrKeys.DefaultMaxRetries, AttrKeys.DefaultMaxRetryLegacy })
        {
            if (graph[key] is { } value && AttributeValues.ParseInt(value) is not >= 0)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"Graph attribute {key}='{value}' must be a non-negative integer.");
            }
        }

        foreach (var node in graph.Nodes.Values)
        {
            if (node[AttrKeys.MaxRetries] is { } retries && AttributeValues.ParseInt(retries) is not >= 0)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"max_retries='{retries}' must be a non-negative integer.") { NodeId = node.Id };
            }

            if (node[AttrKeys.MaxParallel] is { } maxParallel && AttributeValues.ParseInt(maxParallel) is not > 0)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"max_parallel='{maxParallel}' must be a positive integer.") { NodeId = node.Id };
            }

            if (node[AttrKeys.Timeout] is { } timeout && AttributeValues.ParseDuration(timeout) is null)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"timeout='{timeout}' is not a duration (e.g. 900s, 15m, 2h).") { NodeId = node.Id };
            }

            foreach (var key in new[] { AttrKeys.GoalGate, AttrKeys.AutoStatus, AttrKeys.AllowPartial })
            {
                if (node[key] is { } flag && AttributeValues.ParseBool(flag) is null)
                {
                    yield return new Diagnostic(Name, Severity.Warning, $"{key}='{flag}' must be true or false.") { NodeId = node.Id };
                }
            }
        }

        foreach (var edge in graph.Edges)
        {
            if (edge[AttrKeys.Weight] is { } weight && AttributeValues.ParseInt(weight) is null)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"weight='{weight}' must be an integer.") { Edge = (edge.From, edge.To) };
            }

            if (edge[AttrKeys.LoopRestart] is { } restart && AttributeValues.ParseBool(restart) is null)
            {
                yield return new Diagnostic(Name, Severity.Warning, $"loop_restart='{restart}' must be true or false.") { Edge = (edge.From, edge.To) };
            }
        }
    }
}

/// <summary><c>dead_end</c> (WARNING): non-exit nodes without outgoing edges end the run without goal-gate checks.</summary>
public sealed class DeadEndRule : ILintRule
{
    public string Name => "dead_end";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (!Graph.IsExitNode(node) && graph.OutgoingEdges(node.Id).Count == 0)
            {
                yield return new Diagnostic(Name, Severity.Warning,
                    "Node has no outgoing edges; the pipeline would stop here without reaching the exit node.")
                {
                    NodeId = node.Id,
                    Fix = "Add an edge to the next stage or to the exit node.",
                };
            }
        }
    }
}

/// <summary><c>tool_command</c> (WARNING): tool nodes need a <c>tool_command</c>.</summary>
public sealed class ToolCommandRule : ILintRule
{
    public string Name => "tool_command";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (HandlerTypes.Resolve(node) == HandlerTypes.Tool && string.IsNullOrWhiteSpace(node[AttrKeys.ToolCommand]))
            {
                yield return new Diagnostic(Name, Severity.Warning, "Tool node has no tool_command; it will fail at runtime.") { NodeId = node.Id };
            }
        }
    }
}

/// <summary><c>parallel_structure</c> (WARNING): parallel fan-outs need branches; fan-ins need a parallel predecessor.</summary>
public sealed class ParallelStructureRule : ILintRule
{
    public string Name => "parallel_structure";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            var type = HandlerTypes.Resolve(node);
            if (type == HandlerTypes.Parallel && graph.OutgoingEdges(node.Id).Count < 2)
            {
                yield return new Diagnostic(Name, Severity.Warning, "Parallel node has fewer than two branches.") { NodeId = node.Id };
            }

            if (type == HandlerTypes.FanIn)
            {
                var hasParallelAncestor = graph.Nodes.Values
                    .Where(n => HandlerTypes.Resolve(n) == HandlerTypes.Parallel)
                    .Any(p => ReachabilityRule.Reachable(graph, p.Id).Contains(node.Id));
                if (!hasParallelAncestor)
                {
                    yield return new Diagnostic(Name, Severity.Warning, "Fan-in node is not downstream of any parallel node; it will have no results to consolidate.")
                    {
                        NodeId = node.Id,
                    };
                }
            }
        }
    }
}

/// <summary><c>manager_child</c> (WARNING): manager loop nodes need a child pipeline.</summary>
public sealed class ManagerLoopChildRule : ILintRule
{
    public string Name => "manager_child";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (HandlerTypes.Resolve(node) == HandlerTypes.ManagerLoop &&
                string.IsNullOrWhiteSpace(graph[AttrKeys.StackChildDotfile]) &&
                string.IsNullOrWhiteSpace(node[AttrKeys.StackChildDotfile]))
            {
                yield return new Diagnostic(Name, Severity.Warning, "Manager loop node has no child pipeline; set graph attribute stack.child_dotfile.")
                {
                    NodeId = node.Id,
                };
            }
        }
    }
}

/// <summary>
/// <c>artifact_syntax</c> (ERROR, Reactor extension): <c>inputs</c>/<c>outputs</c> entries must be well formed — identifier
/// names, no duplicates, and output paths that are non-empty, relative, and inside the working directory.
/// </summary>
public sealed class ArtifactSyntaxRule : ILintRule
{
    public string Name => "artifact_syntax";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            foreach (var error in ArtifactPins.Parse(node).Errors)
            {
                yield return new Diagnostic(Name, Severity.Error, error.Message) { NodeId = node.Id, Fix = error.Fix };
            }
        }
    }
}

/// <summary><c>artifact_inputs</c> (WARNING, Reactor extension): every declared input should be an output of some stage.</summary>
public sealed class ArtifactInputsRule(IEnumerable<string>? knownTypes = null) : ILintRule
{
    private readonly ArtifactFlow.Types _types = new(knownTypes);

    public string Name => "artifact_inputs";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        var producers = ArtifactFlow.Producers(graph, _types);
        foreach (var (node, input) in ArtifactFlow.Consumed(graph, _types))
        {
            if (!producers.ContainsKey(input))
            {
                yield return new Diagnostic(Name, Severity.Warning, $"Input '{input}' is not produced by any stage, so it is never available.")
                {
                    NodeId = node.Id,
                    Fix = $"Add outputs=\"{input}:…\" to the stage that produces it (or outputs=\"{input}\" for a text artifact).",
                };
            }
        }
    }
}

/// <summary>
/// <c>artifact_order</c> (WARNING, Reactor extension): some producer of each input must be able to run before the
/// consuming stage. Records made inside a parallel branch stay in that branch (branch context is not merged at the
/// fan-in), so a producer that only runs in a branch reaches only later stages of the same branch.
/// </summary>
public sealed class ArtifactOrderRule(IEnumerable<string>? knownTypes = null) : ILintRule
{
    private readonly ArtifactFlow.Types _types = new(knownTypes);

    public string Name => "artifact_order";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        var consumed = ArtifactFlow.Consumed(graph, _types).ToList();
        if (consumed.Count == 0)
        {
            yield break;
        }

        var producers = ArtifactFlow.Producers(graph, _types);
        var flow = new ArtifactFlow(graph, _types);
        foreach (var (node, input) in consumed)
        {
            if (!producers.TryGetValue(input, out var ids) || ids.Any(p => flow.RecordReaches(p, node.Id)))
            {
                continue;
            }

            var list = string.Join(", ", ids.Select(id => $"'{id}'"));
            var confined = ids.Where(p => flow.GraphReaches(p, node.Id)).ToList();
            yield return confined.Count > 0
                ? new Diagnostic(Name, Severity.Warning,
                    $"Input '{input}' is produced only inside parallel branches ({string.Join(", ", confined.Select(id => $"'{id}'"))}); branch context is not merged at the fan-in, so it is never available here.")
                {
                    NodeId = node.Id,
                    Fix = "Consume it later in the same branch, or produce it in a stage after the fan-in.",
                }
                : new Diagnostic(Name, Severity.Warning, $"Input '{input}' is produced only by {list}, which cannot run before this stage, so it is never available.")
                {
                    NodeId = node.Id,
                    Fix = "Produce it in an earlier stage, or reorder the stages.",
                };
        }
    }
}

/// <summary>
/// <c>artifact_handlers</c> (WARNING, Reactor extension): only codergen and tool stages consume and produce artifacts;
/// <c>inputs</c>/<c>outputs</c> on other node types are ignored.
/// </summary>
public sealed class ArtifactHandlersRule(IEnumerable<string>? knownTypes = null) : ILintRule
{
    private readonly ArtifactFlow.Types _types = new(knownTypes);

    public string Name => "artifact_handlers";

    public IEnumerable<Diagnostic> Apply(Graph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            var type = _types.Of(node);
            if (!ArtifactPins.Declares(node) || ArtifactPins.IsSupportedHandlerType(type))
            {
                continue;
            }

            var attributes = new[] { AttrKeys.Inputs, AttrKeys.Outputs }.Where(k => !string.IsNullOrWhiteSpace(node[k])).ToList();
            yield return new Diagnostic(Name, Severity.Warning,
                $"{string.Join(" and ", attributes)} {(attributes.Count > 1 ? "are" : "is")} ignored on {type} nodes; only codergen and tool stages consume and produce artifacts.")
            {
                NodeId = node.Id,
                Fix = "Declare artifacts on a codergen (box) or tool (parallelogram) stage.",
            };
        }
    }
}

/// <summary>How artifact records flow through a graph (shared by the artifact lint rules).</summary>
internal sealed class ArtifactFlow
{
    private readonly Graph _graph;
    private readonly Types _types;
    private readonly HashSet<string> _mainFlow;
    private readonly Dictionary<string, HashSet<string>> _graphReach = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _branchReach = new(StringComparer.Ordinal);

    public ArtifactFlow(Graph graph, Types types)
    {
        _graph = graph;
        _types = types;
        _mainFlow = MainFlow();
    }

    /// <summary>Artifact name → ids of the stages that produce it (stages whose handler supports artifacts).</summary>
    public static Dictionary<string, List<string>> Producers(Graph graph, Types types)
    {
        var producers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Values.Where(n => ArtifactPins.IsSupportedHandlerType(types.Of(n))))
        {
            foreach (var pin in ArtifactPins.Parse(node).Outputs)
            {
                if (!producers.TryGetValue(pin.Name, out var ids))
                {
                    producers[pin.Name] = ids = [];
                }

                ids.Add(node.Id);
            }
        }

        return producers;
    }

    /// <summary>Every (stage, input) pair for stages whose handler supports artifacts.</summary>
    public static IEnumerable<(Node Node, string Input)> Consumed(Graph graph, Types types) =>
        graph.Nodes.Values
            .Where(n => ArtifactPins.IsSupportedHandlerType(types.Of(n)))
            .SelectMany(n => ArtifactPins.Parse(n).Inputs.Select(input => (n, input)));

    /// <summary>True when <paramref name="to"/> can run after <paramref name="from"/> (a path of at least one edge).</summary>
    public bool GraphReaches(string from, string to)
    {
        if (!_graphReach.TryGetValue(from, out var reach))
        {
            reach = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in _graph.OutgoingEdges(from))
            {
                reach.UnionWith(ReachabilityRule.Reachable(_graph, edge.To));
            }

            _graphReach[from] = reach;
        }

        return reach.Contains(to);
    }

    /// <summary>
    /// True when a record made by <paramref name="from"/> can be in the context when <paramref name="to"/> runs: like
    /// <see cref="GraphReaches"/>, except that a stage that runs only inside parallel branches reaches only the rest of its
    /// branch (up to the fan-in or exit where the branch stops).
    /// </summary>
    public bool RecordReaches(string from, string to) => _mainFlow.Contains(from) ? GraphReaches(from, to) : BranchReach(from).Contains(to);

    private HashSet<string> BranchReach(string from)
    {
        if (_branchReach.TryGetValue(from, out var cached))
        {
            return cached;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(_graph.OutgoingEdges(from).Select(e => e.To));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (IsBranchStop(id) || !seen.Add(id))
            {
                continue;
            }

            foreach (var edge in _graph.OutgoingEdges(id))
            {
                queue.Enqueue(edge.To);
            }
        }

        return _branchReach[from] = seen;
    }

    /// <summary>
    /// Stages the top-level traversal can run: from the start node along edges, except that a parallel node continues at
    /// the fan-in/exit nodes where its branches stop (its own edges lead into the branches, which run on cloned contexts).
    /// </summary>
    private HashSet<string> MainFlow()
    {
        if (_graph.FindStartNode() is not { } start)
        {
            return _graph.Nodes.Keys.ToHashSet(StringComparer.Ordinal);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Id };
        var queue = new Queue<string>([start.Id]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var next = _graph.GetNode(id) is { } node && _types.Of(node) == HandlerTypes.Parallel
                ? BranchStops(id)
                : _graph.OutgoingEdges(id).Select(e => e.To);
            foreach (var target in next)
            {
                if (seen.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return seen;
    }

    private IEnumerable<string> BranchStops(string parallelId)
    {
        var stops = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(_graph.OutgoingEdges(parallelId).Select(e => e.To));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (IsBranchStop(id))
            {
                stops.Add(id);
            }
            else if (seen.Add(id))
            {
                foreach (var edge in _graph.OutgoingEdges(id))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        return stops;
    }

    private bool IsBranchStop(string id) => _graph.GetNode(id) is not { } node || _types.Of(node) == HandlerTypes.FanIn || Graph.IsExitNode(node);

    /// <summary>Resolves the handler type a node runs with, as <see cref="HandlerRegistry.TryResolve"/> does.</summary>
    internal sealed class Types(IEnumerable<string>? knownTypes)
    {
        private readonly HashSet<string> _known = new(knownTypes ?? HandlerTypes.BuiltIn, StringComparer.Ordinal);

        public string Of(Node node)
        {
            if (node.Type.Length > 0 && _known.Contains(node.Type))
            {
                return node.Type;
            }

            var shapeType = HandlerRegistry.ShapeType(node);
            return _known.Contains(shapeType) ? shapeType : HandlerTypes.Codergen;
        }
    }
}

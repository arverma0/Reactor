using Reactor.Graphs;
using Reactor.Stylesheets;

namespace Reactor.Transforms;

/// <summary>
/// A graph rewrite applied after parsing and before validation. Implementations must not modify the input graph;
/// return a new or cloned graph instead.
/// </summary>
public interface IGraphTransform
{
    string Name { get; }

    Graph Apply(Graph graph);
}

/// <summary>Expands <c>$goal</c> in node prompts to the graph-level <c>goal</c> attribute.</summary>
public sealed class VariableExpansionTransform : IGraphTransform
{
    public const string GoalVariable = "$goal";

    public string Name => "variable_expansion";

    public Graph Apply(Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var result = graph.Clone();
        var goal = result.Goal;
        foreach (var node in result.Nodes.Values)
        {
            if (node[AttrKeys.Prompt] is { } prompt && prompt.Contains(GoalVariable, StringComparison.Ordinal))
            {
                node[AttrKeys.Prompt] = Expand(prompt, goal);
            }
        }

        return result;
    }

    /// <summary>Simple string replacement of <c>$goal</c> (not a templating engine).</summary>
    public static string Expand(string text, string goal) =>
        text.Replace(GoalVariable, goal ?? "", StringComparison.Ordinal);
}

/// <summary>
/// Applies the graph's <c>model_stylesheet</c>: for each node, properties resolved by the stylesheet are set unless the
/// node declares them explicitly. An unparseable stylesheet is left for validation to report (no-op here).
/// </summary>
public sealed class StylesheetTransform : IGraphTransform
{
    public string Name => "model_stylesheet";

    public Graph Apply(Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var result = graph.Clone();
        if (!ModelStylesheet.TryParse(result.ModelStylesheet, out var stylesheet, out _) || stylesheet.Rules.Count == 0)
        {
            return result;
        }

        foreach (var node in result.Nodes.Values)
        {
            foreach (var (property, value) in stylesheet.Resolve(node))
            {
                if (!node.ExplicitKeys.Contains(property))
                {
                    node[property] = value;
                }
            }
        }

        return result;
    }
}

/// <summary>Transform registration and the built-in transform chain.</summary>
public static class GraphTransforms
{
    /// <summary>The built-in transforms, in order: stylesheet application, then variable expansion.</summary>
    public static IReadOnlyList<IGraphTransform> BuiltIn { get; } = [new StylesheetTransform(), new VariableExpansionTransform()];

    /// <summary>Applies the built-in transforms followed by <paramref name="custom"/> transforms in registration order.</summary>
    public static Graph ApplyAll(Graph graph, IEnumerable<IGraphTransform>? custom = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var current = graph;
        foreach (var transform in BuiltIn.Concat(custom ?? []))
        {
            current = transform.Apply(current) ?? throw new InvalidOperationException($"Transform '{transform.Name}' returned null.");
        }

        return current;
    }
}

using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Stylesheets;

namespace Reactor.Server.Studio;

/// <summary>
/// Describes a parsed graph for the Studio: everything needed to rebuild the document losslessly (attributes, explicit keys,
/// default blocks, subgraphs) plus derived information (handler types, classes, the effective model settings and which
/// stylesheet rule supplied them).
/// </summary>
public static class StudioGraphJson
{
    /// <summary>The JSON description of <paramref name="graph"/> (as parsed, before transforms).</summary>
    public static JsonObject Describe(Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ModelStylesheet.TryParse(graph.ModelStylesheet, out var stylesheet, out _);
        var innermost = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var subgraph in graph.Subgraphs)
        {
            foreach (var id in subgraph.NodeIds)
            {
                innermost.TryAdd(id, subgraph.Index);
            }
        }

        var nodes = new JsonArray();
        foreach (var node in graph.Nodes.Values)
        {
            nodes.Add(new JsonObject
            {
                ["id"] = node.Id,
                ["attributes"] = ToJson(node.Attributes),
                ["explicit"] = new JsonArray(node.ExplicitKeys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()),
                ["classes"] = new JsonArray(node.Classes.Select(c => (JsonNode?)c).ToArray()),
                ["subgraph"] = innermost.TryGetValue(node.Id, out var index) ? index : null,
                ["handler"] = HandlerTypes.Resolve(node),
                ["effective"] = Effective(node, stylesheet),
            });
        }

        var edges = new JsonArray();
        foreach (var edge in graph.Edges)
        {
            edges.Add(new JsonObject { ["from"] = edge.From, ["to"] = edge.To, ["attributes"] = ToJson(edge.Attributes) });
        }

        var subgraphs = new JsonArray();
        foreach (var subgraph in graph.Subgraphs)
        {
            subgraphs.Add(new JsonObject
            {
                ["index"] = subgraph.Index,
                ["name"] = subgraph.Name,
                ["parent"] = subgraph.ParentIndex,
                ["label"] = subgraph.Label,
                ["class"] = subgraph.DerivedClass,
                ["attributes"] = ToJson(subgraph.Attributes),
                ["node_defaults"] = ToJson(subgraph.NodeDefaults),
                ["edge_defaults"] = ToJson(subgraph.EdgeDefaults),
                ["nodes"] = new JsonArray(subgraph.NodeIds.Select(id => (JsonNode?)id).ToArray()),
            });
        }

        return new JsonObject
        {
            ["name"] = graph.Name,
            ["attributes"] = ToJson(graph.Attributes),
            ["node_defaults"] = ToJson(graph.NodeDefaults),
            ["edge_defaults"] = ToJson(graph.EdgeDefaults),
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["subgraphs"] = subgraphs,
        };
    }

    /// <summary>
    /// Effective <c>llm_model</c>/<c>llm_provider</c>/<c>reasoning_effort</c> for a node with their source: <c>node</c>
    /// (explicit attribute), <c>stylesheet</c> (with the winning selector), or <c>default</c> (a <c>node [...]</c> block).
    /// Mirrors <see cref="Transforms.StylesheetTransform"/>: the stylesheet overrides defaults but never explicit attributes.
    /// </summary>
    public static JsonObject Effective(Node node, ModelStylesheet stylesheet)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(stylesheet);
        var winners = stylesheet.ResolveWithRules(node);
        var result = new JsonObject();
        foreach (var property in new[] { AttrKeys.LlmModel, AttrKeys.LlmProvider, AttrKeys.ReasoningEffort })
        {
            if (node.ExplicitKeys.Contains(property) && node[property] is { Length: > 0 } explicitValue)
            {
                result[property] = new JsonObject { ["value"] = explicitValue, ["source"] = "node" };
            }
            else if (winners.TryGetValue(property, out var winner))
            {
                result[property] = new JsonObject { ["value"] = winner.Value, ["source"] = "stylesheet", ["selector"] = winner.Rule.Selector.ToString() };
            }
            else if (node[property] is { Length: > 0 } inherited)
            {
                result[property] = new JsonObject { ["value"] = inherited, ["source"] = "default" };
            }
        }

        return result;
    }

    /// <summary>Validation diagnostics in the API's wire format.</summary>
    public static JsonArray Diagnostics(PreparedPipeline prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var array = new JsonArray();
        foreach (var d in prepared.Diagnostics.Select(DiagnosticDto.From))
        {
            array.Add(new JsonObject
            {
                ["rule"] = d.Rule,
                ["severity"] = d.Severity,
                ["message"] = d.Message,
                ["node_id"] = d.NodeId,
                ["edge"] = d.Edge,
                ["fix"] = d.Fix,
            });
        }

        return array;
    }

    private static JsonObject ToJson(IReadOnlyDictionary<string, string> attributes)
    {
        var json = new JsonObject();
        foreach (var (key, value) in attributes)
        {
            json[key] = value;
        }

        return json;
    }
}

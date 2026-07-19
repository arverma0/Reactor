using System.Text;

namespace Reactor.Graphs;

/// <summary>Serializes a <see cref="Graph"/> back to DOT (flattened: subgraph structure is not preserved).</summary>
public static class DotWriter
{
    /// <summary>Writes <paramref name="graph"/> as DOT.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="nodeOverrides">Optional extra/overriding attributes per node (e.g. status colors for rendering).</param>
    /// <param name="includeAttribute">Optional filter for which node/edge/graph attribute keys to emit.</param>
    public static string Write(
        Graph graph,
        Func<Node, IReadOnlyDictionary<string, string>?>? nodeOverrides = null,
        Func<string, bool>? includeAttribute = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var include = includeAttribute ?? (_ => true);
        var sb = new StringBuilder();
        sb.Append("digraph ").Append(AttributeValues.IsIdentifier(graph.Name) && !DotParser.IsKeyword(graph.Name) ? graph.Name : Quote(graph.Name)).Append(" {\n");

        var graphAttributes = graph.Attributes.Where(kv => include(kv.Key)).ToList();
        if (graphAttributes.Count > 0)
        {
            sb.Append("    graph [");
            AppendAttributes(sb, graphAttributes, indent: "        ");
            sb.Append("]\n");
        }

        foreach (var node in graph.Nodes.Values)
        {
            var attributes = new Dictionary<string, string>(node.Attributes, StringComparer.Ordinal);
            if (node.DerivedClasses.Count > 0)
            {
                attributes[AttrKeys.Class] = string.Join(",", node.Classes);
            }

            if (nodeOverrides?.Invoke(node) is { } overrides)
            {
                foreach (var (key, value) in overrides)
                {
                    attributes[key] = value;
                }
            }

            var filtered = attributes.Where(kv => include(kv.Key)).ToList();
            sb.Append("    ").Append(node.Id);
            if (filtered.Count > 0)
            {
                sb.Append(" [");
                AppendAttributes(sb, filtered, indent: null);
                sb.Append(']');
            }

            sb.Append('\n');
        }

        foreach (var edge in graph.Edges)
        {
            sb.Append("    ").Append(edge.From).Append(" -> ").Append(edge.To);
            var filtered = edge.Attributes.Where(kv => include(kv.Key)).ToList();
            if (filtered.Count > 0)
            {
                sb.Append(" [");
                AppendAttributes(sb, filtered, indent: null);
                sb.Append(']');
            }

            sb.Append('\n');
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Quotes a value for DOT when it cannot be written bare.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\r':
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static void AppendAttributes(StringBuilder sb, IReadOnlyList<KeyValuePair<string, string>> attributes, string? indent)
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(indent is null ? ", " : ",\n" + indent);
            }
            else if (indent is not null)
            {
                sb.Append('\n').Append(indent);
            }

            var (key, value) = attributes[i];
            sb.Append(key).Append('=').Append(FormatValue(value));
        }

        if (indent is not null)
        {
            sb.Append('\n').Append(indent[..^4]);
        }
    }

    private static string FormatValue(string value) =>
        AttributeValues.IsUnquotedValue(value) && !DotParser.IsKeyword(value) ? value : Quote(value);
}

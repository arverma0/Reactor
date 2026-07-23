namespace Reactor.Graphs;

/// <summary>
/// Structural record of a <c>subgraph { ... }</c> block. The engine flattens subgraphs (they only scope defaults and derive
/// stylesheet classes), so this information is not used for execution; editors use it to round-trip clusters, their
/// labels, and their default blocks.
/// </summary>
public sealed class SubgraphInfo
{
    public SubgraphInfo(string? name, string? parentName, int index)
    {
        Name = name;
        ParentName = parentName;
        Index = index;
    }

    /// <summary>The subgraph name (e.g. <c>cluster_design</c>), or null for an anonymous <c>{ ... }</c> block.</summary>
    public string? Name { get; }

    /// <summary>Name of the enclosing subgraph, or null when nested directly in the graph (or in an anonymous subgraph).</summary>
    public string? ParentName { get; }

    /// <summary>Position in declaration order (an enclosing block comes before the blocks nested in it).</summary>
    public int Index { get; }

    /// <summary>Index of the enclosing subgraph in <see cref="Graph.Subgraphs"/>, or -1 when nested directly in the graph.</summary>
    public int ParentIndex { get; internal set; } = -1;

    /// <summary>Attributes declared inside the block (<c>label="..."</c>, <c>graph [...]</c>).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary><c>node [...]</c> defaults declared inside this block (not including inherited defaults).</summary>
    public Dictionary<string, string> NodeDefaults { get; } = new(StringComparer.Ordinal);

    /// <summary><c>edge [...]</c> defaults declared inside this block (not including inherited defaults).</summary>
    public Dictionary<string, string> EdgeDefaults { get; } = new(StringComparer.Ordinal);

    /// <summary>Nodes whose innermost enclosing subgraph is this one, in first-mention order.</summary>
    public List<string> NodeIds { get; } = [];

    /// <summary>The subgraph label, when set.</summary>
    public string? Label => Attributes.TryGetValue(AttrKeys.Label, out var label) ? label : null;

    /// <summary>The stylesheet class derived from the label (<c>"Deep thinking"</c> → <c>deep-thinking</c>), when any.</summary>
    public string? DerivedClass => Label is { } label && DotParser.DeriveClassName(label) is { Length: > 0 } derived ? derived : null;

    public SubgraphInfo Clone()
    {
        var clone = new SubgraphInfo(Name, ParentName, Index) { ParentIndex = ParentIndex };
        foreach (var (key, value) in Attributes)
        {
            clone.Attributes[key] = value;
        }

        foreach (var (key, value) in NodeDefaults)
        {
            clone.NodeDefaults[key] = value;
        }

        foreach (var (key, value) in EdgeDefaults)
        {
            clone.EdgeDefaults[key] = value;
        }

        clone.NodeIds.AddRange(NodeIds);
        return clone;
    }

    public override string ToString() => Name ?? $"subgraph #{Index}";
}

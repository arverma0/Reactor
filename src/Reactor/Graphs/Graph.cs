namespace Reactor.Graphs;

/// <summary>
/// An in-memory pipeline graph: graph attributes, nodes (in declaration order), and edges (in declaration order).
/// </summary>
public sealed class Graph
{
    private readonly OrderedDictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<Edge> _edges = [];

    public Graph(string name = "Pipeline")
    {
        Name = name;
    }

    public string Name { get; set; }

    /// <summary>Graph-level attributes (<c>graph [...]</c> blocks and top-level <c>key = value</c> declarations).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>The DOT source this graph was parsed from, when available.</summary>
    public string? Source { get; set; }

    /// <summary>The file the graph was loaded from, when available (relative child pipeline paths may resolve against it).</summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Top-level <c>node [...]</c> defaults as declared in the source (already merged into each node's attributes; kept so
    /// editors can write them back).
    /// </summary>
    public Dictionary<string, string> NodeDefaults { get; } = new(StringComparer.Ordinal);

    /// <summary>Top-level <c>edge [...]</c> defaults as declared in the source (already merged into each edge's attributes).</summary>
    public Dictionary<string, string> EdgeDefaults { get; } = new(StringComparer.Ordinal);

    /// <summary>Subgraph blocks recorded by the parser (structure only; the engine works on the flattened graph).</summary>
    public List<SubgraphInfo> Subgraphs { get; } = [];

    public IReadOnlyDictionary<string, Node> Nodes => _nodes;

    public IReadOnlyList<Edge> Edges => _edges;

    public string? this[string key]
    {
        get => Attributes.TryGetValue(key, out var value) ? value : null;
        set
        {
            if (value is null)
            {
                Attributes.Remove(key);
            }
            else
            {
                Attributes[key] = value;
            }
        }
    }

    public string Goal => this[AttrKeys.Goal] ?? "";

    public string Label => this[AttrKeys.Label] ?? "";

    public string ModelStylesheet => this[AttrKeys.ModelStylesheet] ?? "";

    /// <summary>Default additional retries for nodes without <c>max_retries</c> (legacy alias <c>default_max_retry</c>).</summary>
    public int DefaultMaxRetries =>
        AttributeValues.ParseInt(this[AttrKeys.DefaultMaxRetries]) ?? AttributeValues.ParseInt(this[AttrKeys.DefaultMaxRetryLegacy]) ?? 0;

    public string RetryTarget => this[AttrKeys.RetryTarget] ?? "";

    public string FallbackRetryTarget => this[AttrKeys.FallbackRetryTarget] ?? "";

    /// <summary>Default fidelity mode; empty means unset (runtime fallback is <c>compact</c>).</summary>
    public string DefaultFidelity => this[AttrKeys.DefaultFidelity] ?? "";

    public Node? GetNode(string id) => _nodes.TryGetValue(id, out var node) ? node : null;

    public bool ContainsNode(string id) => _nodes.ContainsKey(id);

    /// <summary>Adds a node, replacing any node with the same id.</summary>
    public Node AddNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes[node.Id] = node;
        return node;
    }

    public Node GetOrAddNode(string id)
    {
        if (!_nodes.TryGetValue(id, out var node))
        {
            node = new Node(id);
            _nodes[id] = node;
        }

        return node;
    }

    /// <summary>Removes a node and every edge touching it.</summary>
    public bool RemoveNode(string id)
    {
        if (!_nodes.Remove(id))
        {
            return false;
        }

        _edges.RemoveAll(e => e.From == id || e.To == id);
        return true;
    }

    public Edge AddEdge(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        _edges.Add(edge);
        return edge;
    }

    public Edge AddEdge(string from, string to, IReadOnlyDictionary<string, string>? attributes = null)
    {
        var edge = new Edge(from, to);
        if (attributes is not null)
        {
            foreach (var (key, value) in attributes)
            {
                edge.Attributes[key] = value;
            }
        }

        return AddEdge(edge);
    }

    public bool RemoveEdge(Edge edge) => _edges.Remove(edge);

    /// <summary>Outgoing edges of <paramref name="nodeId"/>, in declaration order.</summary>
    public IReadOnlyList<Edge> OutgoingEdges(string nodeId) => _edges.Where(e => e.From == nodeId).ToList();

    /// <summary>Incoming edges of <paramref name="nodeId"/>, in declaration order.</summary>
    public IReadOnlyList<Edge> IncomingEdges(string nodeId) => _edges.Where(e => e.To == nodeId).ToList();

    /// <summary>All nodes that qualify as start nodes (shape=Mdiamond, type=start, or id start/Start without a shape).</summary>
    public IReadOnlyList<Node> StartNodes => _nodes.Values.Where(IsStartNode).ToList();

    /// <summary>All nodes that qualify as exit nodes (shape=Msquare, type=exit, or id exit/end without a shape).</summary>
    public IReadOnlyList<Node> ExitNodes => _nodes.Values.Where(IsExitNode).ToList();

    /// <summary>
    /// Resolves the start node: (1) shape=Mdiamond, (2) id <c>start</c>/<c>Start</c>. Returns null when absent.
    /// </summary>
    public Node? FindStartNode() =>
        _nodes.Values.FirstOrDefault(n => n.Shape == "Mdiamond")
        ?? _nodes.Values.FirstOrDefault(n => n.Type == HandlerTypes.Start)
        ?? GetNode("start")
        ?? GetNode("Start");

    public static bool IsStartNode(Node node) => HandlerTypes.Resolve(node) == HandlerTypes.Start;

    public static bool IsExitNode(Node node) => HandlerTypes.Resolve(node) == HandlerTypes.Exit;

    internal static bool IsStartId(string id) => id is "start" or "Start" or "START";

    internal static bool IsExitId(string id) => id.Equals("exit", StringComparison.OrdinalIgnoreCase) || id.Equals("end", StringComparison.OrdinalIgnoreCase);

    /// <summary>Deep copy (nodes, edges, attributes).</summary>
    public Graph Clone()
    {
        var clone = new Graph(Name) { Source = Source, SourcePath = SourcePath };
        foreach (var (key, value) in Attributes)
        {
            clone.Attributes[key] = value;
        }

        foreach (var node in _nodes.Values)
        {
            clone.AddNode(node.Clone());
        }

        foreach (var edge in _edges)
        {
            clone.AddEdge(edge.Clone());
        }

        foreach (var (key, value) in NodeDefaults)
        {
            clone.NodeDefaults[key] = value;
        }

        foreach (var (key, value) in EdgeDefaults)
        {
            clone.EdgeDefaults[key] = value;
        }

        clone.Subgraphs.AddRange(Subgraphs.Select(s => s.Clone()));
        return clone;
    }

    public override string ToString() => $"digraph {Name} ({_nodes.Count} nodes, {_edges.Count} edges)";
}

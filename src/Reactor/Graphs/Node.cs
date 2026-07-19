namespace Reactor.Graphs;

/// <summary>A pipeline stage. Attributes hold the effective values (defaults merged with explicit values).</summary>
public sealed class Node
{
    public Node(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; }

    /// <summary>Effective attributes: node defaults in scope at creation, overridden by explicit attributes.</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>Keys set explicitly on a node statement (as opposed to inherited from <c>node [...]</c> defaults).</summary>
    public HashSet<string> ExplicitKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Classes derived from enclosing subgraph labels, outermost first.</summary>
    public List<string> DerivedClasses { get; } = [];

    /// <summary>Key of the innermost enclosing subgraph (derived class, or subgraph name), used for thread resolution.</summary>
    public string? SubgraphScope { get; set; }

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

    public bool HasAttribute(string key) => Attributes.ContainsKey(key);

    /// <summary>Display name; defaults to the node id.</summary>
    public string Label => NonEmpty(AttrKeys.Label) ?? Id;

    /// <summary>Graphviz shape; defaults to <c>box</c>.</summary>
    public string Shape => NonEmpty(AttrKeys.Shape) ?? "box";

    /// <summary>Explicit handler type override (empty when unset).</summary>
    public string Type => this[AttrKeys.Type] ?? "";

    public string Prompt => this[AttrKeys.Prompt] ?? "";

    /// <summary>Additional attempts beyond the first; null when not set on the node.</summary>
    public int? MaxRetries => AttributeValues.ParseInt(this[AttrKeys.MaxRetries]);

    public bool GoalGate => AttributeValues.ParseBool(this[AttrKeys.GoalGate]) ?? false;

    public string RetryTarget => this[AttrKeys.RetryTarget] ?? "";

    public string FallbackRetryTarget => this[AttrKeys.FallbackRetryTarget] ?? "";

    public string Fidelity => this[AttrKeys.Fidelity] ?? "";

    public string ThreadId => this[AttrKeys.ThreadId] ?? "";

    public TimeSpan? Timeout => AttributeValues.ParseDuration(this[AttrKeys.Timeout]);

    public string? LlmModel => NonEmpty(AttrKeys.LlmModel);

    public string? LlmProvider => NonEmpty(AttrKeys.LlmProvider);

    /// <summary>Reasoning effort; defaults to <c>high</c>.</summary>
    public string ReasoningEffort => NonEmpty(AttrKeys.ReasoningEffort) ?? "high";

    public bool AutoStatus => AttributeValues.ParseBool(this[AttrKeys.AutoStatus]) ?? false;

    public bool AllowPartial => AttributeValues.ParseBool(this[AttrKeys.AllowPartial]) ?? false;

    /// <summary>Classes from the comma-separated <c>class</c> attribute plus subgraph-derived classes.</summary>
    public IReadOnlyList<string> Classes
    {
        get
        {
            var classes = new List<string>();
            if (this[AttrKeys.Class] is { } explicitClasses)
            {
                foreach (var c in explicitClasses.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!classes.Contains(c, StringComparer.Ordinal))
                    {
                        classes.Add(c);
                    }
                }
            }

            foreach (var c in DerivedClasses)
            {
                if (!classes.Contains(c, StringComparer.Ordinal))
                {
                    classes.Add(c);
                }
            }

            return classes;
        }
    }

    public Node Clone()
    {
        var clone = new Node(Id) { SubgraphScope = SubgraphScope };
        foreach (var (key, value) in Attributes)
        {
            clone.Attributes[key] = value;
        }

        clone.ExplicitKeys.UnionWith(ExplicitKeys);
        clone.DerivedClasses.AddRange(DerivedClasses);
        return clone;
    }

    public override string ToString() => Id;

    private string? NonEmpty(string key) => this[key] is { Length: > 0 } value ? value : null;
}

/// <summary>A directed transition between two nodes.</summary>
public sealed class Edge
{
    public Edge(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        From = from;
        To = to;
    }

    public string From { get; }

    public string To { get; }

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

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

    /// <summary>Human-facing caption and routing key.</summary>
    public string Label => this[AttrKeys.Label] ?? "";

    /// <summary>Boolean guard expression (empty = unconditional).</summary>
    public string Condition => this[AttrKeys.Condition] ?? "";

    public bool IsConditional => !string.IsNullOrWhiteSpace(Condition);

    public int Weight => AttributeValues.ParseInt(this[AttrKeys.Weight]) ?? 0;

    public string Fidelity => this[AttrKeys.Fidelity] ?? "";

    public string ThreadId => this[AttrKeys.ThreadId] ?? "";

    public bool LoopRestart => AttributeValues.ParseBool(this[AttrKeys.LoopRestart]) ?? false;

    public Edge Clone()
    {
        var clone = new Edge(From, To);
        foreach (var (key, value) in Attributes)
        {
            clone.Attributes[key] = value;
        }

        return clone;
    }

    /// <summary>Copy of this edge with different endpoints (used by graph-rewriting transforms).</summary>
    public Edge WithEndpoints(string from, string to)
    {
        var clone = new Edge(from, to);
        foreach (var (key, value) in Attributes)
        {
            clone.Attributes[key] = value;
        }

        return clone;
    }

    public override string ToString() => $"{From} -> {To}";
}

using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Handlers;

/// <summary>
/// A node handler (spec 4.1). Handlers must be stateless or synchronize shared state. Exceptions are caught by the engine
/// and converted to FAIL outcomes (or retried when transient). Returning null means "no status written": the engine then
/// reads <c>{logsRoot}/{node.Id}/status.json</c> if present, or applies <c>auto_status</c>.
/// </summary>
public interface INodeHandler
{
    /// <param name="node">The node with all of its attributes.</param>
    /// <param name="context">The shared run context (read/write). Per-run services are available via <see cref="PipelineContext.Run"/>.</param>
    /// <param name="graph">The full graph.</param>
    /// <param name="logsRoot">The run's log/artifact directory.</param>
    /// <param name="cancellationToken">Cancelled on pipeline cancellation or node timeout.</param>
    Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken);
}

/// <summary>Marker: the engine executes this handler exactly once (no retries), e.g. pass-through routing nodes.</summary>
public interface ISingleExecutionHandler : INodeHandler
{
}

/// <summary>Marker: the handler enforces the node's <c>timeout</c> itself (e.g. human gates), so the engine does not cancel it.</summary>
public interface ISelfTimedHandler : INodeHandler
{
}

/// <summary>Maps handler type strings to handler instances (spec 4.2).</summary>
public sealed class HandlerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, INodeHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>The fallback handler (typically codergen).</summary>
    public INodeHandler? DefaultHandler { get; set; }

    /// <summary>Registers a handler; registering an existing type replaces it.</summary>
    public void Register(string type, INodeHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers[type] = handler;
        }
    }

    public bool Unregister(string type)
    {
        lock (_gate)
        {
            return _handlers.Remove(type);
        }
    }

    public bool IsRegistered(string type)
    {
        lock (_gate)
        {
            return _handlers.ContainsKey(type);
        }
    }

    public IReadOnlyCollection<string> Types
    {
        get
        {
            lock (_gate)
            {
                return _handlers.Keys.ToList();
            }
        }
    }

    public INodeHandler? Get(string type)
    {
        lock (_gate)
        {
            return _handlers.GetValueOrDefault(type);
        }
    }

    /// <summary>
    /// Resolves a node's handler: (1) explicit <c>type</c> if registered, (2) shape-based type if registered, (3) default.
    /// </summary>
    /// <exception cref="InvalidOperationException">No handler matches and no default is set.</exception>
    public INodeHandler Resolve(Node node) =>
        TryResolve(node, out _) ?? throw new InvalidOperationException($"No handler is registered for node '{node.Id}' and no default handler is set.");

    /// <summary>Resolves a node's handler and the type string it was resolved by.</summary>
    public INodeHandler? TryResolve(Node node, out string resolvedType)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            if (node.Type.Length > 0 && _handlers.TryGetValue(node.Type, out var explicitHandler))
            {
                resolvedType = node.Type;
                return explicitHandler;
            }

            var shapeType = ShapeType(node);
            if (_handlers.TryGetValue(shapeType, out var shapeHandler))
            {
                resolvedType = shapeType;
                return shapeHandler;
            }

            resolvedType = HandlerTypes.Codergen;
            return DefaultHandler;
        }
    }

    /// <summary>The type implied by shape (or start/exit id) ignoring the explicit <c>type</c> attribute.</summary>
    public static string ShapeType(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Attributes.TryGetValue(AttrKeys.Shape, out var shape) && HandlerTypes.ShapeToType.TryGetValue(shape, out var mapped))
        {
            return mapped;
        }

        if (!node.Attributes.ContainsKey(AttrKeys.Shape))
        {
            if (Graph.IsStartId(node.Id))
            {
                return HandlerTypes.Start;
            }

            if (Graph.IsExitId(node.Id))
            {
                return HandlerTypes.Exit;
            }
        }

        return HandlerTypes.Codergen;
    }
}

/// <summary>
/// The LLM execution backend for codergen stages (spec 4.5). Implementations may call an LLM API, run a coding agent
/// loop, drive a CLI agent, or anything else.
/// </summary>
public interface ICodergenBackend
{
    /// <summary>Runs the stage. Return text (the handler wraps it in a SUCCESS outcome) or a full <see cref="Outcome"/>.</summary>
    Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken);

    /// <summary>
    /// True when the backend runs an agent that can write files, so the prompt can include the status-file protocol
    /// (writing <c>status.json</c> to report fail/retry outcomes and routing preferences).
    /// </summary>
    bool SupportsStatusFile => false;

    /// <summary>Per-node variant of <see cref="SupportsStatusFile"/> (routing backends answer for the backend a node uses).</summary>
    bool SupportsStatusFileFor(Node node) => SupportsStatusFile;
}

/// <summary>A backend result: response text or an explicit outcome, plus optional metadata recorded with the stage.</summary>
public sealed class CodergenResult
{
    private CodergenResult(string? text, Outcome? outcome)
    {
        Text = text;
        Outcome = outcome;
    }

    public string? Text { get; }

    public Outcome? Outcome { get; }

    /// <summary>Backend metadata (model, tokens, session id, cost, ...) written to <c>backend.json</c>.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();

    /// <summary>A copy of this result carrying <paramref name="metadata"/>.</summary>
    public CodergenResult WithMetadata(IReadOnlyDictionary<string, object?> metadata) =>
        new(Text, Outcome) { Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata)) };

    public static CodergenResult FromText(string text) => new(text ?? "", null);

    public static CodergenResult FromOutcome(Outcome outcome) => new(null, outcome ?? throw new ArgumentNullException(nameof(outcome)));

    /// <summary>An explicit outcome plus the response text (written to <c>response.md</c>).</summary>
    public static CodergenResult FromOutcome(Outcome outcome, string? text) => new(text, outcome ?? throw new ArgumentNullException(nameof(outcome)));

    public static implicit operator CodergenResult(string text) => FromText(text);

    public static implicit operator CodergenResult(Outcome outcome) => FromOutcome(outcome);
}

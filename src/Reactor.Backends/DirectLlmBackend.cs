using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Llm;

namespace Reactor.Backends;

/// <summary>Options for <see cref="DirectLlmBackend"/>.</summary>
public sealed record DirectLlmBackendOptions
{
    public required Client Client { get; init; }

    public string? DefaultProvider { get; init; }

    public string? DefaultModel { get; init; }

    /// <summary>Optional system prompt for every stage.</summary>
    public string? SystemPrompt { get; init; } =
        "You are one stage of an automated software pipeline. Complete the task you are given and reply with the result.";

    public int? MaxTokens { get; init; }

    public RetryPolicy RetryPolicy { get; init; } = RetryPolicy.Default;
}

/// <summary>
/// Answers codergen stages with a single LLM call (no tools) — for planning, review, and summarization stages that do not
/// need to act on the workspace. <c>full</c>-fidelity stages on the same thread share a message history.
/// </summary>
public sealed class DirectLlmBackend(DirectLlmBackendOptions options) : ICodergenBackend
{
    private readonly DirectLlmBackendOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ThreadLeasePool<List<Message>> _threads = new();

    public bool SupportsStatusFile => false;

    public async Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(context);
        var stage = StageSettings.From(node, context);
        var (provider, model) = ModelResolution.Resolve(stage, _options.Client, _options.DefaultProvider, _options.DefaultModel);

        await using var lease = await _threads.AcquireAsync(stage.ThreadKey("direct:" + provider), () => []).ConfigureAwait(false);
        var history = lease.Value;
        var messages = new List<Message>();
        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
        {
            messages.Add(Message.System(_options.SystemPrompt));
        }

        messages.AddRange(history);
        messages.Add(Message.User(prompt));
        var request = new Request
        {
            Model = model,
            Provider = provider,
            Messages = messages,
            ReasoningEffort = ModelCatalog.GetModelInfo(model)?.SupportsReasoning == false ? null : stage.ReasoningEffort,
            MaxTokens = _options.MaxTokens,
        };

        context.Run?.Log(node.Id, $"llm call: {provider}/{model}", "info");
        Response response;
        try
        {
            response = await Retry.ExecuteAsync(ct => _options.Client.CompleteAsync(request, ct), _options.RetryPolicy, cancellationToken).ConfigureAwait(false);
        }
        catch (AbortError) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The stage was cancelled.", cancellationToken);
        }

        history.Add(Message.User(prompt));
        history.Add(response.Message);
        return CodergenResult.FromText(response.Text).WithMetadata(new Dictionary<string, object?>
        {
            ["backend"] = "direct",
            ["provider"] = provider,
            ["model"] = response.Model ?? model,
            ["response_id"] = response.Id,
            ["finish_reason"] = response.FinishReason.Reason,
            ["input_tokens"] = response.Usage.InputTokens,
            ["output_tokens"] = response.Usage.OutputTokens,
            ["total_tokens"] = response.Usage.TotalTokens,
            ["reasoning_tokens"] = response.Usage.ReasoningTokens,
        });
    }
}

/// <summary>
/// Routes each codergen stage to a named backend: the node's <c>backend</c> attribute (must name a route), else its
/// <c>llm_provider</c> when a route of that name exists, else the default backend.
/// </summary>
public sealed class BackendRouter : ICodergenBackend, IAsyncDisposable
{
    /// <summary>Node attribute that selects a backend by route name.</summary>
    public const string BackendAttribute = "backend";

    private readonly Dictionary<string, ICodergenBackend> _routes = new(StringComparer.OrdinalIgnoreCase);

    public BackendRouter(ICodergenBackend defaultBackend, string defaultName = "default")
    {
        Default = defaultBackend ?? throw new ArgumentNullException(nameof(defaultBackend));
        DefaultName = defaultName;
        _routes[defaultName] = defaultBackend;
    }

    public ICodergenBackend Default { get; }

    public string DefaultName { get; }

    public IReadOnlyDictionary<string, ICodergenBackend> Routes => _routes;

    /// <summary>Adds (or replaces) a named route.</summary>
    public BackendRouter Add(string name, ICodergenBackend backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _routes[name] = backend ?? throw new ArgumentNullException(nameof(backend));
        return this;
    }

    public bool SupportsStatusFile => Default.SupportsStatusFile;

    public bool SupportsStatusFileFor(Node node) => Select(node).SupportsStatusFileFor(node);

    /// <summary>The backend a node will use.</summary>
    /// <exception cref="InvalidOperationException">The node names a backend that is not configured.</exception>
    public ICodergenBackend Select(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node[BackendAttribute] is { Length: > 0 } explicitName)
        {
            return _routes.TryGetValue(explicitName, out var named)
                ? named
                : throw new InvalidOperationException(
                    $"Node '{node.Id}' requests backend '{explicitName}', which is not configured. Available: {string.Join(", ", _routes.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
        }

        return node.LlmProvider is { } provider && _routes.TryGetValue(provider, out var byProvider) ? byProvider : Default;
    }

    public Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken) =>
        Select(node).RunAsync(node, prompt, context, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var backend in _routes.Values.Distinct())
        {
            switch (backend)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}

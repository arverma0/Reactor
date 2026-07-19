using System.Runtime.CompilerServices;
using Reactor.Llm.Providers;

namespace Reactor.Llm;

/// <summary>
/// The core client: holds provider adapters, routes requests by provider key, and applies middleware.
/// The client holds no per-request mutable state and is safe for concurrent use.
/// </summary>
public sealed class Client : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IProviderAdapter> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IProviderAdapter> _initialized = new(ReferenceEqualityComparer.Instance);
    private readonly List<ILlmMiddleware> _middleware;
    private string? _defaultProvider;

    /// <summary>Creates a client from explicit adapters. No default is assumed unless <paramref name="defaultProvider"/> is set.</summary>
    public Client(
        IReadOnlyDictionary<string, IProviderAdapter>? providers = null,
        string? defaultProvider = null,
        IEnumerable<ILlmMiddleware>? middleware = null)
    {
        if (providers is not null)
        {
            foreach (var (name, adapter) in providers)
            {
                ArgumentNullException.ThrowIfNull(adapter);
                _providers[name] = adapter;
            }
        }

        _middleware = middleware?.ToList() ?? [];
        if (defaultProvider is not null && !_providers.ContainsKey(defaultProvider))
        {
            throw new ConfigurationError($"Default provider '{defaultProvider}' is not registered.");
        }

        _defaultProvider = defaultProvider;
    }

    /// <summary>Creates a client with a single adapter, which becomes the default provider.</summary>
    public static Client Create(IProviderAdapter adapter, params ILlmMiddleware[] middleware)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new Client(new Dictionary<string, IProviderAdapter> { [adapter.Name] = adapter }, adapter.Name, middleware);
    }

    /// <summary>
    /// Creates a client from standard environment variables. Only providers whose keys are present are registered;
    /// the first registered provider (OpenAI, Anthropic, Gemini, OpenAI-compatible, in that order) becomes the default
    /// unless <paramref name="defaultProvider"/> is given.
    /// </summary>
    /// <remarks>
    /// OpenAI: <c>OPENAI_API_KEY</c> (+ <c>OPENAI_BASE_URL</c>, <c>OPENAI_ORG_ID</c>, <c>OPENAI_PROJECT_ID</c>).
    /// Anthropic: <c>ANTHROPIC_API_KEY</c> (+ <c>ANTHROPIC_BASE_URL</c>).
    /// Gemini: <c>GEMINI_API_KEY</c> or <c>GOOGLE_API_KEY</c> (+ <c>GEMINI_BASE_URL</c>).
    /// OpenAI-compatible: <c>OPENAI_COMPATIBLE_BASE_URL</c> (+ <c>OPENAI_COMPATIBLE_API_KEY</c>).
    /// </remarks>
    public static Client FromEnv(
        IReadOnlyDictionary<string, string?>? environment = null,
        string? defaultProvider = null,
        IEnumerable<ILlmMiddleware>? middleware = null,
        AdapterTimeout? timeout = null)
    {
        string? Get(string key)
        {
            var value = environment is not null
                ? (environment.TryGetValue(key, out var v) ? v : null)
                : Environment.GetEnvironmentVariable(key);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        var effectiveTimeout = timeout ?? AdapterTimeout.Default;
        var client = new Client(middleware: middleware);

        if (Get("OPENAI_API_KEY") is { } openAiKey)
        {
            client.RegisterProvider(new OpenAIAdapter(new OpenAIAdapterOptions
            {
                ApiKey = openAiKey,
                BaseUrl = Get("OPENAI_BASE_URL"),
                Organization = Get("OPENAI_ORG_ID"),
                Project = Get("OPENAI_PROJECT_ID"),
                Timeout = effectiveTimeout,
            }));
        }

        if (Get("ANTHROPIC_API_KEY") is { } anthropicKey)
        {
            client.RegisterProvider(new AnthropicAdapter(new AnthropicAdapterOptions
            {
                ApiKey = anthropicKey,
                BaseUrl = Get("ANTHROPIC_BASE_URL"),
                Timeout = effectiveTimeout,
            }));
        }

        if ((Get("GEMINI_API_KEY") ?? Get("GOOGLE_API_KEY")) is { } geminiKey)
        {
            client.RegisterProvider(new GeminiAdapter(new GeminiAdapterOptions
            {
                ApiKey = geminiKey,
                BaseUrl = Get("GEMINI_BASE_URL"),
                Timeout = effectiveTimeout,
            }));
        }

        if (Get("OPENAI_COMPATIBLE_BASE_URL") is { } compatibleBaseUrl)
        {
            client.RegisterProvider(new OpenAICompatibleAdapter(new OpenAICompatibleAdapterOptions
            {
                ApiKey = Get("OPENAI_COMPATIBLE_API_KEY"),
                BaseUrl = compatibleBaseUrl,
                Timeout = effectiveTimeout,
            }));
        }

        if (defaultProvider is not null)
        {
            client.DefaultProvider = defaultProvider;
        }

        return client;
    }

    /// <summary>The provider used when a request does not name one.</summary>
    public string? DefaultProvider
    {
        get
        {
            lock (_gate)
            {
                return _defaultProvider;
            }
        }

        set
        {
            lock (_gate)
            {
                if (value is not null && !_providers.ContainsKey(value))
                {
                    throw new ConfigurationError($"Default provider '{value}' is not registered.");
                }

                _defaultProvider = value;
            }
        }
    }

    /// <summary>Registered provider keys.</summary>
    public IReadOnlyList<string> ProviderNames
    {
        get
        {
            lock (_gate)
            {
                return _providers.Keys.ToList();
            }
        }
    }

    public IReadOnlyList<ILlmMiddleware> Middleware => _middleware;

    /// <summary>
    /// Registers (or replaces) an adapter under <paramref name="name"/> (defaults to <see cref="IProviderAdapter.Name"/>).
    /// The first registered adapter becomes the default when no default is set.
    /// </summary>
    public void RegisterProvider(IProviderAdapter adapter, string? name = null, bool setAsDefault = false)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var key = name ?? adapter.Name;
        lock (_gate)
        {
            _providers[key] = adapter;
            if (setAsDefault || _defaultProvider is null)
            {
                _defaultProvider = key;
            }
        }
    }

    public bool HasProvider(string name)
    {
        lock (_gate)
        {
            return _providers.ContainsKey(name);
        }
    }

    /// <summary>Resolves the adapter for <paramref name="provider"/> (or the default). Never guesses.</summary>
    public IProviderAdapter GetAdapter(string? provider)
    {
        lock (_gate)
        {
            var key = provider ?? _defaultProvider;
            if (key is null)
            {
                throw new ConfigurationError(
                    _providers.Count == 0
                        ? "No LLM providers are configured. Set an API key environment variable (e.g. OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY) or register an adapter."
                        : "The request does not specify a provider and the client has no default provider.");
            }

            if (!_providers.TryGetValue(key, out var adapter))
            {
                throw new ConfigurationError(
                    $"Provider '{key}' is not configured. Registered providers: {(_providers.Count == 0 ? "(none)" : string.Join(", ", _providers.Keys))}.");
            }

            return adapter;
        }
    }

    /// <summary>Sends a request and returns the full response. Does not retry.</summary>
    public async Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (adapter, resolved) = Resolve(request);
        await EnsureInitializedAsync(adapter, cancellationToken).ConfigureAwait(false);

        CompleteNext chain = (req, ct) => adapter.CompleteAsync(req, ct);
        for (var i = _middleware.Count - 1; i >= 0; i--)
        {
            var middleware = _middleware[i];
            var next = chain;
            chain = (req, ct) => middleware.CompleteAsync(req, next, ct);
        }

        return await chain(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request and streams normalized events. Does not retry.</summary>
    public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (adapter, resolved) = Resolve(request);
        return StreamCore(adapter, resolved, cancellationToken);
    }

    private async IAsyncEnumerable<StreamEvent> StreamCore(
        IProviderAdapter adapter,
        Request request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(adapter, cancellationToken).ConfigureAwait(false);

        StreamNext chain = (req, ct) => adapter.StreamAsync(req, ct);
        for (var i = _middleware.Count - 1; i >= 0; i--)
        {
            var middleware = _middleware[i];
            var next = chain;
            chain = (req, ct) => middleware.StreamAsync(req, next, ct);
        }

        await foreach (var evt in chain(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    private (IProviderAdapter Adapter, Request Request) Resolve(Request request)
    {
        var adapter = GetAdapter(request.Provider);
        string providerKey;
        lock (_gate)
        {
            providerKey = request.Provider ?? _defaultProvider!;
        }

        return (adapter, request.Provider is null ? request with { Provider = providerKey } : request);
    }

    private async ValueTask EnsureInitializedAsync(IProviderAdapter adapter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_initialized.Contains(adapter))
            {
                return;
            }
        }

        await adapter.InitializeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _initialized.Add(adapter);
        }
    }

    /// <summary>Closes all registered adapters.</summary>
    public async ValueTask DisposeAsync()
    {
        List<IProviderAdapter> adapters;
        lock (_gate)
        {
            adapters = _providers.Values.Distinct().ToList();
        }

        foreach (var adapter in adapters)
        {
            await adapter.CloseAsync().ConfigureAwait(false);
        }
    }
}

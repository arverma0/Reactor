namespace Reactor.Llm;

/// <summary>
/// The contract every provider adapter implements. Adapters translate the unified <see cref="Request"/> into the
/// provider's native API, and the native response/stream back into <see cref="Response"/>/<see cref="StreamEvent"/>.
/// Adapters never retry; retries are applied by the high-level API or the caller.
/// </summary>
public interface IProviderAdapter
{
    /// <summary>Provider key, e.g. <c>openai</c>, <c>anthropic</c>, <c>gemini</c>.</summary>
    string Name { get; }

    /// <summary>Sends a request and returns the full response once generation finishes.</summary>
    Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request and returns the stream of normalized events. Terminates with a FINISH event.</summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken cancellationToken = default);

    /// <summary>Validates configuration. Called by the client on registration.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Whether a tool choice mode (<c>auto</c>, <c>none</c>, <c>required</c>, <c>named</c>) is supported.</summary>
    bool SupportsToolChoice(string mode) => true;

    /// <summary>Releases resources such as HTTP connections. Called by <see cref="Client.DisposeAsync"/>.</summary>
    ValueTask CloseAsync() => ValueTask.CompletedTask;
}

/// <summary>Adapter-level timeouts.</summary>
public sealed record AdapterTimeout
{
    /// <summary>Time to establish the HTTP connection. Default 10s.</summary>
    public TimeSpan Connect { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Time for the entire request/response cycle (non-streaming) or until headers arrive (streaming). Default 120s.</summary>
    public TimeSpan Request { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Maximum time between consecutive stream events. Default 30s.</summary>
    public TimeSpan StreamRead { get; init; } = TimeSpan.FromSeconds(30);

    public static AdapterTimeout Default { get; } = new();
}

/// <summary>Options shared by HTTP-based provider adapters.</summary>
public record ProviderAdapterOptions
{
    public string? ApiKey { get; init; }

    /// <summary>Overrides the provider's default base URL.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Extra headers sent with every request.</summary>
    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }

    public AdapterTimeout Timeout { get; init; } = AdapterTimeout.Default;

    /// <summary>
    /// Optional HTTP client to use (for proxies, custom handlers, or tests). The adapter does not dispose a supplied client,
    /// and applies its own per-request timeouts.
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}

namespace Reactor.Llm.Providers;

/// <summary>Provider adapter for the Gemini API (/v1beta/models/*:generateContent). (Contract stub: implementation pending.)</summary>
public sealed class GeminiAdapter : IProviderAdapter
{
    private readonly GeminiAdapterOptions _options;

    public GeminiAdapter(GeminiAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Name => "gemini";

    public Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
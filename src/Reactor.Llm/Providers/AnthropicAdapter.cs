namespace Reactor.Llm.Providers;

/// <summary>Provider adapter for the Anthropic Messages API (/v1/messages). (Contract stub: implementation pending.)</summary>
public sealed class AnthropicAdapter : IProviderAdapter
{
    private readonly AnthropicAdapterOptions _options;

    public AnthropicAdapter(AnthropicAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Name => "anthropic";

    public Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
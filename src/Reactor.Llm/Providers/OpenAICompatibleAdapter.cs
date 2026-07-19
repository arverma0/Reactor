namespace Reactor.Llm.Providers;

/// <summary>Provider adapter for the OpenAI-compatible Chat Completions (/v1/chat/completions). (Contract stub: implementation pending.)</summary>
public sealed class OpenAICompatibleAdapter : IProviderAdapter
{
    private readonly OpenAICompatibleAdapterOptions _options;

    public OpenAICompatibleAdapter(OpenAICompatibleAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Name => _options.Name;

    public Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
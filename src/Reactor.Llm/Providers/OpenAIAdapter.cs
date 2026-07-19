namespace Reactor.Llm.Providers;

/// <summary>Provider adapter for the OpenAI Responses API (/v1/responses). (Contract stub: implementation pending.)</summary>
public sealed class OpenAIAdapter : IProviderAdapter
{
    private readonly OpenAIAdapterOptions _options;

    public OpenAIAdapter(OpenAIAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Name => "openai";

    public Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
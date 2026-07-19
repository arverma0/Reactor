using System.Runtime.CompilerServices;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Providers.OpenAICompatible;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers;

/// <summary>
/// Provider adapter for third-party OpenAI-compatible Chat Completions endpoints (vLLM, Ollama, Together, Groq, ...):
/// <c>POST {base}/chat/completions</c>, where <see cref="ProviderAdapterOptions.BaseUrl"/> is required (typically ending
/// in <c>/v1</c>).
/// </summary>
/// <remarks>
/// <para>
/// System and developer messages become <c>system</c> messages in place; tool results become <c>tool</c> messages (tool
/// images follow as a user message); reasoning is not replayed. Responses map <c>reasoning_content</c> (or
/// <c>reasoning</c>) to THINKING parts. Streams request <c>stream_options.include_usage</c>, accumulate tool call
/// argument deltas by index, and finish on <c>data: [DONE]</c>.
/// </para>
/// <para>
/// <c>provider_options[Name]</c> is deep-merged into the request body. The bearer token is sent only when
/// <see cref="ProviderAdapterOptions.ApiKey"/> is set.
/// </para>
/// </remarks>
public sealed class OpenAICompatibleAdapter : IProviderAdapter
{
    private readonly OpenAICompatibleAdapterOptions _options;
    private readonly AdapterTransport _transport;
    private readonly string _url;

    /// <summary>Creates an adapter for an OpenAI-compatible Chat Completions endpoint.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ConfigurationError"><see cref="ProviderAdapterOptions.BaseUrl"/> or the name is missing.</exception>
    public OpenAICompatibleAdapter(OpenAICompatibleAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new ConfigurationError("OpenAICompatibleAdapter requires a BaseUrl (e.g. https://my-vllm-host/v1).");
        }

        if (string.IsNullOrWhiteSpace(options.Name))
        {
            throw new ConfigurationError("OpenAICompatibleAdapter requires a non-empty Name.");
        }

        _options = options;
        _transport = new AdapterTransport(Name, options);
        _url = AdapterSupport.NormalizeBaseUrl(options.BaseUrl, options.BaseUrl) + "/chat/completions";
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    public async Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<Warning>();
        var body = ChatCompletionsRequestBuilder.Build(request, Name, stream: false, warnings);
        var (json, rateLimit) = await _transport.PostJsonAsync(_url, body, Headers(streaming: false), cancellationToken).ConfigureAwait(false);
        return ChatCompletionsResponseParser.Parse(json, request, Name, warnings, rateLimit);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public bool SupportsToolChoice(string mode) => AdapterSupport.IsSupportedToolChoice(mode);

    /// <inheritdoc />
    public ValueTask CloseAsync() => _transport.CloseAsync();

    private async IAsyncEnumerable<StreamEvent> StreamCoreAsync(Request request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var warnings = new List<Warning>();
        var body = ChatCompletionsRequestBuilder.Build(request, Name, stream: true, warnings);
        using var http = await _transport.PostStreamAsync(_url, body, Headers(streaming: true), cancellationToken).ConfigureAwait(false);
        var translator = new ChatCompletionsStreamTranslator(Name, request, warnings, ProviderHttp.ParseRateLimit(http.Response.Headers));
        await foreach (var read in SseStreams.ReadAsync(http.Response, Name, _transport.Timeout.StreamRead, cancellationToken).ConfigureAwait(false))
        {
            foreach (var evt in read.Error is null ? translator.Process(read.Event!) : translator.Fail(read.Error))
            {
                yield return evt;
            }

            if (translator.IsFinished)
            {
                yield break;
            }
        }

        foreach (var evt in translator.Complete())
        {
            yield return evt;
        }
    }

    private Dictionary<string, string> Headers(bool streaming)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            headers.Add(new("Authorization", "Bearer " + _options.ApiKey));
        }

        return AdapterSupport.BuildHeaders(_options, headers, streaming);
    }
}

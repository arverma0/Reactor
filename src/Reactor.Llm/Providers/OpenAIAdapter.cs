using System.Runtime.CompilerServices;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Providers.OpenAI;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers;

/// <summary>
/// Provider adapter for the OpenAI Responses API (<c>POST {base}/responses</c>, default base
/// <c>https://api.openai.com/v1</c>).
/// </summary>
/// <remarks>
/// <para>
/// System and developer messages become <c>instructions</c>; user/assistant messages become <c>message</c> input items;
/// tool calls and results become top-level <c>function_call</c>/<c>function_call_output</c> items. Images returned by
/// tools are delivered in a user message following the tool outputs, because function call outputs are text-only.
/// </para>
/// <para>
/// For reasoning models (catalog <c>supports_reasoning</c>, GPT-5+, o-series, Codex) requests are stateless:
/// <c>"store": false</c> and <c>"include": ["reasoning.encrypted_content"]</c>. Reasoning output items are surfaced as
/// THINKING parts whose <see cref="ContentPart.ProviderMetadata"/> (<c>{"openai": {"encrypted_content", "item_id"}}</c>)
/// lets them be replayed, in order, in the next request. Reasoning summaries are not requested by default; add
/// <c>{"reasoning": {"summary": "auto"}}</c> through <c>provider_options.openai</c> to receive them.
/// </para>
/// <para>
/// <c>provider_options.openai</c>: <c>tools</c> is appended to the function tools (built-in tools such as web search),
/// <c>include</c> is unioned, and every other key is deep-merged into the request body. Stop sequences are not supported
/// by the Responses API and produce a warning.
/// </para>
/// </remarks>
public sealed class OpenAIAdapter : IProviderAdapter
{
    private readonly OpenAIAdapterOptions _options;
    private readonly AdapterTransport _transport;
    private readonly string _url;

    /// <summary>Creates an adapter for the OpenAI Responses API.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public OpenAIAdapter(OpenAIAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _transport = new AdapterTransport(Name, options);
        _url = AdapterSupport.NormalizeBaseUrl(options.BaseUrl, OpenAIAdapterOptions.DefaultBaseUrl) + "/responses";
    }

    /// <inheritdoc />
    public string Name => OpenAIRequestBuilder.ProviderName;

    /// <inheritdoc />
    public async Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<Warning>();
        var body = OpenAIRequestBuilder.Build(request, stream: false, warnings);
        var (json, rateLimit) = await _transport.PostJsonAsync(_url, body, Headers(streaming: false), cancellationToken)
            .ConfigureAwait(false);
        return OpenAIResponseParser.Parse(json, request, Name, warnings, rateLimit);
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
        var body = OpenAIRequestBuilder.Build(request, stream: true, warnings);
        using var http = await _transport.PostStreamAsync(_url, body, Headers(streaming: true), cancellationToken).ConfigureAwait(false);
        var translator = new OpenAIStreamTranslator(Name, request, warnings, ProviderHttp.ParseRateLimit(http.Response.Headers));
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

        if (!string.IsNullOrEmpty(_options.Organization))
        {
            headers.Add(new("OpenAI-Organization", _options.Organization));
        }

        if (!string.IsNullOrEmpty(_options.Project))
        {
            headers.Add(new("OpenAI-Project", _options.Project));
        }

        return AdapterSupport.BuildHeaders(_options, headers, streaming);
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Anthropic;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers;

/// <summary>
/// Provider adapter for the Anthropic Messages API (<c>POST {base}/v1/messages</c>, default base
/// <c>https://api.anthropic.com</c>; a base URL that already ends in <c>/v1</c> is used as is).
/// </summary>
/// <remarks>
/// <para>
/// System and developer messages become <c>system</c> text blocks. Messages strictly alternate: consecutive same-role
/// messages are merged, tool results become <c>tool_result</c> blocks at the start of a user turn, empty text blocks and
/// empty messages are dropped, and a minimal <c>"Continue."</c> user turn is prepended when the conversation would
/// otherwise start with the assistant. Thinking blocks are replayed with their signatures (and redacted thinking
/// verbatim) only while thinking is enabled for the request.
/// </para>
/// <para>
/// <b>Prompt caching heuristic.</b> When <see cref="AnthropicAdapterOptions.AutoCache"/> is true and
/// <c>provider_options.anthropic.auto_cache</c> is not <c>false</c>, <c>"cache_control": {"type": "ephemeral"}</c> is
/// added to (1) the last tool definition, (2) the last system block, and (3) the last cacheable block (text, image,
/// tool_use, tool_result, or document; never thinking) of the final message. Tools, system prompt, and conversation form
/// a stable prefix in agentic loops, so each turn reads the previous turn's cache entry and writes a new one. Explicit
/// breakpoints supplied through <c>ContentPart.ProviderMetadata["anthropic"]["cache_control"]</c> are honored and
/// counted first; at most four breakpoints are ever sent. The <c>prompt-caching-2024-07-31</c> beta header is added
/// whenever a breakpoint is present.
/// </para>
/// <para>
/// <b>Thinking.</b> <c>provider_options.anthropic.thinking</c> is passed through (e.g.
/// <c>{"type": "enabled", "budget_tokens": 10000}</c> or <c>{"type": "adaptive"}</c>); otherwise
/// <see cref="Request.ReasoningEffort"/> maps to a budget (low 2048, medium 8192, high 16384, xhigh/max 32768). With
/// thinking enabled, <c>max_tokens</c> is raised above the budget when needed, temperature/top_p are dropped with a
/// warning, a forced tool choice disables thinking with a warning, and <c>interleaved-thinking-2025-05-14</c> is sent when
/// tools are present.
/// </para>
/// <para>
/// <c>provider_options.anthropic</c>: <c>beta_headers</c> (array or comma-separated string) are joined into
/// <c>anthropic-beta</c>; <c>auto_cache</c> and <c>thinking</c> are consumed as above; every other key is deep-merged into
/// the request body. JSON response formats are requested through a system instruction because the Messages API has no
/// native JSON mode.
/// </para>
/// </remarks>
public sealed class AnthropicAdapter : IProviderAdapter
{
    private const string BetaHeader = "anthropic-beta";

    private readonly AnthropicAdapterOptions _options;
    private readonly AdapterTransport _transport;
    private readonly string _url;

    /// <summary>Creates an adapter for the Anthropic Messages API.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public AnthropicAdapter(AnthropicAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _transport = new AdapterTransport(Name, options);
        var baseUrl = AdapterSupport.NormalizeBaseUrl(options.BaseUrl, AnthropicAdapterOptions.DefaultBaseUrl);
        _url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/messages" : baseUrl + "/v1/messages";
    }

    /// <inheritdoc />
    public string Name => AnthropicRequestBuilder.ProviderName;

    /// <inheritdoc />
    public async Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = AnthropicRequestBuilder.Build(request, _options, stream: false);
        var (json, rateLimit) = await _transport.PostJsonAsync(_url, prepared.Body, Headers(prepared, streaming: false), cancellationToken)
            .ConfigureAwait(false);
        return AnthropicResponseParser.Parse(json, request, Name, prepared.Warnings, rateLimit);
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
        var prepared = AnthropicRequestBuilder.Build(request, _options, stream: true);
        using var http = await _transport.PostStreamAsync(_url, prepared.Body, Headers(prepared, streaming: true), cancellationToken)
            .ConfigureAwait(false);
        var translator = new AnthropicStreamTranslator(Name, request, prepared.Warnings, ProviderHttp.ParseRateLimit(http.Response.Headers));
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

    private Dictionary<string, string> Headers(AnthropicPreparedRequest prepared, bool streaming)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            headers.Add(new("x-api-key", _options.ApiKey));
        }

        headers.Add(new("anthropic-version", _options.ApiVersion));
        var result = AdapterSupport.BuildHeaders(_options, headers, streaming);

        // Beta values configured as default headers are unioned with the per-request ones rather than replaced.
        var betas = new List<string>();
        if (result.TryGetValue(BetaHeader, out var configured))
        {
            AnthropicRequestBuilder.AddBetaValues(betas, JsonValue.Create(configured));
        }

        foreach (var beta in prepared.BetaHeaders)
        {
            AnthropicRequestBuilder.AddBetaValues(betas, JsonValue.Create(beta));
        }

        if (betas.Count > 0)
        {
            result[BetaHeader] = string.Join(",", betas);
        }

        return result;
    }
}

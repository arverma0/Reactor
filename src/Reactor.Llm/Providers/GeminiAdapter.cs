using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Providers.Gemini;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers;

/// <summary>
/// Provider adapter for the native Gemini API (<c>POST {base}/{version}/models/{model}:generateContent</c> and
/// <c>:streamGenerateContent?alt=sse</c>, default base <c>https://generativelanguage.googleapis.com</c>, version
/// <c>v1beta</c>), authenticated with the <c>x-goog-api-key</c> header.
/// </summary>
/// <remarks>
/// <para>
/// System and developer messages become <c>systemInstruction</c>; assistant messages use the <c>model</c> role; tool
/// results become <c>functionResponse</c> parts addressed by function name (resolved from the earlier tool call), with
/// non-object content wrapped as <c>{"result": ...}</c> (or <c>{"error": ...}</c> for failures). Consecutive same-role
/// contents are merged. Gemini rarely assigns function call ids, so missing ids are synthesized as
/// <c>call_&lt;uuid&gt;</c>; ids are never sent back.
/// </para>
/// <para>
/// Thought signatures are captured into <c>ContentPart.ProviderMetadata["gemini"]["thoughtSignature"]</c> on the part
/// they arrived with and re-attached to the same part on the next request (required for Gemini 3+ function calling).
/// For Gemini 3+ models, a function call turn with no signature at all (e.g. history from another provider) gets the
/// documented <c>skip_thought_signature_validator</c> placeholder. Thought (reasoning) parts are surfaced as THINKING
/// parts but not sent back.
/// </para>
/// <para>
/// Tool parameter and response schemas are converted to Gemini's OpenAPI subset. <see cref="Request.ReasoningEffort"/>
/// maps to <c>thinkingLevel</c> (Gemini 3+) or <c>thinkingBudget</c> (Gemini 2.5) with thoughts included.
/// <c>provider_options.gemini</c>: <c>tools</c> (e.g. <c>googleSearch</c>, <c>codeExecution</c>) is appended,
/// <c>thinkingConfig</c> is merged into <c>generationConfig</c>, and every other key (<c>safetySettings</c>,
/// <c>cachedContent</c>, <c>toolConfig</c>, <c>generationConfig</c>, ...) is deep-merged into the request body.
/// </para>
/// </remarks>
public sealed class GeminiAdapter : IProviderAdapter
{
    private readonly GeminiAdapterOptions _options;
    private readonly AdapterTransport _transport;
    private readonly string _baseUrl;

    /// <summary>Creates an adapter for the native Gemini API.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public GeminiAdapter(GeminiAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _transport = new AdapterTransport(Name, options);
        var version = string.IsNullOrWhiteSpace(options.ApiVersion) ? "v1beta" : options.ApiVersion.Trim('/');
        _baseUrl = AdapterSupport.NormalizeBaseUrl(options.BaseUrl, GeminiAdapterOptions.DefaultBaseUrl) + "/" + version;
    }

    /// <inheritdoc />
    public string Name => GeminiRequestBuilder.ProviderName;

    /// <inheritdoc />
    public async Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<Warning>();
        var body = GeminiRequestBuilder.Build(request, warnings);
        var url = ModelUrl(request.Model) + ":generateContent";
        try
        {
            var (json, rateLimit) = await _transport.PostJsonAsync(url, body, Headers(streaming: false), cancellationToken).ConfigureAwait(false);
            return GeminiResponseParser.Parse(json, request, Name, warnings, rateLimit);
        }
        catch (InvalidRequestError error) when (IsInvalidApiKey(error))
        {
            throw AsAuthenticationError(error);
        }
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
        var body = GeminiRequestBuilder.Build(request, warnings);
        using var http = await OpenStreamAsync(ModelUrl(request.Model) + ":streamGenerateContent?alt=sse", body, cancellationToken)
            .ConfigureAwait(false);
        var translator = new GeminiStreamTranslator(Name, request, warnings, ProviderHttp.ParseRateLimit(http.Response.Headers));
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

    private async Task<StreamingHttpResponse> OpenStreamAsync(string url, JsonObject body, CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.PostStreamAsync(url, body, Headers(streaming: true), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidRequestError error) when (IsInvalidApiKey(error))
        {
            throw AsAuthenticationError(error);
        }
    }

    /// <summary>
    /// Gemini reports a bad API key as <c>400 INVALID_ARGUMENT</c> with reason <c>API_KEY_INVALID</c>; it is surfaced as
    /// an <see cref="AuthenticationError"/> like other providers' 401s.
    /// </summary>
    private static bool IsInvalidApiKey(InvalidRequestError error) =>
        AdapterSupport.Objects(error.Raw.Obj("error").Arr("details")).Any(d => d.Str("reason") == "API_KEY_INVALID") ||
        error.Message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase);

    private static AuthenticationError AsAuthenticationError(InvalidRequestError error) =>
        new(error.Message, error.Provider, error.StatusCode, "API_KEY_INVALID", error.Raw, error);

    /// <summary>Accepts bare ids (<c>gemini-2.5-pro</c>) and resource names (<c>models/...</c>, <c>tunedModels/...</c>).</summary>
    private string ModelUrl(string model)
    {
        var trimmed = model.Trim().Trim('/');
        var resource = trimmed.Contains('/', StringComparison.Ordinal) ? trimmed : "models/" + trimmed;
        return _baseUrl + "/" + string.Join('/', resource.Split('/').Select(Uri.EscapeDataString));
    }

    private Dictionary<string, string> Headers(bool streaming)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            headers.Add(new("x-goog-api-key", _options.ApiKey));
        }

        return AdapterSupport.BuildHeaders(_options, headers, streaming);
    }
}

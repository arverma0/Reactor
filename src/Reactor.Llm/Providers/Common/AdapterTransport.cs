using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Common;

/// <summary>
/// Owns one adapter's <see cref="HttpClient"/> and performs JSON POSTs through <see cref="ProviderHttp"/>, which enforces
/// timeouts and translates transport failures and non-2xx responses into the <see cref="SdkError"/> hierarchy.
/// Stateless per request and safe for concurrent use.
/// </summary>
internal sealed class AdapterTransport
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private int _closed;

    public AdapterTransport(string provider, ProviderAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Provider = provider;
        Timeout = options.Timeout ?? AdapterTimeout.Default;
        _ownsHttpClient = options.HttpClient is null;
        _httpClient = ProviderHttp.CreateHttpClient(options);
    }

    public string Provider { get; }

    public AdapterTimeout Timeout { get; }

    /// <summary>Sends a non-streaming request and returns the JSON object body plus parsed rate-limit headers.</summary>
    public async Task<(JsonObject Body, RateLimitInfo? RateLimit)> PostJsonAsync(
        string url,
        JsonObject body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var request = ProviderHttp.CreateJsonPost(url, body, headers);
        using var response = await ProviderHttp.SendAsync(_httpClient, request, Provider, Timeout, streaming: false, cancellationToken)
            .ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, Provider, cancellationToken).ConfigureAwait(false);
        if (json is not JsonObject obj)
        {
            throw new ProviderError(
                $"{Provider} returned an unexpected response body (expected a JSON object).",
                Provider,
                (int)response.StatusCode,
                raw: json);
        }

        return (obj, ProviderHttp.ParseRateLimit(response.Headers));
    }

    /// <summary>
    /// Sends a streaming request and returns once response headers arrive. Non-2xx responses throw here, before any
    /// stream event is produced, so callers can retry the connection.
    /// </summary>
    public async Task<StreamingHttpResponse> PostStreamAsync(
        string url,
        JsonObject body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var request = ProviderHttp.CreateJsonPost(url, body, headers);
        try
        {
            var response = await ProviderHttp.SendAsync(_httpClient, request, Provider, Timeout, streaming: true, cancellationToken)
                .ConfigureAwait(false);
            return new StreamingHttpResponse(request, response);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>Disposes the HTTP client only when this transport created it; a caller-supplied client is never disposed.</summary>
    public ValueTask CloseAsync()
    {
        if (_ownsHttpClient && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>A streaming HTTP response together with the request that produced it; both are released on dispose.</summary>
internal sealed class StreamingHttpResponse(HttpRequestMessage request, HttpResponseMessage response) : IDisposable
{
    public HttpResponseMessage Response => response;

    public void Dispose()
    {
        response.Dispose();
        request.Dispose();
    }
}

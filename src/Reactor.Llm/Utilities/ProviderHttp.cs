using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Llm.Utilities;

/// <summary>
/// Shared HTTP plumbing for provider adapters: client creation, timeouts, exception translation,
/// error-body parsing, <c>Retry-After</c> handling, and rate-limit header parsing.
/// </summary>
public static class ProviderHttp
{
    /// <summary>Returns the caller-supplied client, or creates one honoring the connect timeout.</summary>
    public static HttpClient CreateHttpClient(ProviderAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.HttpClient is not null)
        {
            return options.HttpClient;
        }

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = options.Timeout.Connect,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Creates a JSON POST request.</summary>
    public static HttpRequestMessage CreateJsonPost(string url, JsonNode body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    /// <summary>
    /// Sends <paramref name="request"/>, enforcing <see cref="AdapterTimeout.Request"/> (whole cycle when not streaming,
    /// until headers when streaming) and translating failures into the error hierarchy. Non-2xx responses throw.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        string provider,
        AdapterTimeout timeout,
        bool streaming,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeout);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.Request > TimeSpan.Zero && timeout.Request != System.Threading.Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(timeout.Request);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new AbortError($"{provider} request was cancelled.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new RequestTimeoutError(
                $"{provider} request timed out after {timeout.Request.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkError($"{provider} network error: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await CreateErrorAsync(response, provider, cancellationToken).ConfigureAwait(false);
            }
        }

        return response;
    }

    /// <summary>Reads a successful response body as JSON, translating parse failures.</summary>
    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new AbortError($"{provider} request was cancelled.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new NetworkError($"{provider} network error while reading the response: {ex.Message}", ex);
        }

        try
        {
            return JsonNode.Parse(text) ?? throw new ProviderError($"{provider} returned an empty response body.", provider, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            throw new ProviderError($"{provider} returned invalid JSON: {Truncate(text, 500)}", provider, (int)response.StatusCode, cause: ex);
        }
    }

    /// <summary>Builds the error for a non-success response (spec 7.6).</summary>
    public static async Task<SdkError> CreateErrorAsync(HttpResponseMessage response, string provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // Body is best-effort; fall back to the status line.
        }

        var raw = JsonHelpers.ParseOrNull(body);
        var errorNode = raw switch
        {
            JsonArray { Count: > 0 } arr => arr[0]?["error"] ?? arr[0],
            JsonObject obj => obj["error"] ?? obj,
            _ => null,
        };

        string? message = null;
        string? code = null;
        string? grpcStatus = null;
        double? retryAfter = ParseRetryAfter(response.Headers);

        if (errorNode is JsonObject eo)
        {
            message = eo.Str("message");
            code = eo.Str("code") ?? eo.Str("type");
            if (code is null && eo["code"] is JsonValue numericCode && numericCode.GetValueKind() == JsonValueKind.Number)
            {
                code = numericCode.ToJsonString();
            }

            grpcStatus = eo.Str("status");
            retryAfter ??= ParseGoogleRetryDelay(eo.Arr("details"));
        }
        else if (errorNode is JsonValue ev && ev.GetValueKind() == JsonValueKind.String)
        {
            message = ev.GetValue<string>();
        }

        message ??= raw.Str("message");
        if (string.IsNullOrWhiteSpace(message))
        {
            message = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}" : Truncate(body.Trim(), 2000);
        }

        var status = (int)response.StatusCode;
        return grpcStatus is not null && !int.TryParse(grpcStatus, out _)
            ? ErrorMapper.FromGrpcStatus(grpcStatus, message, provider, status, raw, retryAfter)
            : ErrorMapper.FromHttpStatus(status, message, provider, code, raw, retryAfter);
    }

    /// <summary>Parses <c>retry-after-ms</c> or <c>Retry-After</c> (seconds or HTTP date) into seconds.</summary>
    public static double? ParseRetryAfter(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.TryGetValues("retry-after-ms", out var msValues) &&
            double.TryParse(msValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return ms / 1000.0;
        }

        if (headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return delta.TotalSeconds;
            }

            if (retryAfter.Date is { } date)
            {
                return Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds);
            }
        }

        if (headers.TryGetValues("retry-after", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        return null;
    }

    /// <summary>
    /// Parses rate-limit headers (OpenAI <c>x-ratelimit-*</c> and Anthropic <c>anthropic-ratelimit-*</c>). Null when absent.
    /// </summary>
    public static RateLimitInfo? ParseRateLimit(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        int? GetInt(params string[] names)
        {
            foreach (var name in names)
            {
                if (headers.TryGetValues(name, out var values) &&
                    int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    return v;
                }
            }

            return null;
        }

        string? GetString(params string[] names)
        {
            foreach (var name in names)
            {
                if (headers.TryGetValues(name, out var values))
                {
                    return values.FirstOrDefault();
                }
            }

            return null;
        }

        var info = new RateLimitInfo
        {
            RequestsRemaining = GetInt("x-ratelimit-remaining-requests", "anthropic-ratelimit-requests-remaining"),
            RequestsLimit = GetInt("x-ratelimit-limit-requests", "anthropic-ratelimit-requests-limit"),
            TokensRemaining = GetInt("x-ratelimit-remaining-tokens", "anthropic-ratelimit-tokens-remaining"),
            TokensLimit = GetInt("x-ratelimit-limit-tokens", "anthropic-ratelimit-tokens-limit"),
            ResetAt = ParseReset(GetString("x-ratelimit-reset-requests", "anthropic-ratelimit-requests-reset", "x-ratelimit-reset-tokens", "anthropic-ratelimit-tokens-reset")),
        };

        return info.RequestsRemaining is null && info.RequestsLimit is null && info.TokensRemaining is null &&
               info.TokensLimit is null && info.ResetAt is null
            ? null
            : info;
    }

    /// <summary>Parses Go-style durations such as <c>6m0s</c>, <c>1.5s</c>, <c>20ms</c>.</summary>
    public static TimeSpan? ParseGoDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var total = TimeSpan.Zero;
        var i = 0;
        var matched = false;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
            {
                i++;
            }

            if (start == i ||
                !double.TryParse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            var unitStart = i;
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }

            var unit = text[unitStart..i];
            total += unit switch
            {
                "h" => TimeSpan.FromHours(number),
                "m" => TimeSpan.FromMinutes(number),
                "s" or "" => TimeSpan.FromSeconds(number),
                "ms" => TimeSpan.FromMilliseconds(number),
                "us" or "µs" => TimeSpan.FromMicroseconds(number),
                "ns" => TimeSpan.FromTicks((long)(number / 100)),
                _ => TimeSpan.MinValue,
            };
            if (total < TimeSpan.Zero)
            {
                return null;
            }

            matched = true;
        }

        return matched ? total : null;
    }

    private static DateTimeOffset? ParseReset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return date;
        }

        return ParseGoDuration(value) is { } duration ? DateTimeOffset.UtcNow + duration : null;
    }

    private static double? ParseGoogleRetryDelay(JsonArray? details)
    {
        if (details is null)
        {
            return null;
        }

        foreach (var detail in details)
        {
            if (detail.Str("retryDelay") is { } delay && ParseGoDuration(delay) is { } span)
            {
                return span.TotalSeconds;
            }
        }

        return null;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Common;

/// <summary>One step of reading an SSE body: either an event or the terminal error that ended the stream.</summary>
internal readonly record struct SseRead(SseEvent? Event, SdkError? Error);

/// <summary>Reads SSE events from a streaming response, translating mid-stream failures into terminal errors.</summary>
internal static class SseStreams
{
    /// <summary>
    /// Yields parsed SSE events. Cancellation throws <see cref="AbortError"/>. A read timeout yields a final
    /// <see cref="RequestTimeoutError"/>; IO failures yield a final <see cref="StreamError"/>. Both end the sequence.
    /// </summary>
    public static async IAsyncEnumerable<SseRead> ReadAsync(
        HttpResponseMessage response,
        string provider,
        TimeSpan readTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A 2xx response that is not an event stream usually means a wrong base URL or an intercepting proxy; say so
        // instead of reporting a truncated stream.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var snippet = await ReadSnippetAsync(response, cancellationToken).ConfigureAwait(false);
            yield return new SseRead(null, new StreamError(
                $"{provider} returned '{mediaType}' instead of an event stream (HTTP {(int)response.StatusCode}); check the base URL and any proxy." +
                (snippet.Length > 0 ? $" Response: {snippet}" : "")));
            yield break;
        }

        Stream? stream = null;
        SdkError? failure = null;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new AbortError($"{provider} stream was cancelled.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            failure = new StreamError($"{provider} stream could not be read: {ex.Message}", ex);
        }

        if (stream is null)
        {
            yield return new SseRead(null, failure ?? new StreamError($"{provider} stream could not be read."));
            yield break;
        }

        await using (stream.ConfigureAwait(false))
        {
            var enumerator = SseParser.ParseAsync(stream, readTimeout, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    SseEvent? current = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new AbortError($"{provider} stream was cancelled.", ex);
                    }
                    catch (RequestTimeoutError ex)
                    {
                        failure = ex;
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
                    {
                        failure = new StreamError($"{provider} stream failed: {ex.Message}", ex);
                    }

                    if (failure is not null)
                    {
                        yield return new SseRead(null, failure);
                        yield break;
                    }

                    yield return new SseRead(current, null);
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads up to 1 KB of a response body for diagnostics; never throws.</summary>
    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            text = text.ReplaceLineEndings(" ").Trim();
            return text.Length <= 1024 ? text : text[..1024] + "…";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return "";
        }
    }
}

/// <summary>Maps error payloads delivered inside a 2xx response (stream error events) onto the error hierarchy.</summary>
internal static class ProviderErrors
{
    /// <summary>
    /// Creates the error for a provider error object such as <c>{"type": "overloaded_error", "message": "..."}</c> or
    /// <c>{"code": "rate_limit_exceeded", "message": "..."}</c>. Streamed errors carry no HTTP status, so the error type's
    /// documented HTTP-equivalent status is used for classification; unknown types become a retryable
    /// <see cref="ProviderError"/>.
    /// </summary>
    public static SdkError FromPayload(string provider, JsonNode? error, JsonNode? raw)
    {
        var type = error.Str("type");
        var code = error.Str("code") ?? (error is JsonObject eo && eo["code"] is JsonValue cv && cv.GetValueKind() == JsonValueKind.Number ? cv.ToJsonString() : null);
        var message = error.Str("message") ??
                      (error is JsonValue ev && ev.GetValueKind() == JsonValueKind.String ? ev.GetValue<string>() : null) ??
                      $"{provider} reported an error during streaming.";

        if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            if (numeric is >= 400 and <= 599)
            {
                return ErrorMapper.FromHttpStatus(numeric, message, provider, type ?? code, raw);
            }

            code = null;
        }

        var classification = code ?? type;
        if ((StatusFor(classification) ?? StatusFor(type)) is { } status)
        {
            return ErrorMapper.FromHttpStatus(status, message, provider, classification, raw);
        }

        return new ProviderError(message, provider, statusCode: null, errorCode: classification, retryable: true, raw: raw);
    }

    private static int? StatusFor(string? code) => code?.ToLowerInvariant() switch
    {
        null => null,
        "invalid_request_error" or "invalid_request" or "invalid_prompt" or "bad_request" or "invalid_value" or
            "invalid_image" or "invalid_image_format" or "invalid_base64_image" or "invalid_image_url" or "image_too_large" or
            "image_too_small" or "image_parse_error" or "unsupported_image_media_type" or "empty_image_file" or
            "image_content_policy_violation" or "context_length_exceeded" or "string_above_max_length" => 400,
        "authentication_error" or "invalid_api_key" or "unauthorized" or "unauthenticated" => 401,
        "permission_error" or "permission_denied" or "forbidden" => 403,
        "not_found_error" or "not_found" or "model_not_found" => 404,
        "timeout" or "timeout_error" or "request_timeout" => 408,
        "request_too_large" => 413,
        "rate_limit_error" or "rate_limit_exceeded" or "rate_limited" or "too_many_requests" or "insufficient_quota" => 429,
        "overloaded_error" or "overloaded" => 529,
        "api_error" or "server_error" or "internal_error" or "internal_server_error" or "service_unavailable" or
            "vector_store_timeout" => 500,
        _ => null,
    };
}

/// <summary>
/// Base for per-request stream translators: converts provider SSE events into unified <see cref="StreamEvent"/>s,
/// guaranteeing STREAM_START first and exactly one terminal FINISH or ERROR event.
/// </summary>
internal abstract class StreamTranslatorBase(string provider, Request request, IReadOnlyList<Warning> warnings, RateLimitInfo? rateLimit)
{
    protected string Provider { get; } = provider;

    protected Request Request { get; } = request;

    protected IReadOnlyList<Warning> Warnings { get; } = warnings;

    protected RateLimitInfo? RateLimit { get; } = rateLimit;

    protected bool IsStarted { get; private set; }

    /// <summary>True once a terminal FINISH or ERROR event has been produced.</summary>
    public bool IsFinished { get; protected set; }

    /// <summary>Translates one SSE event.</summary>
    public abstract List<StreamEvent> Process(SseEvent sse);

    /// <summary>Called when the SSE body ends; produces the terminal event if none was produced yet.</summary>
    public abstract List<StreamEvent> Complete();

    /// <summary>Produces the terminal ERROR event for a transport failure.</summary>
    public List<StreamEvent> Fail(SdkError error)
    {
        var events = new List<StreamEvent>();
        if (IsFinished)
        {
            return events;
        }

        EnsureStarted(events, null);
        events.Add(ErrorEvent(error, null));
        IsFinished = true;
        return events;
    }

    protected void EnsureStarted(List<StreamEvent> events, JsonNode? raw)
    {
        if (!IsStarted)
        {
            IsStarted = true;
            events.Add(new StreamEvent { Type = StreamEventType.StreamStart, Warnings = Warnings, Raw = raw });
        }
    }

    /// <summary>Ends the stream with an error because the body ended before the provider's terminal event.</summary>
    protected List<StreamEvent> UnexpectedEnd(string terminalEvent)
    {
        var events = new List<StreamEvent>();
        if (IsFinished)
        {
            return events;
        }

        EnsureStarted(events, null);
        events.Add(ErrorEvent(new StreamError($"{Provider} stream ended before {terminalEvent} was received."), null));
        IsFinished = true;
        return events;
    }

    protected static JsonObject? ParseObject(string data) => JsonHelpers.ParseOrNull(data) as JsonObject;

    protected static StreamEvent TextStart(string textId, JsonNode? raw) =>
        new() { Type = StreamEventType.TextStart, TextId = textId, Raw = raw };

    protected static StreamEvent TextDelta(string textId, string delta, JsonNode? raw) =>
        new() { Type = StreamEventType.TextDelta, TextId = textId, Delta = delta, Raw = raw };

    protected static StreamEvent TextEnd(string textId, JsonNode? raw) =>
        new() { Type = StreamEventType.TextEnd, TextId = textId, Raw = raw };

    protected static StreamEvent ReasoningStart(string id, JsonNode? raw) =>
        new() { Type = StreamEventType.ReasoningStart, TextId = id, Raw = raw };

    protected static StreamEvent ReasoningDelta(string id, string delta, JsonNode? raw) =>
        new() { Type = StreamEventType.ReasoningDelta, TextId = id, ReasoningDelta = delta, Raw = raw };

    protected static StreamEvent ReasoningEnd(string id, JsonNode? raw) =>
        new() { Type = StreamEventType.ReasoningEnd, TextId = id, Raw = raw };

    protected static StreamEvent ToolCallStart(string id, string name, JsonNode? raw) =>
        new() { Type = StreamEventType.ToolCallStart, ToolCall = new ToolCall(id, name, new JsonObject()), Raw = raw };

    protected static StreamEvent ToolCallDelta(string id, string name, string delta, JsonNode? raw) =>
        new() { Type = StreamEventType.ToolCallDelta, ToolCall = new ToolCall(id, name, new JsonObject()), Delta = delta, Raw = raw };

    protected static StreamEvent ToolCallEnd(string id, string name, string rawArguments, JsonNode? raw) =>
        new() { Type = StreamEventType.ToolCallEnd, ToolCall = AdapterSupport.CompletedToolCall(id, name, rawArguments), Raw = raw };

    protected static StreamEvent ProviderEvent(JsonNode? raw) =>
        new() { Type = StreamEventType.ProviderEvent, Raw = raw };

    protected static StreamEvent ErrorEvent(SdkError error, JsonNode? raw) =>
        new() { Type = StreamEventType.Error, Error = error, Raw = raw };

    protected static StreamEvent FinishEvent(Response response, JsonNode? raw) => new()
    {
        Type = StreamEventType.Finish,
        FinishReason = response.FinishReason,
        Usage = response.Usage,
        Response = response,
        Raw = raw,
    };
}

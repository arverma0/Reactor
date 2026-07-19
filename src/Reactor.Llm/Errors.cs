using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>Base type for all Reactor.Llm errors. Every error carries a <see cref="Retryable"/> classification.</summary>
public class SdkError : Exception
{
    public SdkError(string message, Exception? cause = null)
        : base(message, cause)
    {
    }

    /// <summary>Whether the operation is safe to retry. Unknown errors default to retryable.</summary>
    public virtual bool Retryable => true;
}

/// <summary>An error returned by an LLM provider.</summary>
public class ProviderError : SdkError
{
    private readonly bool _retryable;

    public ProviderError(
        string message,
        string? provider,
        int? statusCode = null,
        string? errorCode = null,
        bool retryable = true,
        double? retryAfter = null,
        JsonNode? raw = null,
        Exception? cause = null)
        : base(message, cause)
    {
        Provider = provider ?? "";
        StatusCode = statusCode;
        ErrorCode = errorCode;
        _retryable = retryable;
        RetryAfter = retryAfter;
        Raw = raw;
    }

    /// <summary>Which provider returned the error.</summary>
    public string Provider { get; }

    public int? StatusCode { get; }

    /// <summary>Provider-specific error code.</summary>
    public string? ErrorCode { get; }

    public override bool Retryable => _retryable;

    /// <summary>Seconds to wait before retrying, from the <c>Retry-After</c> header.</summary>
    public double? RetryAfter { get; }

    /// <summary>Raw error response body.</summary>
    public JsonNode? Raw { get; }
}

/// <summary>401: invalid API key, expired token.</summary>
public sealed class AuthenticationError(string message, string? provider, int? statusCode = 401, string? errorCode = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: false, retryAfter: null, raw, cause);

/// <summary>403: insufficient permissions.</summary>
public sealed class AccessDeniedError(string message, string? provider, int? statusCode = 403, string? errorCode = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: false, retryAfter: null, raw, cause);

/// <summary>404: model or endpoint not found.</summary>
public sealed class NotFoundError(string message, string? provider, int? statusCode = 404, string? errorCode = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: false, retryAfter: null, raw, cause);

/// <summary>400/422: malformed request or invalid parameters.</summary>
public sealed class InvalidRequestError(string message, string? provider, int? statusCode = 400, string? errorCode = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: false, retryAfter: null, raw, cause);

/// <summary>429: rate limit exceeded. Retryable.</summary>
public sealed class RateLimitError(string message, string? provider, int? statusCode = 429, string? errorCode = null, double? retryAfter = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: true, retryAfter, raw, cause);

/// <summary>5xx: provider internal error. Retryable.</summary>
public sealed class ServerError(string message, string? provider, int? statusCode = 500, string? errorCode = null, double? retryAfter = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: true, retryAfter, raw, cause);

/// <summary>Response blocked by a safety/content filter.</summary>
public sealed class ContentFilterError(string message, string? provider, int? statusCode = null, string? errorCode = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: false, retryAfter: null, raw, cause);

/// <summary>Input plus output exceeds the context window.</summary>
public sealed class ContextLengthError(string message, string? provider, int? statusCode = 413, string? errorCode = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: false, retryAfter: null, raw, cause);

/// <summary>Billing or usage quota exhausted.</summary>
public sealed class QuotaExceededError(string message, string? provider, int? statusCode = null, string? errorCode = null, JsonNode? raw = null, Exception? cause = null)
    : ProviderError(message, provider, statusCode, errorCode, retryable: false, retryAfter: null, raw, cause);

/// <summary>The request or stream timed out. Not retried by default.</summary>
public sealed class RequestTimeoutError(string message, Exception? cause = null) : SdkError(message, cause)
{
    public override bool Retryable => false;
}

/// <summary>The request was cancelled via its cancellation token (abort signal).</summary>
public sealed class AbortError(string message = "The operation was aborted.", Exception? cause = null) : SdkError(message, cause)
{
    public override bool Retryable => false;
}

/// <summary>A network-level failure. Retryable.</summary>
public sealed class NetworkError(string message, Exception? cause = null) : SdkError(message, cause)
{
    public override bool Retryable => true;
}

/// <summary>An error during stream consumption. Retryable.</summary>
public sealed class StreamError(string message, Exception? cause = null) : SdkError(message, cause)
{
    public override bool Retryable => true;
}

/// <summary>Tool call arguments failed validation.</summary>
public sealed class InvalidToolCallError(string message, Exception? cause = null) : SdkError(message, cause)
{
    public override bool Retryable => false;
}

/// <summary>The provider does not support the requested tool choice mode.</summary>
public sealed class UnsupportedToolChoiceError(string message, Exception? cause = null) : SdkError(message, cause)
{
    public override bool Retryable => false;
}

/// <summary>Structured output parsing or validation failed.</summary>
public sealed class NoObjectGeneratedError(string message, string? text = null, Exception? cause = null) : SdkError(message, cause)
{
    /// <summary>The raw model text that failed to parse/validate.</summary>
    public string? Text { get; } = text;

    public override bool Retryable => false;
}

/// <summary>SDK misconfiguration, e.g. a missing provider.</summary>
public sealed class ConfigurationError(string message, Exception? cause = null) : SdkError(message, cause)
{
    public override bool Retryable => false;
}

/// <summary>Maps HTTP status codes, gRPC status names, and error messages onto the error hierarchy.</summary>
public static class ErrorMapper
{
    /// <summary>Creates the appropriate error for an HTTP error response (spec section 6.4 and 6.5).</summary>
    public static SdkError FromHttpStatus(
        int statusCode,
        string message,
        string? provider,
        string? errorCode = null,
        JsonNode? raw = null,
        double? retryAfter = null)
    {
        message = string.IsNullOrWhiteSpace(message) ? $"HTTP {statusCode}" : message;
        SdkError byStatus = statusCode switch
        {
            400 or 422 => new InvalidRequestError(message, provider, statusCode, errorCode, raw),
            401 => new AuthenticationError(message, provider, statusCode, errorCode, raw),
            403 => new AccessDeniedError(message, provider, statusCode, errorCode, raw),
            404 => new NotFoundError(message, provider, statusCode, errorCode, raw),
            408 => new RequestTimeoutError(Prefix(provider, message)),
            413 => new ContextLengthError(message, provider, statusCode, errorCode, raw),
            429 => new RateLimitError(message, provider, statusCode, errorCode, retryAfter, raw),
            >= 500 and <= 599 => new ServerError(message, provider, statusCode, errorCode, retryAfter, raw),
            _ => new ProviderError(message, provider, statusCode, errorCode, retryable: true, retryAfter, raw),
        };

        return Refine(byStatus, message, provider, statusCode, errorCode, raw, retryAfter);
    }

    /// <summary>Creates the appropriate error for a gRPC-style status name (Gemini).</summary>
    public static SdkError FromGrpcStatus(
        string grpcStatus,
        string message,
        string? provider,
        int? httpStatus = null,
        JsonNode? raw = null,
        double? retryAfter = null)
    {
        message = string.IsNullOrWhiteSpace(message) ? grpcStatus : message;
        SdkError byStatus = grpcStatus.ToUpperInvariant() switch
        {
            "NOT_FOUND" => new NotFoundError(message, provider, httpStatus ?? 404, grpcStatus, raw),
            "INVALID_ARGUMENT" or "FAILED_PRECONDITION" or "OUT_OF_RANGE" => new InvalidRequestError(message, provider, httpStatus ?? 400, grpcStatus, raw),
            "UNAUTHENTICATED" => new AuthenticationError(message, provider, httpStatus ?? 401, grpcStatus, raw),
            "PERMISSION_DENIED" => new AccessDeniedError(message, provider, httpStatus ?? 403, grpcStatus, raw),
            "RESOURCE_EXHAUSTED" => new RateLimitError(message, provider, httpStatus ?? 429, grpcStatus, retryAfter, raw),
            "UNAVAILABLE" or "INTERNAL" or "UNKNOWN" or "ABORTED" or "DATA_LOSS" => new ServerError(message, provider, httpStatus ?? 503, grpcStatus, retryAfter, raw),
            "DEADLINE_EXCEEDED" => new RequestTimeoutError(Prefix(provider, message)),
            "CANCELLED" => new AbortError(Prefix(provider, message)),
            _ when httpStatus is int code => FromHttpStatus(code, message, provider, grpcStatus, raw, retryAfter),
            _ => new ProviderError(message, provider, httpStatus, grpcStatus, retryable: true, retryAfter, raw),
        };

        return Refine(byStatus, message, provider, httpStatus, grpcStatus, raw, retryAfter);
    }

    /// <summary>
    /// Message-based classification for ambiguous cases where the status code alone is insufficient
    /// (spec 6.5). Only generic classifications are refined; specific ones are kept.
    /// </summary>
    private static SdkError Refine(SdkError error, string message, string? provider, int? statusCode, string? errorCode, JsonNode? raw, double? retryAfter)
    {
        var text = (message + " " + errorCode).ToLowerInvariant();

        if (error is RateLimitError or InvalidRequestError or ProviderError { StatusCode: 403 or 402 } &&
            (text.Contains("insufficient_quota", StringComparison.Ordinal) || text.Contains("credit balance", StringComparison.Ordinal) ||
             text.Contains("billing_hard_limit", StringComparison.Ordinal)))
        {
            return new QuotaExceededError(message, provider, statusCode, errorCode, raw);
        }

        var generic = error is InvalidRequestError || error.GetType() == typeof(ProviderError);
        if (!generic)
        {
            return error;
        }

        if (text.Contains("context length", StringComparison.Ordinal) || text.Contains("context_length", StringComparison.Ordinal) ||
            text.Contains("too many tokens", StringComparison.Ordinal) || text.Contains("maximum context", StringComparison.Ordinal) ||
            text.Contains("prompt is too long", StringComparison.Ordinal) || text.Contains("context window", StringComparison.Ordinal) ||
            text.Contains("input is too long", StringComparison.Ordinal))
        {
            return new ContextLengthError(message, provider, statusCode, errorCode, raw);
        }

        if (text.Contains("content filter", StringComparison.Ordinal) || text.Contains("content_filter", StringComparison.Ordinal) ||
            text.Contains("content_policy", StringComparison.Ordinal) || text.Contains("safety", StringComparison.Ordinal))
        {
            return new ContentFilterError(message, provider, statusCode, errorCode, raw);
        }

        if (error.GetType() == typeof(ProviderError))
        {
            if (text.Contains("not found", StringComparison.Ordinal) || text.Contains("does not exist", StringComparison.Ordinal))
            {
                return new NotFoundError(message, provider, statusCode, errorCode, raw);
            }

            if (text.Contains("unauthorized", StringComparison.Ordinal) || text.Contains("invalid key", StringComparison.Ordinal) ||
                text.Contains("invalid api key", StringComparison.Ordinal) || text.Contains("invalid_api_key", StringComparison.Ordinal))
            {
                return new AuthenticationError(message, provider, statusCode, errorCode, raw);
            }
        }

        _ = retryAfter;
        return error;
    }

    private static string Prefix(string? provider, string message) =>
        string.IsNullOrEmpty(provider) ? message : $"{provider}: {message}";
}

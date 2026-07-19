using System.Reflection;
using Reactor.Graphs;

namespace Reactor.Execution;

/// <summary>Delay calculation between node retries (spec 3.6).</summary>
public sealed record BackoffConfig
{
    /// <summary>First retry delay in milliseconds (default 200).</summary>
    public int InitialDelayMs { get; init; } = 200;

    /// <summary>Multiplier for subsequent delays (default 2.0).</summary>
    public double BackoffFactor { get; init; } = 2.0;

    /// <summary>Cap on delay in milliseconds (default 60000).</summary>
    public int MaxDelayMs { get; init; } = 60_000;

    /// <summary>Multiply delays by a random factor in [0.5, 1.5) (default true).</summary>
    public bool Jitter { get; init; } = true;

    public static BackoffConfig Default { get; } = new();

    /// <summary>No waiting between attempts (useful for tests).</summary>
    public static BackoffConfig Immediate { get; } = new() { InitialDelayMs = 0, Jitter = false };

    /// <summary>Delay before retry <paramref name="attempt"/> (1-indexed: the first retry is attempt 1).</summary>
    public TimeSpan DelayForAttempt(int attempt)
    {
        var delay = InitialDelayMs * Math.Pow(BackoffFactor, Math.Max(0, attempt - 1));
        delay = Math.Min(delay, MaxDelayMs);
        if (Jitter)
        {
            delay *= 0.5 + Random.Shared.NextDouble();
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, delay));
    }
}

/// <summary>Per-node retry policy: attempts, backoff, and the retryable-error predicate.</summary>
public sealed record NodeRetryPolicy
{
    /// <summary>Total executions allowed (minimum 1; 1 = no retries).</summary>
    public int MaxAttempts { get; init; } = 1;

    public BackoffConfig Backoff { get; init; } = BackoffConfig.Default;

    /// <summary>Whether an exception thrown by a handler should be retried.</summary>
    public Func<Exception, bool> ShouldRetry { get; init; } = RetryClassifier.IsRetryable;

    /// <summary>Named presets (spec 3.6): none, standard, aggressive, linear, patient.</summary>
    public static IReadOnlyDictionary<string, NodeRetryPolicy> Presets { get; } = new Dictionary<string, NodeRetryPolicy>(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = new() { MaxAttempts = 1 },
        ["standard"] = new() { MaxAttempts = 5, Backoff = new BackoffConfig { InitialDelayMs = 200, BackoffFactor = 2.0 } },
        ["aggressive"] = new() { MaxAttempts = 5, Backoff = new BackoffConfig { InitialDelayMs = 500, BackoffFactor = 2.0 } },
        ["linear"] = new() { MaxAttempts = 3, Backoff = new BackoffConfig { InitialDelayMs = 500, BackoffFactor = 1.0 } },
        ["patient"] = new() { MaxAttempts = 3, Backoff = new BackoffConfig { InitialDelayMs = 2000, BackoffFactor = 3.0 } },
    };

    /// <summary>
    /// Builds the policy for a node: an optional <c>retry_policy</c> preset supplies backoff (and default attempts);
    /// <c>max_retries</c> on the node, else the graph's <c>default_max_retries</c>, sets the attempt count
    /// (<c>max_attempts = max_retries + 1</c>). <paramref name="backoffOverride"/> replaces the backoff (e.g. for tests).
    /// </summary>
    public static NodeRetryPolicy For(Node node, Graph graph, BackoffConfig? backoffOverride = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        var policy = node[AttrKeys.RetryPolicy] is { } presetName && Presets.TryGetValue(presetName, out var preset)
            ? preset
            : new NodeRetryPolicy { MaxAttempts = 1 };

        var maxRetries = node.MaxRetries ?? (node[AttrKeys.RetryPolicy] is not null && policy.MaxAttempts > 1 ? policy.MaxAttempts - 1 : graph.DefaultMaxRetries);
        policy = policy with { MaxAttempts = Math.Max(1, maxRetries + 1) };
        if (backoffOverride is not null)
        {
            policy = policy with { Backoff = backoffOverride };
        }

        return policy;
    }
}

/// <summary>
/// Default retryable-error classification (spec 3.6 and Appendix D): retries network errors, timeouts, rate limits and
/// server errors; does not retry authentication, bad-request, validation, or configuration errors. Exceptions exposing
/// a public boolean <c>Retryable</c> property (such as the Reactor.Llm error hierarchy) are classified by that property.
/// </summary>
public static class RetryClassifier
{
    public static bool IsRetryable(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var property = error.GetType().GetProperty("Retryable", BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType == typeof(bool) && property.GetValue(error) is bool retryable)
        {
            return retryable;
        }

        return error switch
        {
            TimeoutException or IOException or HttpRequestException => true,
            OperationCanceledException => false,
            ArgumentException or InvalidOperationException or NotSupportedException or FormatException or UnauthorizedAccessException => false,
            AggregateException aggregate => aggregate.InnerExceptions.All(IsRetryable),
            _ => error.InnerException is { } inner && IsRetryable(inner),
        };
    }
}

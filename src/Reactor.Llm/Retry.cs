namespace Reactor.Llm;

/// <summary>Retry policy for individual LLM calls: exponential backoff with optional jitter and Retry-After support.</summary>
public sealed record RetryPolicy
{
    /// <summary>Retry attempts not counting the initial call. 0 disables retries.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Initial delay in seconds.</summary>
    public double BaseDelay { get; init; } = 1.0;

    /// <summary>Maximum delay between retries in seconds. A provider Retry-After above this is not waited for.</summary>
    public double MaxDelay { get; init; } = 60.0;

    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>When true, each delay is multiplied by a random factor in [0.5, 1.5).</summary>
    public bool Jitter { get; init; } = true;

    /// <summary>Called before each retry with (error, retry number starting at 1, delay).</summary>
    public Action<Exception, int, TimeSpan>? OnRetry { get; init; }

    /// <summary>Overrides the retryability predicate (e.g. to opt in to retrying timeouts).</summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    public static RetryPolicy Default { get; } = new();

    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>The backoff delay for 0-indexed <paramref name="attempt"/>.</summary>
    public TimeSpan CalculateDelay(int attempt)
    {
        var delay = Math.Min(BaseDelay * Math.Pow(BackoffMultiplier, Math.Max(0, attempt)), MaxDelay);
        if (Jitter)
        {
            delay *= 0.5 + Random.Shared.NextDouble();
        }

        return TimeSpan.FromSeconds(Math.Max(0, delay));
    }

    public bool IsRetryable(Exception error) => ShouldRetry?.Invoke(error) ?? DefaultShouldRetry(error);

    /// <summary>
    /// Default classification: SDK errors use their <see cref="SdkError.Retryable"/> flag (timeouts and aborts are not retried);
    /// raw network/IO exceptions are retried; everything else is not.
    /// </summary>
    public static bool DefaultShouldRetry(Exception error) => error switch
    {
        AbortError or RequestTimeoutError => false,
        OperationCanceledException => false,
        SdkError sdk => sdk.Retryable,
        HttpRequestException or IOException => true,
        _ => false,
    };
}

/// <summary>Standalone retry utility for composing retry behavior around low-level calls.</summary>
public static class Retry
{
    /// <summary>
    /// Runs <paramref name="operation"/>, retrying retryable failures per <paramref name="policy"/>.
    /// When an error carries a Retry-After within <see cref="RetryPolicy.MaxDelay"/>, that delay is used instead of backoff;
    /// when it exceeds the maximum, the error is raised immediately.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        policy ??= RetryPolicy.Default;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not AbortError)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new AbortError(cause: error);
                }

                if (attempt >= policy.MaxRetries || !policy.IsRetryable(error))
                {
                    throw;
                }

                if (!TryGetRetryDelay(policy, error, attempt, out var delay))
                {
                    throw;
                }

                policy.OnRetry?.Invoke(error, attempt + 1, delay);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    throw new AbortError(cause: ex);
                }
            }
        }
    }

    /// <summary>
    /// Computes the delay before retry number <paramref name="attempt"/> + 1, honoring a provider Retry-After.
    /// Returns false when the provider asked for a wait longer than <see cref="RetryPolicy.MaxDelay"/>.
    /// </summary>
    internal static bool TryGetRetryDelay(RetryPolicy policy, Exception error, int attempt, out TimeSpan delay)
    {
        delay = policy.CalculateDelay(attempt);
        if (error is ProviderError { RetryAfter: double retryAfter })
        {
            if (retryAfter > policy.MaxDelay)
            {
                return false;
            }

            delay = TimeSpan.FromSeconds(Math.Max(0, retryAfter));
        }

        return true;
    }

    /// <summary>Runs an operation without a result, retrying per <paramref name="policy"/>.</summary>
    public static Task ExecuteAsync(Func<CancellationToken, Task> operation, RetryPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<bool>(async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return true;
        }, policy, cancellationToken);
    }
}

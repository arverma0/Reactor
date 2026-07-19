using System.Runtime.CompilerServices;

namespace Reactor.Llm;

/// <summary>Continuation for a non-streaming call.</summary>
public delegate Task<Response> CompleteNext(Request request, CancellationToken cancellationToken);

/// <summary>Continuation for a streaming call.</summary>
public delegate IAsyncEnumerable<StreamEvent> StreamNext(Request request, CancellationToken cancellationToken);

/// <summary>
/// Middleware wraps provider calls (onion model): it runs in registration order for the request phase and in reverse
/// order for the response phase. Both methods default to pass-through, so implement only what you need.
/// </summary>
public interface ILlmMiddleware
{
    Task<Response> CompleteAsync(Request request, CompleteNext next, CancellationToken cancellationToken) =>
        next(request, cancellationToken);

    IAsyncEnumerable<StreamEvent> StreamAsync(Request request, StreamNext next, CancellationToken cancellationToken) =>
        next(request, cancellationToken);
}

/// <summary>Builds middleware from delegates.</summary>
public static class LlmMiddleware
{
    public static ILlmMiddleware Create(
        Func<Request, CompleteNext, CancellationToken, Task<Response>>? complete = null,
        Func<Request, StreamNext, CancellationToken, IAsyncEnumerable<StreamEvent>>? stream = null) =>
        new DelegateMiddleware(complete, stream);

    /// <summary>Middleware that observes every stream event without changing it.</summary>
    public static ILlmMiddleware ObserveStream(Action<Request, StreamEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new DelegateMiddleware(null, (req, next, ct) => Observe(req, next(req, ct), observer, ct));
    }

    private static async IAsyncEnumerable<StreamEvent> Observe(
        Request request,
        IAsyncEnumerable<StreamEvent> source,
        Action<Request, StreamEvent> observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            observer(request, evt);
            yield return evt;
        }
    }

    private sealed class DelegateMiddleware(
        Func<Request, CompleteNext, CancellationToken, Task<Response>>? complete,
        Func<Request, StreamNext, CancellationToken, IAsyncEnumerable<StreamEvent>>? stream) : ILlmMiddleware
    {
        public Task<Response> CompleteAsync(Request request, CompleteNext next, CancellationToken cancellationToken) =>
            complete is null ? next(request, cancellationToken) : complete(request, next, cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, StreamNext next, CancellationToken cancellationToken) =>
            stream is null ? next(request, cancellationToken) : stream(request, next, cancellationToken);
    }
}

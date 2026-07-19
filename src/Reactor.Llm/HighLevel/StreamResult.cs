using System.Runtime.CompilerServices;

namespace Reactor.Llm;

/// <summary>
/// The result of <see cref="Ai.Stream"/>: an asynchronous sequence of events (enumerable once), plus the accumulated
/// response after the stream ends. With active tools the stream spans multiple steps: intermediate steps end with a
/// <see cref="StreamEventType.StepFinish"/> event and only the final step emits <see cref="StreamEventType.Finish"/>.
/// </summary>
public sealed class StreamResult : IAsyncEnumerable<StreamEvent>
{
    private readonly Func<StreamResult, CancellationToken, IAsyncEnumerable<StreamEvent>> _producer;
    private readonly CancellationToken _operationToken;
    private readonly TaskCompletionSource<Response> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    internal StreamResult(Func<StreamResult, CancellationToken, IAsyncEnumerable<StreamEvent>> producer, CancellationToken operationToken)
    {
        _producer = producer;
        _operationToken = operationToken;
    }

    /// <summary>The current accumulated state of the active step at any point (null before the stream starts).</summary>
    public Response? PartialResponse => Accumulator?.PartialResponse;

    internal StreamAccumulator? Accumulator { get; set; }

    /// <summary>The final response once the stream has completed successfully.</summary>
    public Response? Response => _completion.Task.IsCompletedSuccessfully ? _completion.Task.Result : null;

    /// <summary>Per-step results, populated as steps complete.</summary>
    public IReadOnlyList<StepResult> Steps => StepsList;

    /// <summary>Usage aggregated across all completed steps.</summary>
    public Usage TotalUsage { get; internal set; } = Usage.Empty;

    internal List<StepResult> StepsList { get; } = [];

    /// <summary>Yields only text deltas. Consumes the stream (use instead of enumerating events).</summary>
    public IAsyncEnumerable<string> TextStream => FilterText(this);

    public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("A StreamResult can only be enumerated once. Use GetResponseAsync() to await completion.");
        }

        return Run(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    /// <summary>
    /// Returns the accumulated final response. If the stream has not been enumerated, it is consumed now; if it is being
    /// enumerated elsewhere, this waits for it to finish. Throws the stream's error if it failed.
    /// </summary>
    public async Task<Response> GetResponseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            await foreach (var _ in this.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
            }
        }

        return await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void Complete(Response response) => _completion.TrySetResult(response);

    internal void Fail(Exception error) => _completion.TrySetException(error);

    private async IAsyncEnumerable<StreamEvent> Run([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_operationToken, cancellationToken);
        var completed = false;
        try
        {
            await foreach (var evt in _producer(this, linked.Token).WithCancellation(linked.Token).ConfigureAwait(false))
            {
                yield return evt;
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                _completion.TrySetException(new AbortError("The stream was not consumed to completion."));
            }
        }
    }

    private static async IAsyncEnumerable<string> FilterText(StreamResult source)
    {
        await foreach (var evt in source.ConfigureAwait(false))
        {
            if (evt.Type == StreamEventType.TextDelta && evt.Delta is { Length: > 0 } delta)
            {
                yield return delta;
            }
        }
    }
}

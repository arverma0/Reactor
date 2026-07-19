using System.Threading.Channels;
using Reactor.Llm;

namespace Reactor.Agent;

/// <summary>Kinds of session events (spec 2.9).</summary>
public enum EventKind
{
    SessionStart,
    SessionEnd,
    UserInput,
    ProcessingEnd,
    AssistantTextStart,
    AssistantTextDelta,
    AssistantTextEnd,
    ToolCallStart,
    ToolCallOutputDelta,
    ToolCallEnd,
    SteeringInjected,
    TurnLimit,
    LoopDetection,
    Warning,
    Error,
}

/// <summary>A typed event emitted by a session.</summary>
/// <param name="Kind">The event kind.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="SessionId">The emitting session (subagent events carry the subagent's id).</param>
/// <param name="Data">Event payload (e.g. <c>text</c>, <c>tool_name</c>, <c>call_id</c>, <c>output</c>, <c>error</c>).</param>
public sealed record SessionEvent(EventKind Kind, DateTimeOffset Timestamp, string SessionId, IReadOnlyDictionary<string, object?> Data)
{
    public string? GetString(string key) => Data.TryGetValue(key, out var value) ? value?.ToString() : null;

    public override string ToString() => $"{Kind} {string.Join(", ", Data.Select(kv => $"{kv.Key}={Clip(kv.Value?.ToString())}"))}";

    private static string Clip(string? value) => value is null ? "null" : value.Length <= 80 ? value : value[..80] + "…";
}

/// <summary>Delivers session events to observers and asynchronous readers.</summary>
public sealed class SessionEventEmitter
{
    private readonly object _gate = new();
    private readonly List<Action<SessionEvent>> _observers = [];
    private readonly List<Channel<SessionEvent>> _channels = [];
    private bool _completed;

    public IDisposable Subscribe(Action<SessionEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _observers.Add(observer);
        }

        return new Unsubscriber(() =>
        {
            lock (_gate)
            {
                _observers.Remove(observer);
            }
        });
    }

    public void Emit(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        Action<SessionEvent>[] observers;
        Channel<SessionEvent>[] channels;
        lock (_gate)
        {
            observers = _observers.ToArray();
            channels = _channels.ToArray();
        }

        foreach (var observer in observers)
        {
            try
            {
                observer(sessionEvent);
            }
            catch (Exception)
            {
                // Observers must never break the agent loop.
            }
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(sessionEvent);
        }
    }

    /// <summary>Streams events emitted after this call until the session ends.</summary>
    public async IAsyncEnumerable<SessionEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            if (_completed)
            {
                yield break;
            }

            _channels.Add(channel);
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_gate)
            {
                _channels.Remove(channel);
            }
        }
    }

    /// <summary>Completes all async readers (called after SESSION_END).</summary>
    public void Complete()
    {
        Channel<SessionEvent>[] channels;
        lock (_gate)
        {
            _completed = true;
            channels = _channels.ToArray();
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Session lifecycle states (spec 2.3).</summary>
public enum SessionState
{
    Idle,
    Processing,
    AwaitingInput,
    Closed,
}

/// <summary>A conversation history entry (spec 2.4).</summary>
public abstract record Turn
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record UserTurn(string Content) : Turn;

/// <summary>A model response. <see cref="Message"/> keeps every content part (thinking signatures, tool calls) for faithful round-tripping.</summary>
public sealed record AssistantTurn(Message Message, Usage Usage, string? ResponseId) : Turn
{
    public string Content => Message.Text;

    public IReadOnlyList<ToolCall> ToolCalls => Message.ToolCalls.Select(ToolCall.FromData).ToList();

    public string? Reasoning
    {
        get
        {
            var parts = Message.Content.Where(p => p.Kind == ContentKind.Thinking && p.Thinking is { Redacted: false, Text.Length: > 0 }).Select(p => p.Thinking!.Text).ToList();
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
    }
}

public sealed record ToolResultsTurn(IReadOnlyList<ToolResult> Results) : Turn;

public sealed record SystemTurn(string Content) : Turn;

/// <summary>A host-injected steering message (sent to the model as a user message).</summary>
public sealed record SteeringTurn(string Content) : Turn;

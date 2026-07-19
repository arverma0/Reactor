using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Reactor.Llm.Tests.Support;

/// <summary>
/// An in-memory provider adapter driven by a script of responses (or failures). Streams are synthesized from the scripted
/// response using the unified start/delta/end event pattern.
/// </summary>
internal sealed class ScriptedAdapter(string name = "scripted") : IProviderAdapter
{
    private readonly ConcurrentQueue<Func<Request, Response>> _script = new();

    public string Name { get; } = name;

    public List<Request> Requests { get; } = [];

    /// <summary>Delay applied before each response (to test timeouts and cancellation).</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>When set, the stream throws this after yielding the given number of events.</summary>
    public (int AfterEvents, Exception Error)? StreamFailure { get; set; }

    public ScriptedAdapter Then(Response response) => Then(_ => response);

    public ScriptedAdapter Then(Func<Request, Response> responder)
    {
        _script.Enqueue(responder);
        return this;
    }

    public ScriptedAdapter ThenThrow(Exception error) => Then(_ => throw error);

    public ScriptedAdapter ThenText(string text, Usage? usage = null) => Then(Text(text, usage));

    public ScriptedAdapter ThenToolCalls(params (string Id, string Name, string ArgsJson)[] calls) => Then(ToolCalls(calls));

    public async Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(Delay, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                throw new AbortError("cancelled", ex);
            }
        }

        if (!_script.TryDequeue(out var responder))
        {
            throw new InvalidOperationException("Scripted adapter ran out of responses.");
        }

        return responder(request) with { Provider = Name };
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await CompleteAsync(request, cancellationToken);
        var emitted = 0;
        foreach (var evt in Synthesize(response))
        {
            if (StreamFailure is { } failure && emitted == failure.AfterEvents)
            {
                throw failure.Error;
            }

            cancellationToken.ThrowIfCancellationRequested();
            emitted++;
            yield return evt;
        }
    }

    public static Response Text(string text, Usage? usage = null) => new()
    {
        Id = "resp_" + Guid.NewGuid().ToString("N")[..8],
        Model = "scripted-model",
        Message = Message.Assistant(text),
        FinishReason = FinishReason.Stop,
        Usage = usage ?? new Usage { InputTokens = 10, OutputTokens = 5, TotalTokens = 15 },
    };

    public static Response ToolCalls(params (string Id, string Name, string ArgsJson)[] calls) => new()
    {
        Id = "resp_" + Guid.NewGuid().ToString("N")[..8],
        Model = "scripted-model",
        Message = new Message
        {
            Role = Role.Assistant,
            Content = calls.Select(c => ContentPart.ToolCallPart(c.Id, c.Name, JsonNode.Parse(c.ArgsJson))).ToList(),
        },
        FinishReason = FinishReason.ToolCalls,
        Usage = new Usage { InputTokens = 20, OutputTokens = 8, TotalTokens = 28 },
    };

    private static IEnumerable<StreamEvent> Synthesize(Response response)
    {
        yield return new StreamEvent { Type = StreamEventType.StreamStart };
        var index = 0;
        foreach (var part in response.Message.Content)
        {
            if (part.Kind == ContentKind.Text && part.Text is { } text)
            {
                var id = "t" + index++;
                yield return new StreamEvent { Type = StreamEventType.TextStart, TextId = id };
                for (var i = 0; i < text.Length; i += 4)
                {
                    yield return new StreamEvent { Type = StreamEventType.TextDelta, TextId = id, Delta = text.Substring(i, Math.Min(4, text.Length - i)) };
                }

                yield return new StreamEvent { Type = StreamEventType.TextEnd, TextId = id };
            }
            else if (part.Kind == ContentKind.ToolCall && part.ToolCall is { } call)
            {
                var json = call.ArgumentsJson;
                yield return new StreamEvent { Type = StreamEventType.ToolCallStart, ToolCall = new ToolCall(call.Id, call.Name, []) };
                for (var i = 0; i < json.Length; i += 5)
                {
                    yield return new StreamEvent
                    {
                        Type = StreamEventType.ToolCallDelta,
                        ToolCall = new ToolCall(call.Id, call.Name, []),
                        Delta = json.Substring(i, Math.Min(5, json.Length - i)),
                    };
                }

                yield return new StreamEvent { Type = StreamEventType.ToolCallEnd, ToolCall = ToolCall.FromData(call) };
            }
        }

        yield return new StreamEvent
        {
            Type = StreamEventType.Finish,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            Response = response,
        };
    }
}

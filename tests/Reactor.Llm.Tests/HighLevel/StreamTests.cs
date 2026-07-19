using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Tests.Support;

namespace Reactor.Llm.Tests.HighLevel;

public class StreamTests
{
    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public async Task Text_deltas_concatenate_to_the_final_response_text()
    {
        var adapter = new ScriptedAdapter().ThenText("The quick brown fox jumps over the lazy dog.");
        var stream = Ai.Stream(new GenerateOptions { Model = "m", Prompt = "go", Client = Client.Create(adapter) });

        var types = new List<StreamEventType>();
        var text = new StringBuilder();
        await foreach (var evt in stream)
        {
            types.Add(evt.Type);
            if (evt.Type == StreamEventType.TextDelta)
            {
                text.Append(evt.Delta);
            }
        }

        var response = await stream.GetResponseAsync();
        Assert.Equal(StreamEventType.StreamStart, types[0]);
        Assert.Equal(StreamEventType.Finish, types[^1]);
        Assert.Contains(StreamEventType.TextStart, types);
        Assert.Contains(StreamEventType.TextEnd, types);
        Assert.Equal(response.Text, text.ToString());
        Assert.Equal(response, stream.Response);
    }

    [Fact]
    public async Task Stream_can_only_be_enumerated_once_and_text_stream_filters_deltas()
    {
        var adapter = new ScriptedAdapter().ThenText("abcdefgh");
        var stream = Ai.Stream(new GenerateOptions { Model = "m", Prompt = "go", Client = Client.Create(adapter) });

        var chunks = new List<string>();
        await foreach (var chunk in stream.TextStream)
        {
            chunks.Add(chunk);
        }

        Assert.Equal("abcdefgh", string.Concat(chunks));
        Assert.Throws<InvalidOperationException>(() => stream.GetAsyncEnumerator());
        Assert.Equal("abcdefgh", (await stream.GetResponseAsync()).Text);
    }

    [Fact]
    public async Task Get_response_drains_an_unconsumed_stream()
    {
        var adapter = new ScriptedAdapter().ThenText("drained");
        var stream = Ai.Stream(new GenerateOptions { Model = "m", Prompt = "go", Client = Client.Create(adapter) });

        var response = await stream.GetResponseAsync();

        Assert.Equal("drained", response.Text);
    }

    [Fact]
    public async Task Streaming_with_tools_spans_steps_with_step_finish_events()
    {
        var adapter = new ScriptedAdapter()
            .ThenToolCalls(("c1", "add", """{"a":2,"b":3}"""))
            .ThenText("The sum is 5.");
        var add = Tool.Create("add", "Adds", Schema("""{"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}}}"""),
            args => args["a"]!.GetValue<int>() + args["b"]!.GetValue<int>());
        var stream = Ai.Stream(new GenerateOptions { Model = "m", Prompt = "add", Tools = [add], MaxToolRounds = 3, Client = Client.Create(adapter) });

        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        Assert.Single(events, e => e.Type == StreamEventType.StepFinish);
        Assert.Single(events, e => e.Type == StreamEventType.Finish);
        Assert.Equal(StreamEventType.Finish, events[^1].Type);
        Assert.Contains(events, e => e.Type == StreamEventType.ToolCallEnd && e.ToolCall!.Arguments["a"]!.GetValue<int>() == 2);
        Assert.Equal(2, stream.Steps.Count);
        Assert.Equal("5", stream.Steps[0].ToolResults[0].ContentText);
        Assert.Equal("The sum is 5.", (await stream.GetResponseAsync()).Text);
        Assert.Equal(15 + 28, stream.TotalUsage.TotalTokens);
    }

    [Fact]
    public async Task Initial_connection_failures_are_retried()
    {
        var adapter = new ScriptedAdapter().ThenThrow(new ServerError("boom", "scripted")).ThenText("ok");
        var stream = Ai.Stream(new GenerateOptions
        {
            Model = "m",
            Prompt = "go",
            Client = Client.Create(adapter),
            RetryPolicy = new RetryPolicy { MaxRetries = 2, BaseDelay = 0.001, Jitter = false },
        });

        Assert.Equal("ok", (await stream.GetResponseAsync()).Text);
        Assert.Equal(2, adapter.Requests.Count);
    }

    [Fact]
    public async Task Failures_after_data_was_delivered_emit_an_error_event_without_retry()
    {
        var adapter = new ScriptedAdapter().ThenText("partial text that will break").ThenText("unused");
        adapter.StreamFailure = (3, new ServerError("connection reset", "scripted"));
        var stream = Ai.Stream(new GenerateOptions { Model = "m", Prompt = "go", Client = Client.Create(adapter) });

        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        Assert.Equal(StreamEventType.Error, events[^1].Type);
        Assert.IsType<ServerError>(events[^1].Error);
        Assert.Single(adapter.Requests);
        await Assert.ThrowsAsync<ServerError>(() => stream.GetResponseAsync());
    }

    [Fact]
    public void Accumulator_builds_a_response_from_events_without_a_finish_response()
    {
        var acc = new StreamAccumulator();
        acc.Process(new StreamEvent { Type = StreamEventType.StreamStart });
        acc.Process(new StreamEvent { Type = StreamEventType.ReasoningStart });
        acc.Process(new StreamEvent { Type = StreamEventType.ReasoningDelta, ReasoningDelta = "thinking..." });
        acc.Process(new StreamEvent { Type = StreamEventType.ReasoningEnd });
        acc.Process(new StreamEvent { Type = StreamEventType.TextStart, TextId = "a" });
        acc.Process(new StreamEvent { Type = StreamEventType.TextDelta, TextId = "a", Delta = "Hel" });
        acc.Process(new StreamEvent { Type = StreamEventType.TextDelta, TextId = "a", Delta = "lo" });
        acc.Process(new StreamEvent { Type = StreamEventType.ToolCallStart, ToolCall = new ToolCall("c1", "f", []) });
        acc.Process(new StreamEvent { Type = StreamEventType.ToolCallDelta, ToolCall = new ToolCall("c1", "f", []), Delta = "{\"x\":" });
        Assert.Equal("Hello", acc.PartialResponse.Text);
        acc.Process(new StreamEvent { Type = StreamEventType.ToolCallDelta, ToolCall = new ToolCall("c1", "f", []), Delta = "1}" });
        acc.Process(new StreamEvent { Type = StreamEventType.Finish, FinishReason = FinishReason.ToolCalls, Usage = new Usage { TotalTokens = 9 } });

        var response = acc.Response;
        Assert.True(acc.IsFinished);
        Assert.Equal("Hello", response.Text);
        Assert.Equal("thinking...", response.Reasoning);
        Assert.Equal(1, response.ToolCalls[0].Arguments["x"]!.GetValue<int>());
        Assert.Equal(FinishReason.ToolCallsReason, response.FinishReason.Reason);
        Assert.Equal(9, response.Usage.TotalTokens);
    }
}

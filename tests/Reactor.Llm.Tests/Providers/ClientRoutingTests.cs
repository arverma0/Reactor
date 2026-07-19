using System.Text.Json.Nodes;
using Reactor.Llm.Providers;

namespace Reactor.Llm.Tests.Providers;

/// <summary>End-to-end routing: <see cref="Client"/> → middleware chain → provider adapter → (fake) HTTP.</summary>
public class ClientRoutingTests
{
    private const string AnthropicText = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"From Claude"}],
         "stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":2}}
        """;

    private const string OpenAIText = """
        {"id":"resp_1","status":"completed","model":"gpt-4.1","output":[{"type":"message","role":"assistant",
         "content":[{"type":"output_text","text":"From GPT"}]}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}
        """;

    private const string AnthropicStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_s","model":"claude-sonnet-4-5","usage":{"input_tokens":3,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Streamed"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    [Fact]
    public async Task Client_routes_through_middleware_to_the_selected_adapter()
    {
        var openAIHandler = new FakeHttpHandler().EnqueueJson(OpenAIText);
        var anthropicHandler = new FakeHttpHandler().EnqueueJson(AnthropicText).EnqueueSse(AnthropicStream);
        var openAI = new OpenAIAdapter(new OpenAIAdapterOptions { ApiKey = "sk", HttpClient = new HttpClient(openAIHandler) });
        var anthropic = new AnthropicAdapter(new AnthropicAdapterOptions { ApiKey = "ak", HttpClient = new HttpClient(anthropicHandler) });

        var order = new List<string>();
        var tagging = LlmMiddleware.Create(complete: async (request, next, ct) =>
        {
            order.Add("outer:before");
            var response = await next(request with { Metadata = new Dictionary<string, string> { ["user_id"] = "tenant-7" } }, ct);
            order.Add("outer:after");
            return response;
        });
        var timing = LlmMiddleware.Create(complete: async (request, next, ct) =>
        {
            order.Add("inner:before:" + request.Provider);
            var response = await next(request, ct);
            order.Add("inner:after:" + response.Provider);
            return response;
        });
        var observed = new List<string>();
        var observer = LlmMiddleware.ObserveStream((_, evt) => observed.Add(evt.Type.Value));

        await using var client = new Client(
            new Dictionary<string, IProviderAdapter> { ["openai"] = openAI, ["anthropic"] = anthropic },
            defaultProvider: "openai",
            middleware: [tagging, timing, observer]);

        var claude = await client.CompleteAsync(new Request { Model = "claude-sonnet-4-5", Provider = "anthropic", Messages = [Message.User("Hi")] });
        var gpt = await client.CompleteAsync(new Request { Model = "gpt-4.1", Messages = [Message.User("Hi")] });
        var streamed = await TestUtil.CollectAsync(client.StreamAsync(new Request { Model = "claude-sonnet-4-5", Provider = "anthropic", Messages = [Message.User("Hi")] }));

        Assert.Equal("From Claude", claude.Text);
        Assert.Equal("anthropic", claude.Provider);
        Assert.Equal("From GPT", gpt.Text);
        Assert.Equal("openai", gpt.Provider);
        Assert.Equal(
            ["outer:before", "inner:before:anthropic", "inner:after:anthropic", "outer:after",
             "outer:before", "inner:before:openai", "inner:after:openai", "outer:after"],
            order);

        // The middleware's request rewrite reached the provider's wire format.
        Assert.Equal("tenant-7", anthropicHandler.Requests[0].Json["metadata"]!["user_id"]!.GetValue<string>());
        Assert.Equal("tenant-7", openAIHandler.Requests[0].Json["metadata"]!["user_id"]!.GetValue<string>());
        Assert.Single(openAIHandler.Requests);
        Assert.Equal(2, anthropicHandler.Requests.Count);

        Assert.Equal(["stream_start", "text_start", "text_delta", "text_end", "finish"], observed);
        Assert.Equal(observed, TestUtil.Types(streamed));
        Assert.Equal("Streamed", streamed[^1].Response!.Text);
        Assert.Equal(7, streamed[^1].Usage!.TotalTokens);
    }

    [Fact]
    public async Task Client_surfaces_adapter_errors_unchanged()
    {
        var handler = new FakeHttpHandler().EnqueueJson(
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""",
            System.Net.HttpStatusCode.Unauthorized);
        await using var client = Client.Create(new AnthropicAdapter(new AnthropicAdapterOptions { ApiKey = "bad", HttpClient = new HttpClient(handler) }));

        var error = await Assert.ThrowsAsync<AuthenticationError>(() => client.CompleteAsync(new Request { Model = "claude-sonnet-4-5", Messages = [Message.User("Hi")] }));

        Assert.False(error.Retryable);
        Assert.Equal(401, error.StatusCode);
        Assert.NotNull(error.Raw);
        Assert.Equal("authentication_error", error.Raw["error"]!["type"]!.GetValue<string>());
        Assert.IsType<JsonObject>(error.Raw);
    }
}

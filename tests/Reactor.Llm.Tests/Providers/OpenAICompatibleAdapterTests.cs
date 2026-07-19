using System.Net;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers;

namespace Reactor.Llm.Tests.Providers;

public class OpenAICompatibleAdapterTests
{
    private const string ToolCallResponse = """
        {
          "id": "chatcmpl-9x1",
          "object": "chat.completion",
          "created": 1760000000,
          "model": "qwen3-32b",
          "choices": [{
            "index": 0,
            "message": {
              "role": "assistant",
              "content": "Checking both cities.",
              "reasoning_content": "Two cities, two calls.",
              "tool_calls": [
                {"id": "call_paris", "type": "function", "function": {"name": "get_weather", "arguments": "{\"city\":\"Paris\"}"}},
                {"id": "call_rome", "type": "function", "function": {"name": "get_weather", "arguments": "{\"city\":\"Rome\",\"unit\":\"c\"}"}}
              ]
            },
            "finish_reason": "tool_calls"
          }],
          "usage": {"prompt_tokens": 80, "completion_tokens": 30, "total_tokens": 110,
                    "completion_tokens_details": {"reasoning_tokens": 12}, "prompt_tokens_details": {"cached_tokens": 64}}
        }
        """;

    private const string ToolCallStream = """
        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"role":"assistant","content":null,"reasoning_content":"Two cities, "},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"reasoning_content":"two calls."},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"content":"Checking "},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"content":"both cities."},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_paris","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_rome","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Rome\","}}]},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Paris\"}"}}]},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"\"unit\":\"c\"}"}}]},"finish_reason":null}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: {"id":"chatcmpl-9x1","object":"chat.completion.chunk","created":1760000000,"model":"qwen3-32b","choices":[],"usage":{"prompt_tokens":80,"completion_tokens":30,"total_tokens":110,"completion_tokens_details":{"reasoning_tokens":12},"prompt_tokens_details":{"cached_tokens":64}}}

        data: [DONE]

        """;

    private const string TextResponse = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"llama3.1:8b",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}
        """;

    private static (OpenAICompatibleAdapter Adapter, FakeHttpHandler Handler) Create(Func<OpenAICompatibleAdapterOptions, OpenAICompatibleAdapterOptions>? configure = null)
    {
        var handler = new FakeHttpHandler();
        var options = new OpenAICompatibleAdapterOptions
        {
            Name = "vllm",
            BaseUrl = "http://localhost:8000/v1/",
            ApiKey = "local-key",
            HttpClient = new HttpClient(handler),
        };
        return (new OpenAICompatibleAdapter(configure?.Invoke(options) ?? options), handler);
    }

    private static Request SimpleRequest() => new() { Model = "qwen3-32b", Messages = [Message.User("Hi")] };

    [Fact]
    public async Task Complete_translates_messages_tools_and_parameters_to_chat_completions()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(new Request
        {
            Model = "qwen3-32b",
            Messages =
            [
                Message.System("You are terse."),
                Message.Developer("Use metric."),
                Message.User("Weather?"),
                Message.Assistant(ContentPart.ThinkingPart("hmm"), ContentPart.TextPart("Checking."), ContentPart.ToolCallPart("call_1", "get_weather", new JsonObject { ["city"] = "Paris", ["days"] = 2 })),
                Message.ToolResult("call_1", new JsonObject { ["temp"] = 18 }),
                Message.User(ContentPart.TextPart("And this?"), ContentPart.ImagePart(ImageData.FromUrl("https://example.com/x.png", "low"))),
            ],
            Tools = [TestUtil.WeatherTool],
            MaxTokens = 300,
            Temperature = 0.3,
            TopP = 0.7,
            StopSequences = ["<|end|>"],
            ReasoningEffort = "low",
            ResponseFormat = ResponseFormat.Json,
            ProviderOptions = (JsonObject)JsonNode.Parse("""{"vllm": {"top_k": 20, "chat_template_kwargs": {"enable_thinking": true}}, "openai": {"ignored": true}}""")!,
        });

        var sent = handler.LastRequest;
        Assert.Equal("http://localhost:8000/v1/chat/completions", sent.Uri.ToString());
        Assert.Equal("Bearer local-key", sent.Header("Authorization"));
        var expected = JsonNode.Parse("""
            {
              "model": "qwen3-32b",
              "messages": [
                {"role": "system", "content": "You are terse."},
                {"role": "system", "content": "Use metric."},
                {"role": "user", "content": "Weather?"},
                {"role": "assistant", "content": "Checking.", "tool_calls": [
                  {"id": "call_1", "type": "function", "function": {"name": "get_weather", "arguments": "{\"city\":\"Paris\",\"days\":2}"}}]},
                {"role": "tool", "tool_call_id": "call_1", "content": "{\"temp\":18}"},
                {"role": "user", "content": [
                  {"type": "text", "text": "And this?"},
                  {"type": "image_url", "image_url": {"url": "https://example.com/x.png", "detail": "low"}}]}
              ],
              "tools": [{"type": "function", "function": {"name": "get_weather", "description": "Get the weather for a city",
                "parameters": {"type":"object","properties":{"city":{"type":"string","description":"City name"},"unit":{"type":"string","enum":["c","f"]}},"required":["city"]}}}],
              "tool_choice": "auto",
              "max_tokens": 300,
              "temperature": 0.3,
              "top_p": 0.7,
              "stop": ["<|end|>"],
              "response_format": {"type": "json_object"},
              "reasoning_effort": "low",
              "top_k": 20,
              "chat_template_kwargs": {"enable_thinking": true}
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, sent.Json), sent.Body);
        Assert.Equal("vllm", response.Provider);
        Assert.Equal("Hello!", response.Text);
        Assert.Equal(new FinishReason(FinishReason.StopReason, "stop"), response.FinishReason);
        Assert.Equal(7, response.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("auto", null, "\"auto\"")]
    [InlineData("none", null, "\"none\"")]
    [InlineData("required", null, "\"required\"")]
    [InlineData("named", "get_weather", """{"type":"function","function":{"name":"get_weather"}}""")]
    public async Task Tool_choice_modes_are_translated(string mode, string? toolName, string expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest() with { Tools = [TestUtil.WeatherTool], ToolChoice = new ToolChoice(mode, toolName) });

        Assert.Equal(expected, handler.LastRequest.Json["tool_choice"]!.ToJsonString());
    }

    [Fact]
    public async Task Json_schema_response_format_and_no_auth_without_api_key()
    {
        var (adapter, handler) = Create(o => o with { ApiKey = null });
        handler.EnqueueJson(TextResponse);
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"}}}""")!;

        await adapter.CompleteAsync(SimpleRequest() with { ResponseFormat = ResponseFormat.ForSchema(schema, strict: true, name: "thing") });

        Assert.Null(handler.LastRequest.Header("Authorization"));
        Assert.Equal(
            """{"type":"json_schema","json_schema":{"name":"thing","schema":{"type":"object","properties":{"a":{"type":"string"}}},"strict":true}}""",
            handler.LastRequest.Json["response_format"]!.ToJsonString());
    }

    [Fact]
    public async Task Parses_reasoning_text_tool_calls_and_usage()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(ToolCallResponse);

        var response = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal("chatcmpl-9x1", response.Id);
        Assert.Equal("qwen3-32b", response.Model);
        Assert.Equal([ContentKind.Thinking, ContentKind.Text, ContentKind.ToolCall, ContentKind.ToolCall], response.Message.Content.Select(p => p.Kind).ToArray());
        Assert.Equal("Two cities, two calls.", response.Reasoning);
        Assert.Equal("Checking both cities.", response.Text);
        Assert.Equal(["call_paris", "call_rome"], response.ToolCalls.Select(c => c.Id).ToArray());
        Assert.Equal("c", response.ToolCalls[1].Arguments["unit"]!.GetValue<string>());
        Assert.Equal(new FinishReason(FinishReason.ToolCallsReason, "tool_calls"), response.FinishReason);
        Assert.Equal(80, response.Usage.InputTokens);
        Assert.Equal(30, response.Usage.OutputTokens);
        Assert.Equal(110, response.Usage.TotalTokens);
        Assert.Equal(12, response.Usage.ReasoningTokens);
        Assert.Equal(64, response.Usage.CacheReadTokens);
    }

    [Theory]
    [InlineData("stop", false, "stop")]
    [InlineData("stop", true, "tool_calls")]
    [InlineData("length", false, "length")]
    [InlineData("function_call", true, "tool_calls")]
    [InlineData("content_filter", false, "content_filter")]
    [InlineData("weird", false, "other")]
    public async Task Finish_reasons_are_mapped(string finishReason, bool withToolCall, string expected)
    {
        var (adapter, handler) = Create();
        var toolCalls = withToolCall ? ""","tool_calls":[{"id":"c1","type":"function","function":{"name":"f","arguments":"{}"}}]""" : "";
        handler.EnqueueJson(
            "{\"id\":\"x\",\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"" + toolCalls +
            "},\"finish_reason\":\"" + finishReason + "\"}]}");

        var response = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal(new FinishReason(expected, finishReason), response.FinishReason);
    }

    [Fact]
    public async Task Stream_accumulates_interleaved_tool_call_deltas_and_finishes_on_done()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse(ToolCallStream).EnqueueJson(ToolCallResponse);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));
        var completed = await adapter.CompleteAsync(SimpleRequest());

        var streamBody = handler.Requests[0].Json;
        Assert.True(streamBody["stream"]!.GetValue<bool>());
        Assert.Equal("""{"include_usage":true}""", streamBody["stream_options"]!.ToJsonString());
        Assert.Equal(
            [
                "stream_start",
                "reasoning_start", "reasoning_delta", "reasoning_delta", "reasoning_end",
                "text_start", "text_delta", "text_delta", "text_end",
                "tool_call_start", "tool_call_delta", "tool_call_start", "tool_call_delta", "tool_call_delta", "tool_call_delta",
                "tool_call_end", "tool_call_end",
                "finish",
            ],
            TestUtil.Types(events));

        var finish = events[^1];
        Assert.Equal(TestUtil.TextDeltas(events), finish.Response!.Text);
        Assert.Equal(TestUtil.ReasoningDeltas(events), finish.Response.Reasoning);
        Assert.Equal(110, finish.Usage!.TotalTokens);
        Assert.Equal(12, finish.Usage.ReasoningTokens);
        TestUtil.AssertEquivalent(completed, finish.Response);
        Assert.Equal(completed.ToolCalls.Select(c => c.Id), finish.Response.ToolCalls.Select(c => c.Id));

        foreach (var id in new[] { "call_paris", "call_rome" })
        {
            var deltas = string.Concat(events.Where(e => e.Type == StreamEventType.ToolCallDelta && e.ToolCall!.Id == id).Select(e => e.Delta));
            var end = events.Single(e => e.Type == StreamEventType.ToolCallEnd && e.ToolCall!.Id == id).ToolCall!;
            Assert.Equal(deltas, end.RawArguments);
            Assert.Null(end.ArgumentsError);
        }
    }

    [Fact]
    public async Task Stream_without_done_finishes_after_finish_reason_but_errors_otherwise()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            data: {"id":"c1","model":"llama3.1:8b","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}

            data: {"id":"c1","model":"llama3.1:8b","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            """);
        handler.EnqueueSse("""
            data: {"id":"c2","model":"llama3.1:8b","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}

            """);

        var finished = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));
        var truncated = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "text_start", "text_delta", "text_end", "finish"], TestUtil.Types(finished));
        Assert.Equal("Hi", finished[^1].Response!.Text);
        Assert.Equal(["stream_start", "text_start", "text_delta", "error"], TestUtil.Types(truncated));
        Assert.IsType<StreamError>(truncated[^1].Error);
    }

    [Fact]
    public async Task Stream_tool_call_without_id_gets_synthetic_id_and_error_chunk_maps_to_error()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            data: {"id":"c3","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"search","arguments":"{\"query\":\"x\"}"}}]},"finish_reason":null}]}

            data: {"id":"c3","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """);
        handler.EnqueueSse("""
            data: {"error":{"message":"Model is overloaded","type":"server_error","code":503}}

            """);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest() with { Tools = [TestUtil.SearchTool] }));
        var errored = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        var start = events.Single(e => e.Type == StreamEventType.ToolCallStart).ToolCall!;
        Assert.StartsWith("call_", start.Id, StringComparison.Ordinal);
        Assert.Equal(start.Id, Assert.Single(events[^1].Response!.ToolCalls).Id);
        Assert.Equal(["stream_start", "error"], TestUtil.Types(errored));
        Assert.IsType<ServerError>(errored[^1].Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(AuthenticationError))]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundError))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(RateLimitError))]
    [InlineData(HttpStatusCode.BadGateway, typeof(ServerError))]
    public async Task Http_errors_map_to_the_error_hierarchy(HttpStatusCode status, Type expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""{"error":{"message":"failure","type":"error","code":null}}""", status);

        var error = await Assert.ThrowsAnyAsync<SdkError>(() => adapter.CompleteAsync(SimpleRequest()));

        Assert.IsType(expected, error);
        Assert.Equal("vllm", Assert.IsType<ProviderError>(error, exactMatch: false).Provider);
    }

    [Fact]
    public async Task Images_and_tool_result_images_are_sent_as_image_urls()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);
        using var file = TestUtil.CreateFile(".png", TestUtil.PngBytes);

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.User(ContentPart.ImagePart(ImageData.FromBytes(TestUtil.PngBytes)), ContentPart.ImagePart(ImageData.FromUrl(file.Path))),
                Message.Assistant(ContentPart.ToolCallPart("call_s", "screenshot", new JsonObject())),
                Message.ToolResult("call_s", JsonValue.Create("ok"), imageData: TestUtil.PngBytes, imageMediaType: "image/png"),
            ],
        });

        var messages = handler.LastRequest.Json["messages"]!.AsArray();
        var dataUri = "data:image/png;base64," + TestUtil.PngBase64;
        Assert.Equal(dataUri, messages[0]!["content"]![0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal(dataUri, messages[0]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("tool", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[3]!["role"]!.GetValue<string>());
        Assert.Equal(dataUri, messages[3]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_base_url_is_a_configuration_error()
    {
        Assert.Throws<ConfigurationError>(() => new OpenAICompatibleAdapter(new OpenAICompatibleAdapterOptions()));
    }
}

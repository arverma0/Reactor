using System.Net;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers;

namespace Reactor.Llm.Tests.Providers;

public class OpenAIAdapterTests
{
    private const string ReasoningResponse = """
        {
          "id": "resp_67ccd2bed1ec8190b14f964abc054267",
          "object": "response",
          "created_at": 1741476542,
          "status": "completed",
          "error": null,
          "incomplete_details": null,
          "model": "gpt-5.2-2025-12-11",
          "output": [
            {
              "type": "reasoning",
              "id": "rs_6820f383d7c08191846711c5df8233bc",
              "summary": [
                {"type": "summary_text", "text": "**Checking the weather**"},
                {"type": "summary_text", "text": "Need to call get_weather."}
              ],
              "encrypted_content": "gAAAAABoIPOD-encrypted-blob"
            },
            {
              "type": "message",
              "id": "msg_67ccd2bf17f0819081ff3bb2cf6508e6",
              "status": "completed",
              "role": "assistant",
              "content": [{"type": "output_text", "text": "Let me check the weather.", "annotations": []}]
            },
            {
              "type": "function_call",
              "id": "fc_67ca09c6bedc8190a7abfec07b1a1332",
              "call_id": "call_unLAR8MvFNptuiZK6K6HCy5k",
              "name": "get_weather",
              "arguments": "{\"city\":\"Paris\",\"unit\":\"c\"}",
              "status": "completed"
            }
          ],
          "parallel_tool_calls": true,
          "store": false,
          "usage": {
            "input_tokens": 328,
            "input_tokens_details": {"cached_tokens": 128},
            "output_tokens": 152,
            "output_tokens_details": {"reasoning_tokens": 64},
            "total_tokens": 480
          }
        }
        """;

    private const string TextResponse = """
        {
          "id": "resp_text_1",
          "object": "response",
          "status": "completed",
          "model": "gpt-4.1-2025-04-14",
          "output": [
            {"type": "message", "id": "msg_1", "status": "completed", "role": "assistant",
             "content": [{"type": "output_text", "text": "Hello there!", "annotations": []}]}
          ],
          "usage": {"input_tokens": 12, "input_tokens_details": {"cached_tokens": 0}, "output_tokens": 4,
                    "output_tokens_details": {"reasoning_tokens": 0}, "total_tokens": 16}
        }
        """;

    private static (OpenAIAdapter Adapter, FakeHttpHandler Handler) Create(Func<OpenAIAdapterOptions, OpenAIAdapterOptions>? configure = null)
    {
        var handler = new FakeHttpHandler();
        var options = new OpenAIAdapterOptions { ApiKey = "sk-test", HttpClient = new HttpClient(handler) };
        return (new OpenAIAdapter(configure?.Invoke(options) ?? options), handler);
    }

    private static Request SimpleRequest(string model = "gpt-4.1") => new() { Model = model, Messages = [Message.User("Hi")] };

    [Fact]
    public async Task Complete_translates_roles_tools_and_parameters_to_the_responses_api()
    {
        var (adapter, handler) = Create(o => o with
        {
            Organization = "org-1",
            Project = "proj-1",
            DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = "abc" },
        });
        handler.EnqueueJson(TextResponse);

        var request = new Request
        {
            Model = "gpt-4.1",
            Messages =
            [
                Message.System("You are terse."),
                Message.Developer("Always answer in English."),
                Message.User(ContentPart.TextPart("What's the weather in Paris?"), ContentPart.ImagePart(ImageData.FromUrl("https://example.com/paris.jpg", "high"))),
                Message.Assistant(ContentPart.TextPart("Checking."), ContentPart.ToolCallPart("call_1", "get_weather", new JsonObject { ["city"] = "Paris", ["days"] = 3 })),
                Message.ToolResult("call_1", "18C and sunny"),
                Message.User("Thanks"),
            ],
            Tools = [TestUtil.WeatherTool],
            MaxTokens = 256,
            Temperature = 0.2,
            TopP = 0.9,
            StopSequences = ["END"],
            Metadata = new Dictionary<string, string> { ["session"] = "s1" },
        };

        var response = await adapter.CompleteAsync(request);

        var sent = handler.LastRequest;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.openai.com/v1/responses", sent.Uri.ToString());
        Assert.Equal("Bearer sk-test", sent.Header("Authorization"));
        Assert.Equal("org-1", sent.Header("OpenAI-Organization"));
        Assert.Equal("proj-1", sent.Header("OpenAI-Project"));
        Assert.Equal("abc", sent.Header("X-Trace"));

        var expected = JsonNode.Parse("""
            {
              "model": "gpt-4.1",
              "instructions": "You are terse.\n\nAlways answer in English.",
              "input": [
                {"type": "message", "role": "user", "content": [
                  {"type": "input_text", "text": "What's the weather in Paris?"},
                  {"type": "input_image", "image_url": "https://example.com/paris.jpg", "detail": "high"}]},
                {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "Checking."}]},
                {"type": "function_call", "call_id": "call_1", "name": "get_weather", "arguments": "{\"city\":\"Paris\",\"days\":3}"},
                {"type": "function_call_output", "call_id": "call_1", "output": "18C and sunny"},
                {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "Thanks"}]}
              ],
              "tools": [{"type": "function", "name": "get_weather", "description": "Get the weather for a city",
                         "parameters": {"type":"object","properties":{"city":{"type":"string","description":"City name"},"unit":{"type":"string","enum":["c","f"]}},"required":["city"]},
                         "strict": false}],
              "tool_choice": "auto",
              "max_output_tokens": 256,
              "temperature": 0.2,
              "top_p": 0.9,
              "metadata": {"session": "s1"}
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, sent.Json), sent.Body);

        Assert.Contains(response.Warnings, w => w.Message.Contains("stop", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Hello there!", response.Text);
        Assert.Equal("openai", response.Provider);
        Assert.Equal("gpt-4.1-2025-04-14", response.Model);
        Assert.Equal(FinishReason.StopReason, response.FinishReason.Reason);
    }

    [Theory]
    [InlineData("auto", null, "\"auto\"")]
    [InlineData("none", null, "\"none\"")]
    [InlineData("required", null, "\"required\"")]
    [InlineData("named", "get_weather", "{\"type\":\"function\",\"name\":\"get_weather\"}")]
    public async Task Tool_choice_modes_are_translated(string mode, string? toolName, string expectedJson)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest() with { Tools = [TestUtil.WeatherTool], ToolChoice = new ToolChoice(mode, toolName) });

        Assert.Equal(expectedJson, handler.LastRequest.Json["tool_choice"]!.ToJsonString());
        Assert.True(adapter.SupportsToolChoice(mode));
    }

    [Fact]
    public async Task Response_formats_map_to_text_format()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse).EnqueueJson(TextResponse);
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""")!;

        await adapter.CompleteAsync(SimpleRequest() with { ResponseFormat = ResponseFormat.ForSchema(schema, strict: true, name: "person") });
        await adapter.CompleteAsync(SimpleRequest() with { ResponseFormat = ResponseFormat.Json });

        var first = handler.Requests[0].Json["text"]!["format"]!;
        Assert.Equal("json_schema", first["type"]!.GetValue<string>());
        Assert.Equal("person", first["name"]!.GetValue<string>());
        Assert.True(first["strict"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(schema, first["schema"]));
        Assert.Equal("""{"format":{"type":"json_object"}}""", handler.Requests[1].Json["text"]!.ToJsonString());
    }

    [Fact]
    public async Task Reasoning_models_are_stateless_and_provider_options_are_merged()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(ReasoningResponse);
        var request = SimpleRequest("gpt-5.2") with
        {
            ReasoningEffort = "high",
            ResponseFormat = ResponseFormat.Json,
            ProviderOptions = (JsonObject)JsonNode.Parse("""
                {"openai": {
                  "reasoning": {"summary": "auto"},
                  "include": ["web_search_call.results", "reasoning.encrypted_content"],
                  "tools": [{"type": "web_search_preview"}],
                  "text": {"verbosity": "low"},
                  "parallel_tool_calls": false
                },
                "anthropic": {"ignored": true}}
                """)!,
        };

        await adapter.CompleteAsync(request);

        var body = handler.LastRequest.Json;
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("""["reasoning.encrypted_content","web_search_call.results"]""", body["include"]!.ToJsonString());
        Assert.Equal("""{"effort":"high","summary":"auto"}""", body["reasoning"]!.ToJsonString());
        Assert.Equal("""[{"type":"web_search_preview"}]""", body["tools"]!.ToJsonString());
        Assert.Equal("""{"format":{"type":"json_object"},"verbosity":"low"}""", body["text"]!.ToJsonString());
        Assert.False(body["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Null(body["ignored"]);

        // The request's provider options are never mutated or re-parented.
        Assert.Equal("auto", request.ProviderOptions!["openai"]!["reasoning"]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task Provider_options_can_override_store()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest("gpt-5.2") with
        {
            ProviderOptions = new JsonObject { ["openai"] = new JsonObject { ["store"] = true, ["previous_response_id"] = "resp_prev" } },
        });

        var body = handler.LastRequest.Json;
        Assert.True(body["store"]!.GetValue<bool>());
        Assert.Equal("resp_prev", body["previous_response_id"]!.GetValue<string>());
        Assert.Equal("""["reasoning.encrypted_content"]""", body["include"]!.ToJsonString());
    }

    [Theory]
    [InlineData("gpt-5.2", true)]
    [InlineData("gpt-5-mini", true)]
    [InlineData("o3-mini", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gpt-5.2-codex", true)]
    [InlineData("gpt-4.1", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("gpt-5-chat-latest", false)]
    public async Task Store_and_include_are_sent_only_for_reasoning_models(string model, bool reasoning)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest(model));

        var body = handler.LastRequest.Json;
        Assert.Equal(reasoning, body.ContainsKey("store"));
        Assert.Equal(reasoning, body.ContainsKey("include"));
    }

    [Fact]
    public async Task Reasoning_effort_is_dropped_with_warning_for_catalog_non_reasoning_models()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(SimpleRequest("gpt-4.1") with { ReasoningEffort = "low" });

        Assert.Null(handler.LastRequest.Json["reasoning"]);
        Assert.Contains(response.Warnings, w => w.Message.Contains("reasoning_effort", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parses_reasoning_text_tool_calls_usage_and_rate_limits()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(
            ReasoningResponse,
            HttpStatusCode.OK,
            ("x-ratelimit-limit-requests", "500"),
            ("x-ratelimit-remaining-requests", "499"),
            ("x-ratelimit-limit-tokens", "30000"),
            ("x-ratelimit-remaining-tokens", "29500"),
            ("x-ratelimit-reset-requests", "120ms"));

        var response = await adapter.CompleteAsync(SimpleRequest("gpt-5.2"));

        Assert.Equal("resp_67ccd2bed1ec8190b14f964abc054267", response.Id);
        Assert.Equal("gpt-5.2-2025-12-11", response.Model);
        Assert.Equal(3, response.Message.Content.Count);

        var thinking = response.Message.Content[0];
        Assert.Equal(ContentKind.Thinking, thinking.Kind);
        Assert.Equal("**Checking the weather**\n\nNeed to call get_weather.", thinking.Thinking!.Text);
        Assert.Equal("gAAAAABoIPOD-encrypted-blob", thinking.GetProviderMetadata("openai")!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("rs_6820f383d7c08191846711c5df8233bc", thinking.GetProviderMetadata("openai")!["item_id"]!.GetValue<string>());

        Assert.Equal("Let me check the weather.", response.Text);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_unLAR8MvFNptuiZK6K6HCy5k", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", call.Arguments["city"]!.GetValue<string>());
        Assert.IsType<JsonObject>(response.Message.Content[2].ToolCall!.Arguments);

        Assert.Equal(new FinishReason(FinishReason.ToolCallsReason, "completed"), response.FinishReason);
        Assert.Equal(328, response.Usage.InputTokens);
        Assert.Equal(152, response.Usage.OutputTokens);
        Assert.Equal(480, response.Usage.TotalTokens);
        Assert.Equal(64, response.Usage.ReasoningTokens);
        Assert.Equal(128, response.Usage.CacheReadTokens);
        Assert.Null(response.Usage.CacheWriteTokens);
        Assert.NotNull(response.Usage.Raw);
        Assert.NotNull(response.Raw);

        Assert.NotNull(response.RateLimit);
        Assert.Equal(499, response.RateLimit.RequestsRemaining);
        Assert.Equal(500, response.RateLimit.RequestsLimit);
        Assert.Equal(29500, response.RateLimit.TokensRemaining);
        Assert.Equal(30000, response.RateLimit.TokensLimit);
        Assert.NotNull(response.RateLimit.ResetAt);
    }

    [Fact]
    public async Task Reasoning_items_and_tool_calls_round_trip_statelessly()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(ReasoningResponse).EnqueueJson(TextResponse);
        var first = await adapter.CompleteAsync(new Request { Model = "gpt-5.2", Messages = [Message.User("Weather in Paris?")], Tools = [TestUtil.WeatherTool] });

        var history = new List<Message> { Message.User("Weather in Paris?"), first.Message, Message.ToolResult("call_unLAR8MvFNptuiZK6K6HCy5k", "18C") };
        await adapter.CompleteAsync(new Request { Model = "gpt-5.2", Messages = history, Tools = [TestUtil.WeatherTool] });

        var input = handler.LastRequest.Json["input"]!.AsArray();
        var expected = JsonNode.Parse("""
            [
              {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "Weather in Paris?"}]},
              {"type": "reasoning", "summary": [{"type": "summary_text", "text": "**Checking the weather**\n\nNeed to call get_weather."}],
               "encrypted_content": "gAAAAABoIPOD-encrypted-blob"},
              {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "Let me check the weather."}]},
              {"type": "function_call", "call_id": "call_unLAR8MvFNptuiZK6K6HCy5k", "name": "get_weather", "arguments": "{\"city\":\"Paris\",\"unit\":\"c\"}"},
              {"type": "function_call_output", "call_id": "call_unLAR8MvFNptuiZK6K6HCy5k", "output": "18C"}
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, input), input.ToJsonString());
    }

    [Fact]
    public async Task Thinking_without_encrypted_content_and_foreign_reasoning_are_not_replayed()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);
        var request = SimpleRequest("gpt-5.2") with
        {
            Messages =
            [
                Message.User("Hi"),
                Message.Assistant(ContentPart.ThinkingPart("anthropic thoughts", "sig"), ContentPart.RedactedThinkingPart("opaque"), ContentPart.TextPart("Hello")),
                Message.User("Again"),
            ],
        };

        await adapter.CompleteAsync(request);

        var types = handler.LastRequest.Json["input"]!.AsArray().Select(i => i!["type"]!.GetValue<string>()).ToArray();
        Assert.Equal(["message", "message", "message"], types);
    }

    [Fact]
    public async Task Refusal_maps_to_content_filter_and_incomplete_to_length()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""
            {"id":"r1","status":"completed","model":"gpt-4.1","output":[{"type":"message","id":"m","role":"assistant",
             "content":[{"type":"refusal","refusal":"I can't help with that."}]}],"usage":{"input_tokens":5,"output_tokens":6,"total_tokens":11}}
            """);
        handler.EnqueueJson("""
            {"id":"r2","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"model":"gpt-4.1",
             "output":[{"type":"message","id":"m","role":"assistant","content":[{"type":"output_text","text":"Partial"}]}],
             "usage":{"input_tokens":5,"output_tokens":16,"total_tokens":21}}
            """);

        var refused = await adapter.CompleteAsync(SimpleRequest());
        var truncated = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal("I can't help with that.", refused.Text);
        Assert.Equal(FinishReason.ContentFilterReason, refused.FinishReason.Reason);
        Assert.Equal(new FinishReason(FinishReason.LengthReason, "max_output_tokens"), truncated.FinishReason);
    }

    [Fact]
    public async Task Invalid_tool_arguments_are_kept_as_raw_string()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""
            {"id":"r","status":"completed","model":"gpt-4.1","output":[
              {"type":"function_call","id":"fc","call_id":"call_x","name":"search","arguments":"{\"query\": \"unterminated"}]}
            """);

        var response = await adapter.CompleteAsync(SimpleRequest() with { Tools = [TestUtil.SearchTool] });

        var data = response.Message.Content[0].ToolCall!;
        Assert.Equal("{\"query\": \"unterminated", data.Arguments!.GetValue<string>());
        var call = Assert.Single(response.ToolCalls);
        Assert.NotNull(call.ArgumentsError);
        Assert.Equal("{\"query\": \"unterminated", call.RawArguments);
    }

    [Fact]
    public async Task Images_documents_and_tool_result_images_are_translated()
    {
        var (adapter, handler) = Create(o => o with { BaseUrl = "https://proxy.example.com/openai/v1/" });
        handler.EnqueueJson(TextResponse);
        using var file = TestUtil.CreateFile(".png", TestUtil.PngBytes);

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.User(
                    ContentPart.ImagePart(ImageData.FromUrl("https://example.com/a.png")),
                    ContentPart.ImagePart(ImageData.FromBytes(TestUtil.PngBytes, "image/png")),
                    ContentPart.ImagePart(ImageData.FromUrl(file.Path)),
                    ContentPart.DocumentPart(new DocumentData { Data = TestUtil.PdfBytes, MediaType = "application/pdf", FileName = "report.pdf" })),
                Message.Assistant(ContentPart.ToolCallPart("call_s", "screenshot", new JsonObject())),
                Message.ToolResult("call_s", JsonValue.Create("captured"), imageData: TestUtil.PngBytes, imageMediaType: "image/png"),
            ],
        });

        Assert.Equal("https://proxy.example.com/openai/v1/responses", handler.LastRequest.Uri.ToString());
        var input = handler.LastRequest.Json["input"]!.AsArray();
        var content = input[0]!["content"]!.AsArray();
        Assert.Equal("https://example.com/a.png", content[0]!["image_url"]!.GetValue<string>());
        Assert.Equal("auto", content[0]!["detail"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + TestUtil.PngBase64, content[1]!["image_url"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + TestUtil.PngBase64, content[2]!["image_url"]!.GetValue<string>());
        Assert.Equal("input_file", content[3]!["type"]!.GetValue<string>());
        Assert.Equal("report.pdf", content[3]!["filename"]!.GetValue<string>());
        Assert.Equal("data:application/pdf;base64," + Convert.ToBase64String(TestUtil.PdfBytes), content[3]!["file_data"]!.GetValue<string>());

        Assert.Equal("function_call_output", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("captured", input[2]!["output"]!.GetValue<string>());
        var imageMessage = input[3]!;
        Assert.Equal("user", imageMessage["role"]!.GetValue<string>());
        Assert.Equal("input_image", imageMessage["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + TestUtil.PngBase64, imageMessage["content"]![1]!["image_url"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided","type":"invalid_request_error","code":"invalid_api_key"}}""", typeof(AuthenticationError))]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"message":"The model 'gpt-9' does not exist","type":"invalid_request_error","code":"model_not_found"}}""", typeof(NotFoundError))]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid value for 'input'","type":"invalid_request_error","code":null}}""", typeof(InvalidRequestError))]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""", typeof(QuotaExceededError))]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"message":"The server had an error","type":"server_error","code":null}}""", typeof(ServerError))]
    public async Task Http_errors_map_to_the_error_hierarchy(HttpStatusCode status, string body, Type expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(body, status);

        var error = await Assert.ThrowsAnyAsync<SdkError>(() => adapter.CompleteAsync(SimpleRequest()));

        Assert.IsType(expected, error);
        var providerError = Assert.IsType<ProviderError>(error, exactMatch: false);
        Assert.Equal("openai", providerError.Provider);
        Assert.Equal((int)status, providerError.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_error_carries_retry_after()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""{"error":{"message":"Rate limit reached","type":"requests","code":"rate_limit_exceeded"}}""", HttpStatusCode.TooManyRequests, ("Retry-After", "7"));

        var error = await Assert.ThrowsAsync<RateLimitError>(() => adapter.CompleteAsync(SimpleRequest()));

        Assert.Equal(7, error.RetryAfter);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Stream_emits_unified_events_and_finish_matches_complete()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse(ReasoningStream()).EnqueueJson(ReasoningResponse);
        var request = new Request { Model = "gpt-5.2", Messages = [Message.User("Weather in Paris?")], Tools = [TestUtil.WeatherTool] };

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(request));
        var completed = await adapter.CompleteAsync(request);

        Assert.Equal(
            [
                "stream_start",
                "reasoning_start", "reasoning_delta", "reasoning_delta", "reasoning_delta", "reasoning_end",
                "text_start", "text_delta", "text_delta", "text_delta", "text_end",
                "tool_call_start", "tool_call_delta", "tool_call_delta", "tool_call_end",
                "finish",
            ],
            TestUtil.Types(events));
        Assert.True(handler.Requests[0].Json["stream"]!.GetValue<bool>());
        Assert.Equal("text/event-stream", handler.Requests[0].Header("Accept"));

        var finish = events[^1];
        Assert.NotNull(finish.Response);
        Assert.Equal(TestUtil.TextDeltas(events), finish.Response.Text);
        Assert.Equal(TestUtil.ReasoningDeltas(events), finish.Response.Reasoning);
        Assert.Equal(finish.Response.Usage, finish.Usage);
        Assert.Equal(finish.Response.FinishReason, finish.FinishReason);
        TestUtil.AssertEquivalent(completed, finish.Response);

        var textIds = events.Where(e => e.Type == StreamEventType.TextDelta).Select(e => e.TextId).Distinct();
        Assert.Equal(["msg_1"], textIds);

        var start = events.Single(e => e.Type == StreamEventType.ToolCallStart).ToolCall!;
        Assert.Equal(("call_unLAR8MvFNptuiZK6K6HCy5k", "get_weather"), (start.Id, start.Name));
        Assert.Empty(start.Arguments);
        var deltas = string.Concat(events.Where(e => e.Type == StreamEventType.ToolCallDelta).Select(e => e.Delta));
        var end = events.Single(e => e.Type == StreamEventType.ToolCallEnd).ToolCall!;
        Assert.Equal(deltas, end.RawArguments);
        Assert.Equal("c", end.Arguments["unit"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stream_failed_and_error_events_become_error_events()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_1","status":"in_progress","output":[]}}

            event: response.failed
            data: {"type":"response.failed","response":{"id":"resp_1","status":"failed","error":{"code":"server_error","message":"The model failed to generate a response."},"output":[]}}

            """);
        handler.EnqueueSse("""
            event: error
            data: {"type":"error","code":"rate_limit_exceeded","message":"Rate limit reached for requests","param":null,"sequence_number":1}

            """);

        var failed = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));
        var errored = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "error"], TestUtil.Types(failed));
        Assert.IsType<ServerError>(failed[^1].Error);
        Assert.Equal(["stream_start", "error"], TestUtil.Types(errored));
        Assert.IsType<RateLimitError>(errored[^1].Error);
    }

    [Fact]
    public async Task Stream_http_error_throws_from_first_move_next()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""{"error":{"message":"Rate limit","type":"requests","code":"rate_limit_exceeded"}}""", HttpStatusCode.TooManyRequests, ("Retry-After", "2"));

        var stream = adapter.StreamAsync(SimpleRequest());
        await using var enumerator = stream.GetAsyncEnumerator();

        var error = await Assert.ThrowsAsync<RateLimitError>(async () => await enumerator.MoveNextAsync());
        Assert.Equal(2, error.RetryAfter);
    }

    [Fact]
    public async Task Stream_incomplete_finishes_with_length_and_items_fallback()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_9","role":"assistant","content":[]}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"msg_9","output_index":0,"content_index":0,"delta":"Partial ans"}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_9","role":"assistant","content":[{"type":"output_text","text":"Partial ans"}]}}

            event: response.incomplete
            data: {"type":"response.incomplete","response":{"id":"resp_9","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"model":"gpt-4.1","usage":{"input_tokens":3,"output_tokens":4,"total_tokens":7}}}

            """);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "text_start", "text_delta", "text_end", "finish"], TestUtil.Types(events));
        var response = events[^1].Response!;
        Assert.Equal("Partial ans", response.Text);
        Assert.Equal(new FinishReason(FinishReason.LengthReason, "max_output_tokens"), response.FinishReason);
        Assert.Equal(7, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task Stream_that_ends_without_completion_reports_stream_error()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"Hel"}

            """);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "text_start", "text_delta", "error"], TestUtil.Types(events));
        Assert.IsType<StreamError>(events[^1].Error);
    }

    [Fact]
    public async Task Unknown_stream_events_are_passed_through_as_provider_events()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            event: response.web_search_call.searching
            data: {"type":"response.web_search_call.searching","item_id":"ws_1","output_index":0}

            event: response.completed
            data: {"type":"response.completed","response":@RESPONSE@}

            """.Replace("@RESPONSE@", JsonNode.Parse(TextResponse)!.ToJsonString(), StringComparison.Ordinal));

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "provider_event", "finish"], TestUtil.Types(events));
        Assert.Equal("response.web_search_call.searching", events[1].Raw!["type"]!.GetValue<string>());
        Assert.Equal("Hello there!", events[^1].Response!.Text);
    }

    [Fact]
    public async Task Custom_tool_calls_round_trip_with_custom_outputs()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""
            {"id":"r","status":"completed","model":"gpt-5.2","output":[
              {"type":"custom_tool_call","id":"ctc_1","call_id":"call_patch","name":"apply_patch","input":"*** Begin Patch\n*** End Patch"}]}
            """);
        handler.EnqueueJson(TextResponse);

        var first = await adapter.CompleteAsync(SimpleRequest("gpt-5.2"));
        var call = first.Message.Content[0].ToolCall!;
        Assert.Equal("custom", call.Type);
        Assert.Equal("*** Begin Patch\n*** End Patch", call.Arguments!.GetValue<string>());
        Assert.Equal(new FinishReason(FinishReason.ToolCallsReason, "completed"), first.FinishReason);

        await adapter.CompleteAsync(SimpleRequest("gpt-5.2") with { Messages = [Message.User("Patch it"), first.Message, Message.ToolResult("call_patch", "Done!")] });

        var input = handler.LastRequest.Json["input"]!.AsArray();
        Assert.Equal("""{"type":"custom_tool_call","call_id":"call_patch","name":"apply_patch","input":"*** Begin Patch\n*** End Patch"}""", input[1]!.ToJsonString());
        Assert.Equal("""{"type":"custom_tool_call_output","call_id":"call_patch","output":"Done!"}""", input[2]!.ToJsonString());
    }

    [Fact]
    public async Task Cancellation_raises_abort_error()
    {
        var (adapter, handler) = Create();
        handler.EnqueueHang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<AbortError>(() => adapter.CompleteAsync(SimpleRequest(), cts.Token));
    }

    private static string ReasoningStream()
    {
        var completed = JsonNode.Parse(ReasoningResponse)!.AsObject();
        return """
            event: response.created
            data: {"type":"response.created","sequence_number":0,"response":{"id":"resp_67ccd2bed1ec8190b14f964abc054267","object":"response","status":"in_progress","model":"gpt-5.2-2025-12-11","output":[],"usage":null}}

            event: response.in_progress
            data: {"type":"response.in_progress","sequence_number":1,"response":{"id":"resp_67ccd2bed1ec8190b14f964abc054267","status":"in_progress","output":[]}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"type":"reasoning","id":"rs_6820f383d7c08191846711c5df8233bc","summary":[]}}

            event: response.reasoning_summary_part.added
            data: {"type":"response.reasoning_summary_part.added","sequence_number":3,"item_id":"rs_6820f383d7c08191846711c5df8233bc","output_index":0,"summary_index":0,"part":{"type":"summary_text","text":""}}

            event: response.reasoning_summary_text.delta
            data: {"type":"response.reasoning_summary_text.delta","sequence_number":4,"item_id":"rs_6820f383d7c08191846711c5df8233bc","output_index":0,"summary_index":0,"delta":"**Checking the weather**"}

            event: response.reasoning_summary_text.done
            data: {"type":"response.reasoning_summary_text.done","sequence_number":5,"item_id":"rs_6820f383d7c08191846711c5df8233bc","output_index":0,"summary_index":0,"text":"**Checking the weather**"}

            event: response.reasoning_summary_text.delta
            data: {"type":"response.reasoning_summary_text.delta","sequence_number":6,"item_id":"rs_6820f383d7c08191846711c5df8233bc","output_index":0,"summary_index":1,"delta":"Need to call "}

            event: response.reasoning_summary_text.delta
            data: {"type":"response.reasoning_summary_text.delta","sequence_number":7,"item_id":"rs_6820f383d7c08191846711c5df8233bc","output_index":0,"summary_index":1,"delta":"get_weather."}

            event: response.output_item.done
            data: {"type":"response.output_item.done","sequence_number":8,"output_index":0,"item":@REASONING_ITEM@}

            event: response.output_item.added
            data: {"type":"response.output_item.added","sequence_number":9,"output_index":1,"item":{"type":"message","id":"msg_1","status":"in_progress","role":"assistant","content":[]}}

            event: response.content_part.added
            data: {"type":"response.content_part.added","sequence_number":10,"item_id":"msg_1","output_index":1,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","sequence_number":11,"item_id":"msg_1","output_index":1,"content_index":0,"delta":"Let me "}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","sequence_number":12,"item_id":"msg_1","output_index":1,"content_index":0,"delta":"check the "}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","sequence_number":13,"item_id":"msg_1","output_index":1,"content_index":0,"delta":"weather."}

            event: response.output_text.done
            data: {"type":"response.output_text.done","sequence_number":14,"item_id":"msg_1","output_index":1,"content_index":0,"text":"Let me check the weather."}

            event: response.content_part.done
            data: {"type":"response.content_part.done","sequence_number":15,"item_id":"msg_1","output_index":1,"content_index":0,"part":{"type":"output_text","text":"Let me check the weather.","annotations":[]}}

            event: response.output_item.done
            data: {"type":"response.output_item.done","sequence_number":16,"output_index":1,"item":{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Let me check the weather.","annotations":[]}]}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","sequence_number":17,"output_index":2,"item":{"type":"function_call","id":"fc_67ca09c6bedc8190a7abfec07b1a1332","call_id":"call_unLAR8MvFNptuiZK6K6HCy5k","name":"get_weather","arguments":"","status":"in_progress"}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","sequence_number":18,"item_id":"fc_67ca09c6bedc8190a7abfec07b1a1332","output_index":2,"delta":"{\"city\":\"Paris\","}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","sequence_number":19,"item_id":"fc_67ca09c6bedc8190a7abfec07b1a1332","output_index":2,"delta":"\"unit\":\"c\"}"}

            event: response.function_call_arguments.done
            data: {"type":"response.function_call_arguments.done","sequence_number":20,"item_id":"fc_67ca09c6bedc8190a7abfec07b1a1332","output_index":2,"arguments":"{\"city\":\"Paris\",\"unit\":\"c\"}"}

            event: response.output_item.done
            data: {"type":"response.output_item.done","sequence_number":21,"output_index":2,"item":@CALL_ITEM@}

            event: response.completed
            data: {"type":"response.completed","sequence_number":22,"response":@RESPONSE@}

            """
            .Replace("@REASONING_ITEM@", completed["output"]![0]!.ToJsonString(), StringComparison.Ordinal)
            .Replace("@CALL_ITEM@", completed["output"]![2]!.ToJsonString(), StringComparison.Ordinal)
            .Replace("@RESPONSE@", completed.ToJsonString(), StringComparison.Ordinal);
    }
}

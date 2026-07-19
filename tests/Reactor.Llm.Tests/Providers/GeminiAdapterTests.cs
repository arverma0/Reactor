using System.Net;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers;

namespace Reactor.Llm.Tests.Providers;

public class GeminiAdapterTests
{
    private const string FunctionCallResponse = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {"text": "**Planning**\nI need the weather tool.", "thought": true},
                  {"text": "Let me look that up."},
                  {"functionCall": {"name": "get_weather", "args": {"city": "Paris", "unit": "c"}}, "thoughtSignature": "CiQB0e2Kb8bz+SIG-A"},
                  {"functionCall": {"name": "get_weather", "args": {"city": "London"}}}
                ],
                "role": "model"
              },
              "finishReason": "STOP",
              "index": 0
            }
          ],
          "usageMetadata": {
            "promptTokenCount": 120,
            "candidatesTokenCount": 40,
            "totalTokenCount": 230,
            "thoughtsTokenCount": 70,
            "cachedContentTokenCount": 64,
            "promptTokensDetails": [{"modality": "TEXT", "tokenCount": 120}]
          },
          "modelVersion": "gemini-3-pro-preview",
          "responseId": "mAitaLmkHPPlz7IPvtfUqQ4"
        }
        """;

    private const string FunctionCallStream = """
        data: {"candidates":[{"content":{"parts":[{"text":"**Planning**\n","thought":true}],"role":"model"},"index":0}],"usageMetadata":{"promptTokenCount":120,"totalTokenCount":120},"modelVersion":"gemini-3-pro-preview","responseId":"mAitaLmkHPPlz7IPvtfUqQ4"}

        data: {"candidates":[{"content":{"parts":[{"text":"I need the weather tool.","thought":true}],"role":"model"},"index":0}],"modelVersion":"gemini-3-pro-preview","responseId":"mAitaLmkHPPlz7IPvtfUqQ4"}

        data: {"candidates":[{"content":{"parts":[{"text":"Let me "}],"role":"model"},"index":0}],"modelVersion":"gemini-3-pro-preview","responseId":"mAitaLmkHPPlz7IPvtfUqQ4"}

        data: {"candidates":[{"content":{"parts":[{"text":"look that up."}],"role":"model"},"index":0}],"modelVersion":"gemini-3-pro-preview","responseId":"mAitaLmkHPPlz7IPvtfUqQ4"}

        data: {"candidates":[{"content":{"parts":[{"functionCall":{"name":"get_weather","args":{"city":"Paris","unit":"c"}},"thoughtSignature":"CiQB0e2Kb8bz+SIG-A"},{"functionCall":{"name":"get_weather","args":{"city":"London"}}}],"role":"model"},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":120,"candidatesTokenCount":40,"totalTokenCount":230,"thoughtsTokenCount":70,"cachedContentTokenCount":64},"modelVersion":"gemini-3-pro-preview","responseId":"mAitaLmkHPPlz7IPvtfUqQ4"}

        """;

    private const string TextResponse = """
        {"candidates":[{"content":{"parts":[{"text":"Hi!"}],"role":"model"},"finishReason":"STOP","index":0}],
         "usageMetadata":{"promptTokenCount":4,"candidatesTokenCount":2,"totalTokenCount":6},"modelVersion":"gemini-2.5-flash","responseId":"r-1"}
        """;

    private static (GeminiAdapter Adapter, FakeHttpHandler Handler) Create(Func<GeminiAdapterOptions, GeminiAdapterOptions>? configure = null)
    {
        var handler = new FakeHttpHandler();
        var options = new GeminiAdapterOptions { ApiKey = "gk-test", HttpClient = new HttpClient(handler) };
        return (new GeminiAdapter(configure?.Invoke(options) ?? options), handler);
    }

    private static Request SimpleRequest(string model = "gemini-2.5-flash") => new() { Model = model, Messages = [Message.User("Hi")] };

    private static ContentPart Signed(ContentPart part, string signature) => part with
    {
        ProviderMetadata = new JsonObject { ["gemini"] = new JsonObject { ["thoughtSignature"] = signature } },
    };

    [Fact]
    public async Task Complete_translates_system_roles_tool_results_and_generation_config()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(new Request
        {
            Model = "gemini-2.5-pro",
            Messages =
            [
                Message.System("Be brief."),
                Message.Developer("Use metric."),
                Message.User("Weather in Paris and London?"),
                Message.Assistant(
                    Signed(ContentPart.ToolCallPart("call_a", "get_weather", new JsonObject { ["city"] = "Paris" }), "sigA"),
                    ContentPart.ToolCallPart("call_b", "get_weather", JsonValue.Create("""{"city":"London"}"""))),
                Message.ToolResult("call_a", "18C"),
                Message.ToolResult("call_b", new JsonObject { ["temp"] = 12, ["sky"] = "cloudy" }),
                Message.User("Thanks"),
            ],
            Tools = [TestUtil.WeatherTool],
            Temperature = 0.4,
            TopP = 0.95,
            MaxTokens = 1024,
            StopSequences = ["END"],
        });

        var sent = handler.LastRequest;
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent", sent.Uri.ToString());
        Assert.Equal("gk-test", sent.Header("x-goog-api-key"));
        Assert.DoesNotContain("key=", sent.Uri.Query, StringComparison.Ordinal);

        var expected = JsonNode.Parse("""
            {
              "contents": [
                {"role": "user", "parts": [{"text": "Weather in Paris and London?"}]},
                {"role": "model", "parts": [
                  {"functionCall": {"name": "get_weather", "args": {"city": "Paris"}}, "thoughtSignature": "sigA"},
                  {"functionCall": {"name": "get_weather", "args": {"city": "London"}}}]},
                {"role": "user", "parts": [
                  {"functionResponse": {"name": "get_weather", "response": {"result": "18C"}}},
                  {"functionResponse": {"name": "get_weather", "response": {"temp": 12, "sky": "cloudy"}}},
                  {"text": "Thanks"}]}
              ],
              "systemInstruction": {"parts": [{"text": "Be brief.\n\nUse metric."}]},
              "tools": [{"functionDeclarations": [{"name": "get_weather", "description": "Get the weather for a city",
                "parameters": {"type": "object", "properties": {"city": {"type": "string", "description": "City name"},
                               "unit": {"type": "string", "enum": ["c", "f"]}}, "required": ["city"]}}]}],
              "toolConfig": {"functionCallingConfig": {"mode": "AUTO"}},
              "generationConfig": {"temperature": 0.4, "topP": 0.95, "maxOutputTokens": 1024, "stopSequences": ["END"]}
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, sent.Json), sent.Body);
        Assert.Equal("Hi!", response.Text);
        Assert.Equal("r-1", response.Id);
        Assert.Equal("gemini-2.5-flash", response.Model);
        Assert.Equal("gemini", response.Provider);
    }

    [Theory]
    [InlineData("auto", null, """{"mode":"AUTO"}""")]
    [InlineData("none", null, """{"mode":"NONE"}""")]
    [InlineData("required", null, """{"mode":"ANY"}""")]
    [InlineData("named", "get_weather", """{"mode":"ANY","allowedFunctionNames":["get_weather"]}""")]
    public async Task Tool_choice_modes_are_translated(string mode, string? toolName, string expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest() with { Tools = [TestUtil.WeatherTool], ToolChoice = new ToolChoice(mode, toolName) });

        Assert.Equal(expected, handler.LastRequest.Json["toolConfig"]!["functionCallingConfig"]!.ToJsonString());
        Assert.NotNull(handler.LastRequest.Json["tools"]);
    }

    [Theory]
    [InlineData("gemini-3-pro-preview", "low", """{"thinkingLevel":"low","includeThoughts":true}""")]
    [InlineData("gemini-3-flash-preview", "medium", """{"thinkingLevel":"medium","includeThoughts":true}""")]
    [InlineData("gemini-3.1-pro-preview", "high", """{"thinkingLevel":"high","includeThoughts":true}""")]
    [InlineData("models/gemini-3-pro-preview", "xhigh", """{"thinkingLevel":"high","includeThoughts":true}""")]
    [InlineData("gemini-2.5-flash", "low", """{"thinkingBudget":1024,"includeThoughts":true}""")]
    [InlineData("gemini-2.5-pro", "medium", """{"thinkingBudget":8192,"includeThoughts":true}""")]
    [InlineData("gemini-2.5-pro", "max", """{"thinkingBudget":24576,"includeThoughts":true}""")]
    public async Task Reasoning_effort_maps_to_thinking_config(string model, string effort, string expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(SimpleRequest(model) with { ReasoningEffort = effort });

        Assert.Equal(expected, handler.LastRequest.Json["generationConfig"]!["thinkingConfig"]!.ToJsonString());
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task Reasoning_effort_on_non_thinking_model_is_omitted_with_warning()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(SimpleRequest("gemini-2.0-flash") with { ReasoningEffort = "high" });

        Assert.Null(handler.LastRequest.Json["generationConfig"]);
        Assert.Contains(response.Warnings, w => w.Message.Contains("reasoning_effort", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Response_formats_set_mime_type_and_sanitized_schema()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse).EnqueueJson(TextResponse);
        var schema = (JsonObject)JsonNode.Parse("""
            {"$schema":"http://json-schema.org/draft-07/schema#","type":"object","additionalProperties":false,
             "properties":{"name":{"type":"string"},"age":{"type":["integer","null"]}},"required":["name","age"]}
            """)!;

        await adapter.CompleteAsync(SimpleRequest() with { ResponseFormat = ResponseFormat.ForSchema(schema) });
        await adapter.CompleteAsync(SimpleRequest() with { ResponseFormat = ResponseFormat.Json });

        Assert.Equal(
            """{"responseMimeType":"application/json","responseSchema":{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer","nullable":true}},"required":["name","age"]}}""",
            handler.Requests[0].Json["generationConfig"]!.ToJsonString());
        Assert.Equal("""{"responseMimeType":"application/json"}""", handler.Requests[1].Json["generationConfig"]!.ToJsonString());
    }

    [Fact]
    public async Task Provider_options_are_merged_and_extra_tools_appended()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Temperature = 0.1,
            Tools = [TestUtil.WeatherTool],
            ProviderOptions = (JsonObject)JsonNode.Parse("""
                {"gemini": {
                  "safetySettings": [{"category": "HARM_CATEGORY_HARASSMENT", "threshold": "BLOCK_NONE"}],
                  "cachedContent": "cachedContents/abc123",
                  "generationConfig": {"candidateCount": 1, "temperature": 0.9},
                  "tools": [{"googleSearch": {}}],
                  "thinkingConfig": {"thinkingBudget": 0}
                }}
                """)!,
        });

        var body = handler.LastRequest.Json;
        Assert.Equal("cachedContents/abc123", body["cachedContent"]!.GetValue<string>());
        Assert.Equal("BLOCK_NONE", body["safetySettings"]![0]!["threshold"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"temperature":0.9,"candidateCount":1,"thinkingConfig":{"thinkingBudget":0}}"""),
            body["generationConfig"]), body["generationConfig"]!.ToJsonString());
        var tools = body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.NotNull(tools[0]!["functionDeclarations"]);
        Assert.Equal("""{"googleSearch":{}}""", tools[1]!.ToJsonString());
    }

    [Fact]
    public async Task Parses_thoughts_text_function_calls_signatures_and_usage()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(FunctionCallResponse);

        var response = await adapter.CompleteAsync(SimpleRequest("gemini-3-pro-preview"));

        Assert.Equal("mAitaLmkHPPlz7IPvtfUqQ4", response.Id);
        Assert.Equal("gemini-3-pro-preview", response.Model);
        var parts = response.Message.Content;
        Assert.Equal([ContentKind.Thinking, ContentKind.Text, ContentKind.ToolCall, ContentKind.ToolCall], parts.Select(p => p.Kind).ToArray());
        Assert.Equal("**Planning**\nI need the weather tool.", response.Reasoning);
        Assert.Equal("Let me look that up.", response.Text);
        Assert.Equal("CiQB0e2Kb8bz+SIG-A", parts[2].GetProviderMetadata("gemini")!["thoughtSignature"]!.GetValue<string>());
        Assert.Null(parts[3].ProviderMetadata);

        var calls = response.ToolCalls;
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.StartsWith("call_", c.Id, StringComparison.Ordinal));
        Assert.NotEqual(calls[0].Id, calls[1].Id);
        Assert.Equal("London", calls[1].Arguments["city"]!.GetValue<string>());
        Assert.Equal(new FinishReason(FinishReason.ToolCallsReason, "STOP"), response.FinishReason);

        Assert.Equal(120, response.Usage.InputTokens);
        Assert.Equal(40, response.Usage.OutputTokens);
        Assert.Equal(230, response.Usage.TotalTokens);
        Assert.Equal(70, response.Usage.ReasoningTokens);
        Assert.Equal(64, response.Usage.CacheReadTokens);
    }

    [Fact]
    public async Task Provider_assigned_function_call_ids_are_kept()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""
            {"candidates":[{"content":{"parts":[{"functionCall":{"id":"fc-123","name":"get_weather","args":{"city":"Rome"}}}],"role":"model"},"finishReason":"STOP"}]}
            """);

        var response = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal("fc-123", Assert.Single(response.ToolCalls).Id);
    }

    [Fact]
    public async Task Thought_signatures_and_function_names_round_trip_without_ids_or_thoughts()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(FunctionCallResponse).EnqueueJson(TextResponse);
        var first = await adapter.CompleteAsync(SimpleRequest("gemini-3-pro-preview") with { Tools = [TestUtil.WeatherTool] });
        var calls = first.ToolCalls;

        await adapter.CompleteAsync(SimpleRequest("gemini-3-pro-preview") with
        {
            Messages =
            [
                Message.User("Weather?"),
                first.Message,
                Message.ToolResult(calls[0].Id, "18C"),
                Message.ToolResult(calls[1].Id, "Service unavailable", isError: true),
            ],
            Tools = [TestUtil.WeatherTool],
        });

        var contents = handler.LastRequest.Json["contents"]!.AsArray();
        var expected = JsonNode.Parse("""
            [
              {"role": "user", "parts": [{"text": "Weather?"}]},
              {"role": "model", "parts": [
                {"text": "Let me look that up."},
                {"functionCall": {"name": "get_weather", "args": {"city": "Paris", "unit": "c"}}, "thoughtSignature": "CiQB0e2Kb8bz+SIG-A"},
                {"functionCall": {"name": "get_weather", "args": {"city": "London"}}}]},
              {"role": "user", "parts": [
                {"functionResponse": {"name": "get_weather", "response": {"result": "18C"}}},
                {"functionResponse": {"name": "get_weather", "response": {"error": "Service unavailable"}}}]}
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, contents), contents.ToJsonString());
    }

    [Fact]
    public async Task Unsigned_function_calls_get_the_skip_validator_signature_on_gemini_3()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse).EnqueueJson(TextResponse);
        List<Message> history =
        [
            Message.User("Weather?"),
            Message.Assistant(ContentPart.ToolCallPart("toolu_1", "get_weather", new JsonObject { ["city"] = "Paris" }), ContentPart.ToolCallPart("toolu_2", "get_weather", new JsonObject { ["city"] = "Oslo" })),
            Message.ToolResult("toolu_1", "18C"),
            Message.ToolResult("toolu_2", "-1C"),
        ];

        await adapter.CompleteAsync(SimpleRequest("gemini-3-pro-preview") with { Messages = history });
        await adapter.CompleteAsync(SimpleRequest("gemini-2.5-pro") with { Messages = history });

        var gemini3 = handler.Requests[0].Json["contents"]![1]!["parts"]!.AsArray();
        Assert.Equal("skip_thought_signature_validator", gemini3[0]!["thoughtSignature"]!.GetValue<string>());
        Assert.Null(gemini3[1]!["thoughtSignature"]);
        Assert.DoesNotContain("thoughtSignature", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Function_name_falls_back_to_message_name_then_call_id_with_warning()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.User("go"),
                Message.Assistant("Working on it."),
                Message.ToolResult("orphan_1", new JsonArray(1, 2)) with { Name = "list_numbers" },
                Message.ToolResult("orphan_2", "x"),
            ],
        });

        var parts = handler.LastRequest.Json["contents"]![2]!["parts"]!.AsArray();
        Assert.Equal("""{"functionResponse":{"name":"list_numbers","response":{"result":[1,2]}}}""", parts[0]!.ToJsonString());
        Assert.Equal("orphan_2", parts[1]!["functionResponse"]!["name"]!.GetValue<string>());
        Assert.Contains(response.Warnings, w => w.Code == "unknown_tool_call");
    }

    [Theory]
    [InlineData("STOP", "stop")]
    [InlineData("MAX_TOKENS", "length")]
    [InlineData("SAFETY", "content_filter")]
    [InlineData("RECITATION", "content_filter")]
    [InlineData("PROHIBITED_CONTENT", "content_filter")]
    [InlineData("MALFORMED_FUNCTION_CALL", "error")]
    [InlineData("OTHER", "other")]
    public async Task Finish_reasons_are_mapped(string finishReason, string expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse.Replace("\"STOP\"", "\"" + finishReason + "\"", StringComparison.Ordinal));

        var response = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal(new FinishReason(expected, finishReason), response.FinishReason);
    }

    [Fact]
    public async Task Blocked_prompt_maps_to_content_filter()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""{"promptFeedback":{"blockReason":"SAFETY","safetyRatings":[]},"usageMetadata":{"promptTokenCount":8,"totalTokenCount":8},"modelVersion":"gemini-2.5-flash"}""");

        var response = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal(new FinishReason(FinishReason.ContentFilterReason, "SAFETY"), response.FinishReason);
        Assert.Empty(response.Message.Content);
        Assert.Contains(response.Warnings, w => w.Code == "prompt_blocked");
    }

    [Fact]
    public async Task Stream_emits_unified_events_and_finish_matches_complete()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse(FunctionCallStream).EnqueueJson(FunctionCallResponse);
        var request = SimpleRequest("gemini-3-pro-preview") with { Tools = [TestUtil.WeatherTool] };

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(request));
        var completed = await adapter.CompleteAsync(request);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-pro-preview:streamGenerateContent?alt=sse",
            handler.Requests[0].Uri.ToString());
        Assert.Equal(
            [
                "stream_start",
                "reasoning_start", "reasoning_delta", "reasoning_delta", "reasoning_end",
                "text_start", "text_delta", "text_delta", "text_end",
                "tool_call_start", "tool_call_end", "tool_call_start", "tool_call_end",
                "finish",
            ],
            TestUtil.Types(events));

        var finish = events[^1].Response!;
        Assert.Equal(TestUtil.TextDeltas(events), finish.Text);
        Assert.Equal(TestUtil.ReasoningDeltas(events), finish.Reasoning);
        TestUtil.AssertEquivalent(completed, finish);

        var ends = events.Where(e => e.Type == StreamEventType.ToolCallEnd).Select(e => e.ToolCall!).ToList();
        Assert.Equal(ends.Select(c => c.Id), finish.ToolCalls.Select(c => c.Id));
        Assert.Equal(
            events.Where(e => e.Type == StreamEventType.ToolCallStart).Select(e => e.ToolCall!.Id),
            ends.Select(c => c.Id));
        Assert.Equal("Paris", ends[0].Arguments["city"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stream_keeps_signature_from_trailing_empty_text_part()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            data: {"candidates":[{"content":{"parts":[{"text":"Hello"}],"role":"model"},"index":0}],"modelVersion":"gemini-3-flash-preview"}

            data: {"candidates":[{"content":{"parts":[{"text":" world"}],"role":"model"},"index":0}]}

            data: {"candidates":[{"content":{"parts":[{"text":"","thoughtSignature":"SIG_TEXT"}],"role":"model"},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}

            """);
        handler.EnqueueJson(TextResponse);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest("gemini-3-flash-preview")));

        Assert.Equal(["stream_start", "text_start", "text_delta", "text_delta", "text_end", "finish"], TestUtil.Types(events));
        var response = events[^1].Response!;
        var part = Assert.Single(response.Message.Content);
        Assert.Equal("Hello world", part.Text);
        Assert.Equal("SIG_TEXT", part.GetProviderMetadata("gemini")!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal(5, response.Usage.TotalTokens);
        Assert.Equal(new FinishReason(FinishReason.StopReason, "STOP"), response.FinishReason);

        await adapter.CompleteAsync(SimpleRequest("gemini-3-flash-preview") with { Messages = [Message.User("Hi"), response.Message, Message.User("More")] });
        Assert.Equal("""{"text":"Hello world","thoughtSignature":"SIG_TEXT"}""", handler.LastRequest.Json["contents"]![1]!["parts"]![0]!.ToJsonString());
    }

    [Fact]
    public async Task Stream_error_chunk_and_truncated_stream_produce_error_events()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            data: {"candidates":[{"content":{"parts":[{"text":"Par"}],"role":"model"},"index":0}]}

            data: {"error":{"code":503,"message":"The model is overloaded. Please try again later.","status":"UNAVAILABLE"}}

            """);
        handler.EnqueueSse("""
            data: {"candidates":[{"content":{"parts":[{"text":"Par"}],"role":"model"},"index":0}]}

            """);

        var errored = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));
        var truncated = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "text_start", "text_delta", "error"], TestUtil.Types(errored));
        var error = Assert.IsType<ServerError>(errored[^1].Error);
        Assert.Equal("UNAVAILABLE", error.ErrorCode);
        Assert.Equal(["stream_start", "text_start", "text_delta", "error"], TestUtil.Types(truncated));
        Assert.IsType<StreamError>(truncated[^1].Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_ARGUMENT", "Request contains an invalid argument.", typeof(InvalidRequestError))]
    [InlineData(HttpStatusCode.Forbidden, "PERMISSION_DENIED", "Permission denied on resource project.", typeof(AccessDeniedError))]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", "models/gemini-9 is not found for API version v1beta.", typeof(NotFoundError))]
    [InlineData(HttpStatusCode.InternalServerError, "INTERNAL", "An internal error has occurred.", typeof(ServerError))]
    [InlineData(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE", "The model is overloaded.", typeof(ServerError))]
    public async Task Http_errors_map_via_grpc_status(HttpStatusCode status, string grpcStatus, string message, Type expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson($$$"""{"error":{"code":{{{(int)status}}},"message":"{{{message}}}","status":"{{{grpcStatus}}}"}}""", status);

        var error = await Assert.ThrowsAnyAsync<SdkError>(() => adapter.CompleteAsync(SimpleRequest()));

        Assert.IsType(expected, error);
        Assert.Equal(grpcStatus, Assert.IsType<ProviderError>(error, exactMatch: false).ErrorCode);
    }

    [Fact]
    public async Task Resource_exhausted_maps_to_rate_limit_with_retry_delay()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""
            {"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota).","status":"RESOURCE_EXHAUSTED",
             "details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"17s"}]}}
            """, HttpStatusCode.TooManyRequests);

        var error = await Assert.ThrowsAsync<RateLimitError>(() => adapter.CompleteAsync(SimpleRequest()));

        Assert.Equal(17, error.RetryAfter);
        Assert.Equal("RESOURCE_EXHAUSTED", error.ErrorCode);
    }

    [Fact]
    public async Task Invalid_api_key_maps_to_authentication_error_for_complete_and_stream()
    {
        var (adapter, handler) = Create();
        const string body = """
            {"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT",
             "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID","domain":"googleapis.com"}]}}
            """;
        handler.EnqueueJson(body, HttpStatusCode.BadRequest).EnqueueJson(body, HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<AuthenticationError>(() => adapter.CompleteAsync(SimpleRequest()));
        await Assert.ThrowsAsync<AuthenticationError>(() => TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest())));
    }

    [Fact]
    public async Task Images_audio_and_documents_are_inlined_or_referenced()
    {
        var (adapter, handler) = Create(o => o with { BaseUrl = "https://gemini-proxy.example.com/", ApiVersion = "v1" });
        handler.EnqueueJson(TextResponse);
        using var file = TestUtil.CreateFile(".webp", TestUtil.PngBytes);

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.User(
                    ContentPart.TextPart("Describe these"),
                    ContentPart.ImagePart(ImageData.FromUrl("https://example.com/photo.jpg")),
                    ContentPart.ImagePart(ImageData.FromBytes(TestUtil.PngBytes)),
                    ContentPart.ImagePart(ImageData.FromUrl(file.Path)),
                    ContentPart.AudioPart(new AudioData { Data = [1, 2, 3], MediaType = "audio/mpeg" }),
                    ContentPart.DocumentPart(new DocumentData { Data = TestUtil.PdfBytes, MediaType = "application/pdf" }),
                    ContentPart.DocumentPart(new DocumentData { Url = "https://example.com/paper.pdf" })),
            ],
        });

        Assert.Equal("https://gemini-proxy.example.com/v1/models/gemini-2.5-flash:generateContent", handler.LastRequest.Uri.ToString());
        var expected = JsonNode.Parse($$$"""
            [
              {"text": "Describe these"},
              {"fileData": {"mimeType": "image/jpeg", "fileUri": "https://example.com/photo.jpg"}},
              {"inlineData": {"mimeType": "image/png", "data": "{{{TestUtil.PngBase64}}}"}},
              {"inlineData": {"mimeType": "image/webp", "data": "{{{TestUtil.PngBase64}}}"}},
              {"inlineData": {"mimeType": "audio/mpeg", "data": "AQID"}},
              {"inlineData": {"mimeType": "application/pdf", "data": "{{{Convert.ToBase64String(TestUtil.PdfBytes)}}}"}},
              {"fileData": {"mimeType": "application/pdf", "fileUri": "https://example.com/paper.pdf"}}
            ]
            """);
        var parts = handler.LastRequest.Json["contents"]![0]!["parts"]!;
        Assert.True(JsonNode.DeepEquals(expected, parts), parts.ToJsonString());
    }

    [Fact]
    public async Task Tool_result_images_follow_the_function_responses()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.User("Take a screenshot"),
                Message.Assistant(ContentPart.ToolCallPart("call_1", "screenshot", new JsonObject())),
                Message.ToolResult("call_1", JsonValue.Create("done"), imageData: TestUtil.PngBytes, imageMediaType: "image/png"),
            ],
        });

        Assert.Equal(
            """[{"functionResponse":{"name":"screenshot","response":{"result":"done"}}},{"inlineData":{"mimeType":"image/png","data":"@DATA@"}}]"""
                .Replace("@DATA@", TestUtil.PngBase64, StringComparison.Ordinal),
            handler.LastRequest.Json["contents"]![2]!["parts"]!.ToJsonString());
    }

    [Fact]
    public async Task Empty_object_parameters_are_omitted_from_function_declarations()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);
        var noArgs = new ToolDefinition("get_time", "Current time", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

        await adapter.CompleteAsync(SimpleRequest() with { Tools = [noArgs] });

        Assert.Equal(
            """{"name":"get_time","description":"Current time"}""",
            handler.LastRequest.Json["tools"]![0]!["functionDeclarations"]![0]!.ToJsonString());
    }
}

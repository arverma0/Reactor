using System.Net;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers;

namespace Reactor.Llm.Tests.Providers;

public class AnthropicAdapterTests
{
    private const string Signature = "EqQBCgIYAhIM1gbcDa9GJwZA2b3hGgxBdjrkzLoky3dl1pkiMOYds";
    private const string RedactedData = "EmwKAhgBEgy3va3pzix/LafPsn4aDFIT2Xlxh0L5L8rLVyIwxtE3rAFBa8cr3qpP";
    private const string ThinkingText = "The user wants the weather. I should call the tool.";

    private const string ToolUseResponse = """
        {
          "id": "msg_01XFDUDYJgAACzvnptvVoYEL",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-4-6",
          "content": [
            {"type": "thinking", "thinking": "The user wants the weather. I should call the tool.", "signature": "EqQBCgIYAhIM1gbcDa9GJwZA2b3hGgxBdjrkzLoky3dl1pkiMOYds"},
            {"type": "redacted_thinking", "data": "EmwKAhgBEgy3va3pzix/LafPsn4aDFIT2Xlxh0L5L8rLVyIwxtE3rAFBa8cr3qpP"},
            {"type": "text", "text": "I'll check the weather in Paris."},
            {"type": "tool_use", "id": "toolu_01A09q90qw90lq917835lq9", "name": "get_weather", "input": {"city": "Paris", "unit": "c"}}
          ],
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": {"input_tokens": 2095, "cache_creation_input_tokens": 1024, "cache_read_input_tokens": 4096, "output_tokens": 503}
        }
        """;

    private const string ToolUseStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01XFDUDYJgAACzvnptvVoYEL","type":"message","role":"assistant","model":"claude-opus-4-6","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2095,"cache_creation_input_tokens":1024,"cache_read_input_tokens":4096,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The user wants the weather. "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"I should call the tool."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EqQBCgIYAhIM1gbcDa9GJwZA2b3hGgxBdjrkzLoky3dl1pkiMOYds"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"EmwKAhgBEgy3va3pzix/LafPsn4aDFIT2Xlxh0L5L8rLVyIwxtE3rAFBa8cr3qpP"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}

        event: ping
        data: {"type": "ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"I'll check "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"the weather in Paris."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: content_block_start
        data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_01A09q90qw90lq917835lq9","name":"get_weather","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{\"city\": \"Par"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"is\", \"unit\": \"c\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":3}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":503}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private const string TextResponse = """
        {"id":"msg_text","type":"message","role":"assistant","model":"claude-sonnet-4-5-20250929",
         "content":[{"type":"text","text":"Hello!"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":10,"output_tokens":3}}
        """;

    private static (AnthropicAdapter Adapter, FakeHttpHandler Handler) Create(Func<AnthropicAdapterOptions, AnthropicAdapterOptions>? configure = null)
    {
        var handler = new FakeHttpHandler();
        var options = new AnthropicAdapterOptions { ApiKey = "sk-ant-test", HttpClient = new HttpClient(handler) };
        return (new AnthropicAdapter(configure?.Invoke(options) ?? options), handler);
    }

    private static Request SimpleRequest() => new() { Model = "claude-sonnet-4-5", Messages = [Message.User("Hi")] };

    private static JsonObject Ephemeral => new() { ["type"] = "ephemeral" };

    [Fact]
    public async Task Complete_translates_system_alternation_tools_and_parameters()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(new Request
        {
            Model = "claude-sonnet-4-5",
            Messages =
            [
                Message.System("You are a helpful assistant."),
                Message.Developer("Prefer metric units."),
                Message.User("What's the weather?"),
                Message.User(ContentPart.ImagePart(ImageData.FromUrl("https://example.com/sky.png"))),
                Message.Assistant(ContentPart.TextPart(""), ContentPart.ToolCallPart("toolu_1", "get_weather", new JsonObject { ["city"] = "Paris", ["days"] = 2 })),
                Message.ToolResult("toolu_1", "18C"),
                Message.User("Thanks! And tomorrow?"),
                Message.Assistant("Sunny."),
                Message.User("Great"),
            ],
            Tools = [TestUtil.WeatherTool],
            Temperature = 0.5,
            TopP = 0.8,
            StopSequences = ["###"],
            Metadata = new Dictionary<string, string> { ["user_id"] = "u-42", ["trace"] = "t1" },
        });

        var sent = handler.LastRequest;
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri.ToString());
        Assert.Equal("sk-ant-test", sent.Header("x-api-key"));
        Assert.Equal("2023-06-01", sent.Header("anthropic-version"));
        Assert.Equal("prompt-caching-2024-07-31", sent.Header("anthropic-beta"));
        Assert.Null(sent.Header("Authorization"));

        var expected = JsonNode.Parse("""
            {
              "model": "claude-sonnet-4-5",
              "max_tokens": 4096,
              "system": [
                {"type": "text", "text": "You are a helpful assistant."},
                {"type": "text", "text": "Prefer metric units.", "cache_control": {"type": "ephemeral"}}
              ],
              "messages": [
                {"role": "user", "content": [
                  {"type": "text", "text": "What's the weather?"},
                  {"type": "image", "source": {"type": "url", "url": "https://example.com/sky.png"}}]},
                {"role": "assistant", "content": [
                  {"type": "tool_use", "id": "toolu_1", "name": "get_weather", "input": {"city": "Paris", "days": 2}}]},
                {"role": "user", "content": [
                  {"type": "tool_result", "tool_use_id": "toolu_1", "content": "18C", "is_error": false},
                  {"type": "text", "text": "Thanks! And tomorrow?"}]},
                {"role": "assistant", "content": [{"type": "text", "text": "Sunny."}]},
                {"role": "user", "content": [{"type": "text", "text": "Great", "cache_control": {"type": "ephemeral"}}]}
              ],
              "tools": [{"name": "get_weather", "description": "Get the weather for a city",
                         "input_schema": {"type":"object","properties":{"city":{"type":"string","description":"City name"},"unit":{"type":"string","enum":["c","f"]}},"required":["city"]},
                         "cache_control": {"type": "ephemeral"}}],
              "tool_choice": {"type": "auto"},
              "temperature": 0.5,
              "top_p": 0.8,
              "stop_sequences": ["###"],
              "metadata": {"user_id": "u-42"}
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, sent.Json), sent.Body);
        Assert.Contains(response.Warnings, w => w.Message.Contains("trace", StringComparison.Ordinal));
        Assert.Equal("Hello!", response.Text);
        Assert.Equal(new FinishReason(FinishReason.StopReason, "end_turn"), response.FinishReason);
    }

    [Fact]
    public async Task Conversation_starting_with_assistant_gets_a_user_turn_and_tool_results_lead_user_turns()
    {
        var (adapter, handler) = Create(o => o with { AutoCache = false });
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.Assistant(ContentPart.ToolCallPart("toolu_a", "get_weather", new JsonObject { ["city"] = "Oslo" }), ContentPart.ToolCallPart("toolu_b", "get_weather", new JsonObject { ["city"] = "Rome" })),
                Message.User("(note)"),
                Message.ToolResult("toolu_a", "-2C"),
                new Message { Role = Role.Tool, ToolCallId = "toolu_b", Content = [ContentPart.TextPart("Tool failed")] },
                Message.ToolResult("toolu_c", JsonValue.Create(""), isError: true),
            ],
            Tools = [TestUtil.WeatherTool],
        });

        var messages = handler.LastRequest.Json["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("""{"role":"user","content":[{"type":"text","text":"Continue."}]}""", messages[0]!.ToJsonString());
        var userTurn = messages[2]!["content"]!.AsArray();
        Assert.Equal(["tool_result", "tool_result", "tool_result", "text"], userTurn.Select(b => b!["type"]!.GetValue<string>()).ToArray());
        Assert.Equal("Tool failed", userTurn[1]!["content"]!.GetValue<string>());
        Assert.True(userTurn[2]!["is_error"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(userTurn[2]!["content"]!.GetValue<string>()));
        Assert.Null(handler.LastRequest.Header("anthropic-beta"));
    }

    [Theory]
    [InlineData("auto", null, false, """{"type":"auto"}""")]
    [InlineData("required", null, false, """{"type":"any"}""")]
    [InlineData("named", "get_weather", false, """{"type":"tool","name":"get_weather"}""")]
    [InlineData("none", null, false, null)]
    [InlineData("none", null, true, """{"type":"none"}""")]
    public async Task Tool_choice_modes_are_translated(string mode, string? toolName, bool withToolHistory, string? expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);
        List<Message> messages = withToolHistory
            ? [Message.User("Weather?"), Message.Assistant(ContentPart.ToolCallPart("toolu_1", "get_weather", new JsonObject { ["city"] = "Paris" })), Message.ToolResult("toolu_1", "18C")]
            : [Message.User("Weather?")];

        await adapter.CompleteAsync(SimpleRequest() with { Messages = messages, Tools = [TestUtil.WeatherTool], ToolChoice = new ToolChoice(mode, toolName) });

        var body = handler.LastRequest.Json;
        if (expected is null)
        {
            Assert.False(body.ContainsKey("tools"));
            Assert.False(body.ContainsKey("tool_choice"));
        }
        else
        {
            Assert.Equal(expected, body["tool_choice"]!.ToJsonString());
            Assert.Equal("get_weather", body["tools"]![0]!["name"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Explicit_cache_breakpoints_are_honored_and_total_never_exceeds_four()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);
        static ContentPart Cached(string text) => ContentPart.TextPart(text) with
        {
            ProviderMetadata = new JsonObject { ["anthropic"] = new JsonObject { ["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = "1h" } } },
        };

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.System("system prompt"),
                Message.User(Cached("document one")),
                Message.Assistant("ok"),
                Message.User(Cached("document two")),
                Message.Assistant("ok"),
                Message.User(Cached("question")),
            ],
            Tools = [TestUtil.WeatherTool, TestUtil.SearchTool],
        });

        var body = handler.LastRequest.Json;
        var marked = new List<string>();
        foreach (var tool in body["tools"]!.AsArray())
        {
            if (tool!["cache_control"] is not null)
            {
                marked.Add("tool:" + tool["name"]!.GetValue<string>());
            }
        }

        foreach (var block in body["system"]!.AsArray())
        {
            if (block!["cache_control"] is not null)
            {
                marked.Add("system");
            }
        }

        foreach (var message in body["messages"]!.AsArray())
        {
            foreach (var block in message!["content"]!.AsArray())
            {
                if (block!["cache_control"] is not null)
                {
                    marked.Add("text:" + block["text"]!.GetValue<string>());
                }
            }
        }

        // Three explicit breakpoints, then the automatic heuristic fills the last slot (tools come first).
        Assert.Equal(["tool:search", "text:document one", "text:document two", "text:question"], marked);
        Assert.Equal("1h", body["messages"]![0]!["content"]![0]!["cache_control"]!["ttl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Auto_cache_can_be_disabled_per_request_and_per_adapter()
    {
        var (adapter, handler) = Create();
        var (disabledAdapter, disabledHandler) = Create(o => o with { AutoCache = false });
        handler.EnqueueJson(TextResponse);
        disabledHandler.EnqueueJson(TextResponse);
        var request = new Request
        {
            Model = "claude-sonnet-4-5",
            Messages = [Message.System("sys"), Message.User("hello")],
            Tools = [TestUtil.WeatherTool],
            ProviderOptions = (JsonObject)JsonNode.Parse("""{"anthropic": {"auto_cache": false}}""")!,
        };

        await adapter.CompleteAsync(request);
        await disabledAdapter.CompleteAsync(request with { ProviderOptions = null });

        foreach (var sent in new[] { handler.LastRequest, disabledHandler.LastRequest })
        {
            Assert.DoesNotContain("cache_control", sent.Body, StringComparison.Ordinal);
            Assert.Null(sent.Header("anthropic-beta"));
            Assert.False(sent.Json.ContainsKey("auto_cache"));
        }
    }

    [Fact]
    public async Task Reasoning_effort_enables_thinking_and_adjusts_incompatible_parameters()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(SimpleRequest() with
        {
            ReasoningEffort = "high",
            MaxTokens = 4000,
            Temperature = 0.7,
            TopP = 0.9,
            Tools = [TestUtil.WeatherTool],
        });

        var body = handler.LastRequest.Json;
        Assert.Equal("""{"type":"enabled","budget_tokens":16384}""", body["thinking"]!.ToJsonString());
        Assert.Equal(16384 + 4096, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("top_p"));
        Assert.Equal("prompt-caching-2024-07-31,interleaved-thinking-2025-05-14", handler.LastRequest.Header("anthropic-beta"));
        Assert.Contains(response.Warnings, w => w.Code == "max_tokens_adjusted");
        Assert.Contains(response.Warnings, w => w.Message.StartsWith("temperature", StringComparison.Ordinal));
        Assert.Contains(response.Warnings, w => w.Message.StartsWith("top_p", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("low", 2048)]
    [InlineData("medium", 8192)]
    [InlineData("high", 16384)]
    [InlineData("xhigh", 32768)]
    [InlineData("max", 32768)]
    public async Task Reasoning_effort_maps_to_thinking_budget(string effort, int budget)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        await adapter.CompleteAsync(SimpleRequest() with { ReasoningEffort = effort, MaxTokens = 64000 });

        Assert.Equal(budget, handler.LastRequest.Json["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Equal(64000, handler.LastRequest.Json["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task In_memory_thinking_budget_raises_max_tokens()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(SimpleRequest() with
        {
            MaxTokens = 1000,
            ProviderOptions = new JsonObject { ["anthropic"] = new JsonObject { ["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = 20000L } } },
        });

        Assert.Equal(20000 + 4096, handler.LastRequest.Json["max_tokens"]!.GetValue<int>());
        Assert.Contains(response.Warnings, w => w.Code == "max_tokens_adjusted");
    }

    [Fact]
    public async Task Forced_tool_choice_disables_thinking_with_warning()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse);

        var response = await adapter.CompleteAsync(SimpleRequest() with
        {
            ReasoningEffort = "medium",
            Temperature = 0.3,
            Tools = [TestUtil.WeatherTool],
            ToolChoice = ToolChoice.Required,
        });

        var body = handler.LastRequest.Json;
        Assert.False(body.ContainsKey("thinking"));
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
        Assert.Equal("""{"type":"any"}""", body["tool_choice"]!.ToJsonString());
        Assert.Contains(response.Warnings, w => w.Code == "thinking_disabled");
        Assert.DoesNotContain("interleaved", handler.LastRequest.Header("anthropic-beta") ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_options_pass_through_thinking_beta_headers_and_extra_fields()
    {
        var (adapter, handler) = Create(o => o with { DefaultHeaders = new Dictionary<string, string> { ["anthropic-beta"] = "context-1m-2025-08-07" } });
        handler.EnqueueJson(TextResponse);
        var options = new JsonObject
        {
            ["anthropic"] = new JsonObject
            {
                ["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = 2000 },
                ["beta_headers"] = "token-efficient-tools-2025-02-19, context-1m-2025-08-07",
                ["top_k"] = 5,
                ["service_tier"] = "auto",
            },
        };

        await adapter.CompleteAsync(SimpleRequest() with { ProviderOptions = options });

        var body = handler.LastRequest.Json;
        Assert.Equal("""{"type":"enabled","budget_tokens":2000}""", body["thinking"]!.ToJsonString());
        Assert.Equal(4096, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(5, body["top_k"]!.GetValue<int>());
        Assert.Equal("auto", body["service_tier"]!.GetValue<string>());
        Assert.False(body.ContainsKey("beta_headers"));
        Assert.Equal(
            "context-1m-2025-08-07,token-efficient-tools-2025-02-19,prompt-caching-2024-07-31",
            handler.LastRequest.Header("anthropic-beta"));

        // Array-valued beta headers are accepted too.
        handler.EnqueueJson(TextResponse);
        await adapter.CompleteAsync(SimpleRequest() with
        {
            ProviderOptions = (JsonObject)JsonNode.Parse("""{"anthropic":{"beta_headers":["interleaved-thinking-2025-05-14"],"auto_cache":false}}""")!,
        });
        Assert.Equal("context-1m-2025-08-07,interleaved-thinking-2025-05-14", handler.LastRequest.Header("anthropic-beta"));
    }

    [Fact]
    public async Task Thinking_blocks_round_trip_only_while_thinking_is_enabled()
    {
        var (adapter, handler) = Create(o => o with { AutoCache = false });
        handler.EnqueueJson(TextResponse).EnqueueJson(TextResponse);
        var history = new List<Message>
        {
            Message.User("Solve it"),
            Message.Assistant(
                ContentPart.ThinkingPart("step by step", "sig_abc123"),
                ContentPart.ThinkingPart("unsigned foreign reasoning"),
                ContentPart.RedactedThinkingPart("opaque=="),
                ContentPart.TextPart("42")),
            Message.User("Why?"),
        };

        await adapter.CompleteAsync(SimpleRequest() with { Messages = history, ReasoningEffort = "low" });
        await adapter.CompleteAsync(SimpleRequest() with { Messages = history });

        var withThinking = handler.Requests[0].Json["messages"]![1]!["content"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            [{"type":"thinking","thinking":"step by step","signature":"sig_abc123"},
             {"type":"redacted_thinking","data":"opaque=="},
             {"type":"text","text":"42"}]
            """), withThinking), withThinking.ToJsonString());
        Assert.Equal("""[{"type":"text","text":"42"}]""", handler.Requests[1].Json["messages"]![1]!["content"]!.ToJsonString());
    }

    [Fact]
    public async Task Parses_thinking_redacted_thinking_tool_use_usage_and_rate_limits()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(
            ToolUseResponse,
            HttpStatusCode.OK,
            ("anthropic-ratelimit-requests-limit", "4000"),
            ("anthropic-ratelimit-requests-remaining", "3999"),
            ("anthropic-ratelimit-tokens-limit", "400000"),
            ("anthropic-ratelimit-tokens-remaining", "395000"),
            ("anthropic-ratelimit-requests-reset", "2026-07-19T00:00:00Z"));

        var response = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal("msg_01XFDUDYJgAACzvnptvVoYEL", response.Id);
        Assert.Equal("claude-opus-4-6", response.Model);
        Assert.Equal("anthropic", response.Provider);
        var parts = response.Message.Content;
        Assert.Equal([ContentKind.Thinking, ContentKind.RedactedThinking, ContentKind.Text, ContentKind.ToolCall], parts.Select(p => p.Kind).ToArray());
        Assert.Equal(new ThinkingData { Text = ThinkingText, Signature = Signature }, parts[0].Thinking);
        Assert.Equal(RedactedData, parts[1].Thinking!.Text);
        Assert.True(parts[1].Thinking!.Redacted);
        Assert.Equal("I'll check the weather in Paris.", response.Text);
        Assert.Equal(ThinkingText, response.Reasoning);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("toolu_01A09q90qw90lq917835lq9", call.Id);
        Assert.Equal("c", call.Arguments["unit"]!.GetValue<string>());
        Assert.Equal(new FinishReason(FinishReason.ToolCallsReason, "tool_use"), response.FinishReason);

        Assert.Equal(2095, response.Usage.InputTokens);
        Assert.Equal(503, response.Usage.OutputTokens);
        Assert.Equal(2095 + 503, response.Usage.TotalTokens);
        Assert.Equal(4096, response.Usage.CacheReadTokens);
        Assert.Equal(1024, response.Usage.CacheWriteTokens);
        Assert.Equal((ThinkingText.Length + 3) / 4, response.Usage.ReasoningTokens);

        Assert.Equal(3999, response.RateLimit!.RequestsRemaining);
        Assert.Equal(4000, response.RateLimit.RequestsLimit);
        Assert.Equal(395000, response.RateLimit.TokensRemaining);
        Assert.Equal(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero), response.RateLimit.ResetAt);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("refusal", "content_filter")]
    [InlineData("pause_turn", "other")]
    public async Task Stop_reasons_are_mapped(string stopReason, string expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse.Replace("\"end_turn\"", "\"" + stopReason + "\"", StringComparison.Ordinal));

        var response = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal(new FinishReason(expected, stopReason), response.FinishReason);
        Assert.Null(response.Usage.ReasoningTokens);
    }

    [Fact]
    public async Task Stream_emits_unified_events_and_finish_matches_complete()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse(ToolUseStream).EnqueueJson(ToolUseResponse);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));
        var completed = await adapter.CompleteAsync(SimpleRequest());

        Assert.Equal(
            [
                "stream_start",
                "reasoning_start", "reasoning_delta", "reasoning_delta", "reasoning_end",
                "text_start", "text_delta", "text_delta", "text_end",
                "tool_call_start", "tool_call_delta", "tool_call_delta", "tool_call_end",
                "finish",
            ],
            TestUtil.Types(events));
        Assert.True(handler.Requests[0].Json["stream"]!.GetValue<bool>());

        var finish = events[^1];
        Assert.Equal(TestUtil.TextDeltas(events), finish.Response!.Text);
        Assert.Equal(TestUtil.ReasoningDeltas(events), finish.Response.Reasoning);
        Assert.Equal(new FinishReason(FinishReason.ToolCallsReason, "tool_use"), finish.FinishReason);
        Assert.Equal(503, finish.Usage!.OutputTokens);
        Assert.Equal(4096, finish.Usage.CacheReadTokens);
        TestUtil.AssertEquivalent(completed, finish.Response);
        Assert.Equal(completed.ToolCalls[0].Id, finish.Response.ToolCalls[0].Id);
        Assert.Equal(Signature, finish.Response.Message.Content[0].Thinking!.Signature);

        var textEvents = events.Where(e => e.Type == StreamEventType.TextStart || e.Type == StreamEventType.TextDelta || e.Type == StreamEventType.TextEnd);
        Assert.All(textEvents, e => Assert.Equal("2", e.TextId));
        var deltas = string.Concat(events.Where(e => e.Type == StreamEventType.ToolCallDelta).Select(e => e.Delta));
        var end = events.Single(e => e.Type == StreamEventType.ToolCallEnd).ToolCall!;
        Assert.Equal(deltas, end.RawArguments);
        Assert.Equal("Paris", end.Arguments["city"]!.GetValue<string>());
        Assert.Equal("toolu_01A09q90qw90lq917835lq9", end.Id);
    }

    [Fact]
    public async Task Stream_tool_use_without_input_deltas_has_empty_object_arguments()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_2","model":"claude-haiku-4-5","usage":{"input_tokens":5,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_now","name":"get_time","input":{}}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":12}}

            event: message_stop
            data: {"type":"message_stop"}

            """);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "tool_call_start", "tool_call_end", "finish"], TestUtil.Types(events));
        Assert.Empty(events[2].ToolCall!.Arguments);
        Assert.Equal("{}", events[2].ToolCall!.RawArguments);
        var call = Assert.Single(events[^1].Response!.ToolCalls);
        Assert.Empty(call.Arguments);
        Assert.Equal(17, events[^1].Usage!.TotalTokens);
    }

    [Fact]
    public async Task Stream_error_event_yields_error()
    {
        var (adapter, handler) = Create();
        handler.EnqueueSse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_3","model":"claude-opus-4-6","usage":{"input_tokens":5,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

            event: error
            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

            """);

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest()));

        Assert.Equal(["stream_start", "text_start", "text_delta", "error"], TestUtil.Types(events));
        var error = Assert.IsType<ServerError>(events[^1].Error);
        Assert.True(error.Retryable);
        Assert.Equal("overloaded_error", error.ErrorCode);
        Assert.Equal("anthropic", error.Provider);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key", typeof(AuthenticationError))]
    [InlineData(HttpStatusCode.Forbidden, "permission_error", "Your API key does not have permission", typeof(AccessDeniedError))]
    [InlineData(HttpStatusCode.NotFound, "not_found_error", "model: claude-nope", typeof(NotFoundError))]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long: 210000 tokens > 200000 maximum", typeof(ContextLengthError))]
    [InlineData(HttpStatusCode.InternalServerError, "api_error", "Internal server error", typeof(ServerError))]
    [InlineData((HttpStatusCode)529, "overloaded_error", "Overloaded", typeof(ServerError))]
    public async Task Http_errors_map_to_the_error_hierarchy(HttpStatusCode status, string type, string message, Type expected)
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson($$$"""{"type":"error","error":{"type":"{{{type}}}","message":"{{{message}}}"}}""", status);

        var error = await Assert.ThrowsAnyAsync<SdkError>(() => adapter.CompleteAsync(SimpleRequest()));

        Assert.IsType(expected, error);
        Assert.Equal(type, Assert.IsType<ProviderError>(error, exactMatch: false).ErrorCode);
    }

    [Fact]
    public async Task Rate_limit_error_carries_retry_after()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson("""{"type":"error","error":{"type":"rate_limit_error","message":"Number of request tokens has exceeded your per-minute rate limit"}}""", HttpStatusCode.TooManyRequests, ("retry-after", "30"));

        var error = await Assert.ThrowsAsync<RateLimitError>(() => adapter.StreamAsync(SimpleRequest()).GetAsyncEnumerator().MoveNextAsync().AsTask());

        Assert.Equal(30, error.RetryAfter);
    }

    [Fact]
    public async Task Images_documents_and_tool_result_images_are_translated()
    {
        var (adapter, handler) = Create(o => o with { AutoCache = false, BaseUrl = "https://gateway.example.com/anthropic/v1/" });
        handler.EnqueueJson(TextResponse);
        using var file = TestUtil.CreateFile(".jpg", TestUtil.PngBytes);

        await adapter.CompleteAsync(SimpleRequest() with
        {
            Messages =
            [
                Message.User(
                    ContentPart.ImagePart(ImageData.FromUrl("https://example.com/a.png")),
                    ContentPart.ImagePart(ImageData.FromBytes(TestUtil.PngBytes)),
                    ContentPart.ImagePart(ImageData.FromUrl(file.Path)),
                    ContentPart.ImagePart(ImageData.FromUrl("data:image/webp;base64," + TestUtil.PngBase64)),
                    ContentPart.DocumentPart(new DocumentData { Data = TestUtil.PdfBytes, FileName = "report.pdf" }),
                    ContentPart.AudioPart(new AudioData { Data = [1, 2, 3], MediaType = "audio/wav" })),
                Message.Assistant(ContentPart.ToolCallPart("toolu_s", "screenshot", null)),
                Message.ToolResult("toolu_s", JsonValue.Create("captured"), imageData: TestUtil.PngBytes, imageMediaType: "image/png"),
            ],
        });

        Assert.Equal("https://gateway.example.com/anthropic/v1/messages", handler.LastRequest.Uri.ToString());
        var expected = JsonNode.Parse($$$"""
            [
              {"type":"image","source":{"type":"url","url":"https://example.com/a.png"}},
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{{TestUtil.PngBase64}}}"}},
              {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"{{{TestUtil.PngBase64}}}"}},
              {"type":"image","source":{"type":"base64","media_type":"image/webp","data":"{{{TestUtil.PngBase64}}}"}},
              {"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"{{{Convert.ToBase64String(TestUtil.PdfBytes)}}}"},"title":"report.pdf"}
            ]
            """);
        var messages = handler.LastRequest.Json["messages"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(expected, messages[0]!["content"]), messages[0]!["content"]!.ToJsonString());
        Assert.Equal("""{"type":"tool_use","id":"toolu_s","name":"screenshot","input":{}}""", messages[1]!["content"]![0]!.ToJsonString());
        var toolResult = messages[2]!["content"]![0]!;
        Assert.Equal(
            $$$"""{"type":"tool_result","tool_use_id":"toolu_s","content":[{"type":"text","text":"captured"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{{TestUtil.PngBase64}}}"}}],"is_error":false}""",
            toolResult.ToJsonString());
    }

    [Fact]
    public async Task Json_response_formats_are_requested_through_a_system_instruction()
    {
        var (adapter, handler) = Create(o => o with { AutoCache = false });
        handler.EnqueueJson(TextResponse).EnqueueJson(TextResponse);
        var schema = (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""")!;

        await adapter.CompleteAsync(SimpleRequest() with { Messages = [Message.System("Extract data."), Message.User("Alice")], ResponseFormat = ResponseFormat.ForSchema(schema) });
        await adapter.CompleteAsync(SimpleRequest() with { ResponseFormat = ResponseFormat.Json });

        var system = handler.Requests[0].Json["system"]!.AsArray();
        Assert.Equal(2, system.Count);
        Assert.Equal("Extract data.", system[0]!["text"]!.GetValue<string>());
        var instruction = system[1]!["text"]!.GetValue<string>();
        Assert.Contains("JSON", instruction, StringComparison.Ordinal);
        Assert.Contains(schema.ToJsonString(), instruction, StringComparison.Ordinal);
        Assert.Contains("JSON", handler.Requests[1].Json["system"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_is_not_mutated_and_can_be_sent_twice()
    {
        var (adapter, handler) = Create();
        handler.EnqueueJson(TextResponse).EnqueueJson(TextResponse);
        var request = SimpleRequest() with
        {
            Tools = [TestUtil.WeatherTool],
            ProviderOptions = (JsonObject)JsonNode.Parse("""{"anthropic":{"thinking":{"type":"enabled","budget_tokens":1024},"top_k":3}}""")!,
        };

        await adapter.CompleteAsync(request);
        await adapter.CompleteAsync(request);

        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.False(request.Tools[0].Parameters.ContainsKey("cache_control"));
        Assert.Equal("""{"anthropic":{"thinking":{"type":"enabled","budget_tokens":1024},"top_k":3}}""", request.ProviderOptions!.ToJsonString());
    }
}

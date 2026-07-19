using System.Net;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers;

namespace Reactor.Llm.Tests.Providers;

/// <summary>Behavior every adapter shares: tool choice support, cancellation, timeouts, stream failures, lifetime, concurrency.</summary>
public class AdapterCommonTests
{
    public static TheoryData<string> Providers => new() { "openai", "anthropic", "gemini", "compatible" };

    private static (IProviderAdapter Adapter, FakeHttpHandler Handler) Create(string provider, AdapterTimeout? timeout = null)
    {
        var handler = new FakeHttpHandler();
        var client = new HttpClient(handler);
        var effective = timeout ?? AdapterTimeout.Default;
        IProviderAdapter adapter = provider switch
        {
            "openai" => new OpenAIAdapter(new OpenAIAdapterOptions { ApiKey = "k", HttpClient = client, Timeout = effective }),
            "anthropic" => new AnthropicAdapter(new AnthropicAdapterOptions { ApiKey = "k", HttpClient = client, Timeout = effective }),
            "gemini" => new GeminiAdapter(new GeminiAdapterOptions { ApiKey = "k", HttpClient = client, Timeout = effective }),
            _ => new OpenAICompatibleAdapter(new OpenAICompatibleAdapterOptions { BaseUrl = "http://localhost:1234/v1", HttpClient = client, Timeout = effective }),
        };
        return (adapter, handler);
    }

    /// <summary>One SSE event that yields a single TEXT_DELTA "Hel" for the provider.</summary>
    private static string TextDeltaEvent(string provider) => provider switch
    {
        "openai" => "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hel\"}\n\n",
        "anthropic" => "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}\n\n",
        "gemini" => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hel\"}],\"role\":\"model\"},\"index\":0}]}\n\n",
        _ => "data: {\"id\":\"c\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n",
    };

    /// <summary>A complete non-streaming response whose text is <paramref name="text"/>.</summary>
    private static string TextResponse(string provider, string text) => provider switch
    {
        "openai" => new JsonObject
        {
            ["id"] = "r", ["status"] = "completed", ["model"] = "m",
            ["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
            }),
        }.ToJsonString(),
        "anthropic" => new JsonObject
        {
            ["id"] = "m", ["model"] = "m", ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        }.ToJsonString(),
        "gemini" => new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["finishReason"] = "STOP",
            }),
        }.ToJsonString(),
        _ => new JsonObject
        {
            ["id"] = "c", ["model"] = "m",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                ["finish_reason"] = "stop",
            }),
        }.ToJsonString(),
    };

    private static Request SimpleRequest(string provider) => new()
    {
        Model = provider switch { "openai" => "gpt-4.1", "anthropic" => "claude-sonnet-4-5", "gemini" => "gemini-2.5-flash", _ => "llama" },
        Messages = [Message.User("Hi")],
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void Supports_all_four_tool_choice_modes(string provider)
    {
        var (adapter, _) = Create(provider);

        foreach (var mode in new[] { "auto", "none", "required", "named" })
        {
            Assert.True(adapter.SupportsToolChoice(mode), mode);
        }

        Assert.False(adapter.SupportsToolChoice("sometimes"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Invalid_tool_choices_are_rejected_before_sending(string provider)
    {
        var (adapter, handler) = Create(provider);
        var tools = new[] { TestUtil.WeatherTool };

        await Assert.ThrowsAsync<UnsupportedToolChoiceError>(() => adapter.CompleteAsync(SimpleRequest(provider) with { Tools = tools, ToolChoice = new ToolChoice("sometimes") }));
        await Assert.ThrowsAsync<InvalidRequestError>(() => adapter.CompleteAsync(SimpleRequest(provider) with { Tools = tools, ToolChoice = new ToolChoice("named") }));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Cancelling_mid_stream_throws_abort_error(string provider)
    {
        var (adapter, handler) = Create(provider);
        handler.EnqueueStream(new ScriptedStream(TextDeltaEvent(provider), ScriptedStream.Ending.Block));
        using var cts = new CancellationTokenSource();
        var received = new List<StreamEvent>();

        await Assert.ThrowsAsync<AbortError>(async () =>
        {
            await foreach (var evt in adapter.StreamAsync(SimpleRequest(provider), cts.Token))
            {
                received.Add(evt);
                if (evt.Type == StreamEventType.TextDelta)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.Contains(received, e => e.Type == StreamEventType.TextDelta && e.Delta == "Hel");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Cancelling_before_headers_throws_abort_error_from_first_move_next(string provider)
    {
        var (adapter, handler) = Create(provider);
        handler.EnqueueHang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<AbortError>(() => TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest(provider), cts.Token)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Stream_read_timeout_ends_with_request_timeout_error_event(string provider)
    {
        var (adapter, handler) = Create(provider, new AdapterTimeout { StreamRead = TimeSpan.FromMilliseconds(150) });
        handler.EnqueueStream(new ScriptedStream(TextDeltaEvent(provider), ScriptedStream.Ending.Block));

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest(provider)));

        Assert.Equal(StreamEventType.StreamStart, events[0].Type);
        Assert.Contains(events, e => e.Type == StreamEventType.TextDelta);
        Assert.Equal(StreamEventType.Error, events[^1].Type);
        Assert.IsType<RequestTimeoutError>(events[^1].Error);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Stream_io_failure_ends_with_stream_error_event(string provider)
    {
        var (adapter, handler) = Create(provider);
        handler.EnqueueStream(new ScriptedStream(TextDeltaEvent(provider), ScriptedStream.Ending.Throw));

        var events = await TestUtil.CollectAsync(adapter.StreamAsync(SimpleRequest(provider)));

        Assert.Equal(StreamEventType.Error, events[^1].Type);
        var error = Assert.IsType<StreamError>(events[^1].Error);
        Assert.True(error.Retryable);
        Assert.IsType<IOException>(error.InnerException);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Request_timeout_raises_request_timeout_error(string provider)
    {
        var (adapter, handler) = Create(provider, new AdapterTimeout { Request = TimeSpan.FromMilliseconds(100) });
        handler.EnqueueHang();

        await Assert.ThrowsAsync<RequestTimeoutError>(() => adapter.CompleteAsync(SimpleRequest(provider)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Close_never_disposes_a_caller_supplied_http_client(string provider)
    {
        var (adapter, handler) = Create(provider);
        handler.EnqueueJson(TextResponse(provider, "first")).EnqueueJson(TextResponse(provider, "second"));
        Assert.Equal("first", (await adapter.CompleteAsync(SimpleRequest(provider))).Text);

        await adapter.CloseAsync();
        await adapter.CloseAsync();

        // The caller-supplied HttpClient is still alive, so requests keep flowing through it.
        Assert.Equal("second", (await adapter.CompleteAsync(SimpleRequest(provider))).Text);
    }

    [Fact]
    public async Task Close_disposes_an_adapter_owned_http_client()
    {
        // Port 9 (discard) on loopback: if the client were not disposed the call would fail with a NetworkError instead.
        var adapter = new OpenAIAdapter(new OpenAIAdapterOptions { ApiKey = "k", BaseUrl = "http://127.0.0.1:9/v1" });
        await adapter.CloseAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.CompleteAsync(SimpleRequest("openai")));
    }

    [Fact]
    public async Task Supplied_http_client_remains_usable_after_close()
    {
        var handler = new FakeHttpHandler();
        using var httpClient = new HttpClient(handler);
        var adapter = new AnthropicAdapter(new AnthropicAdapterOptions { ApiKey = "k", HttpClient = httpClient });
        await adapter.CloseAsync();
        handler.EnqueueJson("{}");

        using var response = await httpClient.GetAsync(new Uri("https://example.com/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Adapters_are_safe_for_concurrent_use(string provider)
    {
        var (adapter, handler) = Create(provider);
        handler.Fallback = async (request, ct) =>
        {
            await Task.Yield();
            var json = request.Json;
            var text = provider switch
            {
                "openai" => json["input"]![0]!["content"]![0]!["text"]!.GetValue<string>(),
                "anthropic" => json["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>(),
                "gemini" => json["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>(),
                _ => json["messages"]![0]!["content"]!.GetValue<string>(),
            };
            return FakeHttpHandler.Json(TextResponse(provider, "echo:" + text));
        };

        var tasks = Enumerable.Range(0, 32)
            .Select(i => adapter.CompleteAsync(SimpleRequest(provider) with { Messages = [Message.User("m" + i)] }))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        for (var i = 0; i < responses.Length; i++)
        {
            Assert.Equal("echo:m" + i, responses[i].Text);
        }
    }
}

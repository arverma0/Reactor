using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Reactor.Llm.Tests.Providers;

/// <summary>An HTTP request captured by <see cref="FakeHttpHandler"/>.</summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public JsonObject Json => JsonNode.Parse(Body) as JsonObject ?? throw new InvalidOperationException("Request body is not a JSON object.");

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A fake transport that records requests and replays canned responses in order.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Queue<Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>> _responders = new();
    private readonly List<CapturedRequest> _requests = [];

    /// <summary>Used when the queue is empty (e.g. concurrency tests).</summary>
    public Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>? Fallback { get; set; }

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public CapturedRequest LastRequest => Requests[^1];

    public FakeHttpHandler Enqueue(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        lock (_gate)
        {
            _responders.Enqueue(responder);
        }

        return this;
    }

    public FakeHttpHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers) =>
        Enqueue((_, _) => Task.FromResult(Json(json, status, headers)));

    public FakeHttpHandler EnqueueSse(string sse, params (string Name, string Value)[] headers) =>
        Enqueue((_, _) => Task.FromResult(Sse(sse, headers)));

    public FakeHttpHandler EnqueueStream(Stream stream, params (string Name, string Value)[] headers) =>
        Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
            AddHeaders(response, headers);
            return Task.FromResult(response);
        });

    /// <summary>A responder that never completes until the request is cancelled.</summary>
    public FakeHttpHandler EnqueueHang() =>
        Enqueue(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        AddHeaders(response, headers);
        return response;
    }

    public static HttpResponseMessage Sse(string sse, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        AddHeaders(response, headers);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body);
        Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder;
        lock (_gate)
        {
            _requests.Add(captured);
            responder = _responders.Count > 0
                ? _responders.Dequeue()
                : Fallback ?? throw new InvalidOperationException("No canned response queued for " + request.RequestUri);
        }

        var response = await responder(captured, cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    private static void AddHeaders(HttpResponseMessage response, (string Name, string Value)[] headers)
    {
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

/// <summary>A response body that serves fixed bytes and then blocks (honoring cancellation), fails, or ends.</summary>
internal sealed class ScriptedStream(string initial, ScriptedStream.Ending ending) : Stream
{
    private readonly byte[] _data = Encoding.UTF8.GetBytes(initial);
    private int _position;

    public enum Ending
    {
        Eof,
        Block,
        Throw,
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _data.Length)
        {
            var count = Math.Min(buffer.Length, _data.Length - _position);
            _data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        switch (ending)
        {
            case Ending.Block:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            case Ending.Throw:
                throw new IOException("The connection was reset by the peer.");
            default:
                return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Shared helpers for adapter tests.</summary>
internal static class TestUtil
{
    /// <summary>A tiny fake PNG payload (content is irrelevant to translation).</summary>
    public static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03];

    public static readonly string PngBase64 = Convert.ToBase64String(PngBytes);

    public static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 fake");

    public static readonly JsonObject WeatherSchema = (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{"city":{"type":"string","description":"City name"},"unit":{"type":"string","enum":["c","f"]}},"required":["city"]}
        """)!;

    public static ToolDefinition WeatherTool => new("get_weather", "Get the weather for a city", (JsonObject)WeatherSchema.DeepClone());

    public static ToolDefinition SearchTool => new("search", "Search the web", (JsonObject)JsonNode.Parse(
        """{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer"}},"required":["query"]}""")!);

    public static async Task<List<StreamEvent>> CollectAsync(IAsyncEnumerable<StreamEvent> stream, CancellationToken cancellationToken = default)
    {
        var events = new List<StreamEvent>();
        await foreach (var evt in stream.WithCancellation(cancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    public static string[] Types(IEnumerable<StreamEvent> events) => events.Select(e => e.Type.Value).ToArray();

    public static string TextDeltas(IEnumerable<StreamEvent> events) =>
        string.Concat(events.Where(e => e.Type == StreamEventType.TextDelta).Select(e => e.Delta));

    public static string ReasoningDeltas(IEnumerable<StreamEvent> events) =>
        string.Concat(events.Where(e => e.Type == StreamEventType.ReasoningDelta).Select(e => e.ReasoningDelta));

    /// <summary>Asserts that a streamed FINISH response carries the same content and metadata as a non-streaming one.</summary>
    public static void AssertEquivalent(Response expected, Response actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Reasoning, actual.Reasoning);
        Assert.Equal(expected.FinishReason, actual.FinishReason);
        Assert.Equal(expected.Usage with { Raw = null }, actual.Usage with { Raw = null });
        Assert.Equal(expected.Message.Content.Count, actual.Message.Content.Count);
        for (var i = 0; i < expected.Message.Content.Count; i++)
        {
            var e = expected.Message.Content[i];
            var a = actual.Message.Content[i];
            Assert.Equal(e.Kind, a.Kind);
            Assert.Equal(e.Text, a.Text);
            Assert.Equal(e.Thinking, a.Thinking);
            Assert.True(JsonNode.DeepEquals(e.ProviderMetadata, a.ProviderMetadata), $"Provider metadata differs at part {i}.");
            if (e.ToolCall is not null)
            {
                Assert.NotNull(a.ToolCall);
                Assert.Equal(e.ToolCall.Name, a.ToolCall.Name);
                Assert.True(JsonNode.DeepEquals(e.ToolCall.Arguments, a.ToolCall.Arguments), $"Tool arguments differ at part {i}.");
            }
        }
    }

    /// <summary>Creates a file under the test output directory (cleaned up on dispose).</summary>
    public static TestFile CreateFile(string extension, byte[] content)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, content);
        return new TestFile(path);
    }

    internal sealed class TestFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}

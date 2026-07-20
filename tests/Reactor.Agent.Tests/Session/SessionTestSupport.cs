using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Reactor.Llm;

namespace Reactor.Agent.Tests.Session;

/// <summary>
/// A provider adapter driven by a responder function (or a FIFO script). Streams are synthesized from the response using
/// the unified start/delta/end pattern.
/// </summary>
internal sealed class FakeLlm : IProviderAdapter
{
    private readonly ConcurrentQueue<Func<Request, Response>> _script = new();
    private readonly List<Request> _requests = [];

    public FakeLlm(string name = "test")
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>When set, used instead of the FIFO script.</summary>
    public Func<Request, Response>? Responder { get; set; }

    /// <summary>Awaited before each response (lets tests block the model call, e.g. to test abort).</summary>
    public Func<Request, CancellationToken, Task>? BeforeRespond { get; set; }

    private StreamFailure? _pendingStreamFailure;

    private sealed record StreamFailure(int AfterEvents, Exception Error);

    /// <summary>Makes the next stream throw <paramref name="error"/> after yielding <paramref name="afterEvents"/> events.</summary>
    public void FailNextStream(int afterEvents, Exception error) => _pendingStreamFailure = new StreamFailure(afterEvents, error);

    public IReadOnlyList<Request> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public FakeLlm Then(Func<Request, Response> responder)
    {
        _script.Enqueue(responder);
        return this;
    }

    public FakeLlm ThenText(string text) => Then(_ => Text(text));

    public FakeLlm ThenTools(params (string Id, string Name, string Args)[] calls) => Then(_ => ToolCalls(calls));

    public FakeLlm ThenThrow(Exception error) => Then(_ => throw error);

    public async Task<Response> CompleteAsync(Request request, CancellationToken cancellationToken = default)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        if (BeforeRespond is { } before)
        {
            await before(request, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Responder is { } responder)
        {
            return responder(request) with { Provider = Name };
        }

        if (!_script.TryDequeue(out var next))
        {
            throw new InvalidOperationException("FakeLlm ran out of scripted responses.");
        }

        return next(request) with { Provider = Name };
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await CompleteAsync(request, cancellationToken);
        var failure = Interlocked.Exchange(ref _pendingStreamFailure, null);
        var emitted = 0;
        foreach (var evt in Synthesize(response))
        {
            if (failure is not null && emitted == failure.AfterEvents)
            {
                throw failure.Error;
            }

            cancellationToken.ThrowIfCancellationRequested();
            emitted++;
            yield return evt;
        }
    }

    public static Response Text(string text) => new()
    {
        Id = "resp_" + Guid.NewGuid().ToString("N")[..8],
        Model = "test-model",
        Message = Message.Assistant(text),
        FinishReason = FinishReason.Stop,
        Usage = new Usage { InputTokens = 10, OutputTokens = 5, TotalTokens = 15 },
    };

    public static Response ToolCalls(params (string Id, string Name, string Args)[] calls) => new()
    {
        Id = "resp_" + Guid.NewGuid().ToString("N")[..8],
        Model = "test-model",
        Message = new Message
        {
            Role = Role.Assistant,
            Content = calls.Select(c => ContentPart.ToolCallPart(c.Id, c.Name, JsonNode.Parse(c.Args))).ToList(),
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
                yield return new StreamEvent { Type = StreamEventType.ToolCallStart, ToolCall = new ToolCall(call.Id, call.Name, []) };
                yield return new StreamEvent
                {
                    Type = StreamEventType.ToolCallDelta,
                    ToolCall = new ToolCall(call.Id, call.Name, []),
                    Delta = call.ArgumentsJson,
                };
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

/// <summary>An in-memory execution environment: files in a dictionary, commands answered by a handler.</summary>
internal sealed class FakeEnvironment(string workingDirectory = "/work") : IExecutionEnvironment
{
    public ConcurrentDictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public Func<string, ExecResult>? CommandHandler { get; set; }

    public ConcurrentQueue<string> Commands { get; } = new();

    public int CleanupCount;

    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory);

    public string Platform => "linux";

    public string OsVersion => "TestOS 1.0";

    public string ShellName => "/bin/bash";

    public string ResolvePath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));

    public Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default) =>
        Files.TryGetValue(ResolvePath(path), out var content)
            ? Task.FromResult(content)
            : Task.FromException<string>(new FileNotFoundException("File not found", path));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) =>
        ReadFileAsync(path, cancellationToken: cancellationToken).ContinueWith(t => System.Text.Encoding.UTF8.GetBytes(t.Result), TaskScheduler.Default);

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Files[ResolvePath(path)] = content;
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Files.ContainsKey(ResolvePath(path)));

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        Files.TryRemove(ResolvePath(path), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DirEntry>> ListDirectoryAsync(string path, int depth = 1, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DirEntry>>([]);

    public Task<ExecResult> ExecCommandAsync(
        string command,
        int timeoutMs,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command);
        var result = CommandHandler?.Invoke(command) ?? new ExecResult { ExitCode = 128, Stderr = "not a git repository" };
        return Task.FromResult(result);
    }

    public Task<string> GrepAsync(string pattern, string path, GrepOptions options, CancellationToken cancellationToken = default) => Task.FromResult("");

    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CleanupAsync()
    {
        Interlocked.Increment(ref CleanupCount);
        return Task.CompletedTask;
    }
}

/// <summary>Builds sessions over a <see cref="FakeLlm"/> with a small set of controllable tools.</summary>
internal static class SessionHarness
{
    public static readonly RetryPolicy FastRetry = new() { MaxRetries = 2, BaseDelay = 0.001, MaxDelay = 1, Jitter = false };

    public static ToolRegistry TestTools(ToolExecutor? custom = null) => new([
        new RegisteredTool(
            new ToolDefinition("echo", "Echo text back.", Schema("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""")),
            (args, _) => Task.FromResult<ToolOutput>("echo: " + args["text"]!.GetValue<string>())),
        new RegisteredTool(
            new ToolDefinition("fail", "Always throws.", Schema("""{"type":"object","properties":{}}""")),
            (_, _) => throw new InvalidOperationException("boom")),
        new RegisteredTool(
            new ToolDefinition("big", "Returns a large output.", Schema("""{"type":"object","properties":{"size":{"type":"integer"}}}""")),
            (args, _) => Task.FromResult<ToolOutput>(new string('x', args["size"]?.GetValue<int>() ?? 100_000))),
        new RegisteredTool(
            new ToolDefinition("custom", "Custom test behavior.", Schema("""{"type":"object","properties":{"value":{"type":"string"}}}""")),
            custom ?? ((_, _) => Task.FromResult<ToolOutput>("custom"))),
    ]);

    public static ProviderProfile Profile(
        ToolRegistry? tools = null,
        bool parallel = true,
        int contextWindow = 200_000,
        string provider = "test",
        string model = "test-model") =>
        new(provider, model, tools ?? TestTools(), "You are a test agent.", ["AGENTS.md"], contextWindowSize: contextWindow, supportsParallelToolCalls: parallel);

    public static (Agent.Session Session, FakeLlm Llm, FakeEnvironment Env, List<SessionEvent> Events) Create(
        Action<SessionConfig>? configure = null,
        ProviderProfile? profile = null,
        FakeLlm? llm = null,
        FakeEnvironment? env = null,
        SessionOptions? options = null)
    {
        llm ??= new FakeLlm();
        env ??= new FakeEnvironment();
        profile ??= Profile();
        profile.ProviderName = llm.Name;
        var config = new SessionConfig { LlmRetryPolicy = FastRetry };
        configure?.Invoke(config);
        var client = new Client(new Dictionary<string, IProviderAdapter> { [llm.Name] = llm }, llm.Name);
        var session = new Agent.Session(profile, env, client, config, options);
        var events = new List<SessionEvent>();
        session.Events.Subscribe(e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        });
        return (session, llm, env, events);
    }

    public static List<EventKind> Kinds(List<SessionEvent> events)
    {
        lock (events)
        {
            return events.Select(e => e.Kind).ToList();
        }
    }

    public static List<SessionEvent> Of(List<SessionEvent> events, EventKind kind)
    {
        lock (events)
        {
            return events.Where(e => e.Kind == kind).ToList();
        }
    }

    public static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>The tool result messages in a request, in order.</summary>
    public static List<ToolResultData> ToolResults(Request request) =>
        request.Messages.Where(m => m.Role == Role.Tool).SelectMany(m => m.Content).Where(p => p.ToolResult is not null).Select(p => p.ToolResult!).ToList();

    public static string ResultText(ToolResultData result) => result.Content is JsonValue v && v.TryGetValue<string>(out var s) ? s : result.Content?.ToJsonString() ?? "";
}

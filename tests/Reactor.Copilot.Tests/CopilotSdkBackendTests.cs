using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;

namespace Reactor.Copilot.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reactor-copilot-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A fake Copilot runtime whose conversations run a scripted turn handler.</summary>
internal sealed class FakeCopilotRuntime : ICopilotRuntime
{
    public delegate Task<string?> TurnHandler(FakeConversation conversation, string prompt, CancellationToken cancellationToken);

    public List<CopilotConversationOptions> Created { get; } = [];

    public List<FakeConversation> Conversations { get; } = [];

    public TurnHandler OnTurn { get; set; } = (_, _, _) => Task.FromResult<string?>("done");

    /// <summary>Models reported by <see cref="ListModelsAsync"/>; null makes listing fail (verification is skipped).</summary>
    public List<CopilotModelInfo>? Models { get; set; }

    public int ListCalls;

    public bool Disposed { get; private set; }

    public Task<IReadOnlyList<CopilotModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ListCalls);
        return Models is null
            ? Task.FromException<IReadOnlyList<CopilotModelInfo>>(new IOException("models.list is not supported"))
            : Task.FromResult<IReadOnlyList<CopilotModelInfo>>(Models);
    }

    public Task<ICopilotConversation> CreateConversationAsync(CopilotConversationOptions options, CancellationToken cancellationToken)
    {
        var conversation = new FakeConversation(this, options);
        OnCreated?.Invoke(conversation);
        lock (Created)
        {
            Created.Add(options);
            Conversations.Add(conversation);
        }

        return Task.FromResult<ICopilotConversation>(conversation);
    }

    /// <summary>Configures each conversation as it is created.</summary>
    public Action<FakeConversation>? OnCreated { get; set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeConversation(FakeCopilotRuntime runtime, CopilotConversationOptions options) : ICopilotConversation
{
    private readonly List<Action<CopilotActivity>> _handlers = [];

    public CopilotConversationOptions Options { get; } = options;

    public string SessionId => Options.SessionId;

    public List<string> Prompts { get; } = [];

    public List<(string Model, string? Effort)> ModelChanges { get; } = [];

    public int Aborts { get; private set; }

    public bool Disposed { get; private set; }

    public IDisposable Subscribe(Action<CopilotActivity> handler)
    {
        lock (_handlers)
        {
            _handlers.Add(handler);
        }

        return new Unsubscribe(() =>
        {
            lock (_handlers)
            {
                _handlers.Remove(handler);
            }
        });
    }

    public void Emit(CopilotActivity activity)
    {
        Action<CopilotActivity>[] handlers;
        lock (_handlers)
        {
            handlers = _handlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            handler(activity);
        }
    }

    public Task<string?> SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        return runtime.OnTurn(this, prompt, cancellationToken);
    }

    /// <summary>Steering messages sent to the turn in progress.</summary>
    public List<string> Steered { get; } = [];

    /// <summary>Completes with the first steering message.</summary>
    public TaskCompletionSource<string> FirstSteering { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SteerAsync(string message, CancellationToken cancellationToken)
    {
        SteerAttempted.TrySetResult();
        if (SteerError is { } error)
        {
            return Task.FromException(error);
        }

        lock (Steered)
        {
            Steered.Add(message);
        }

        FirstSteering.TrySetResult(message);
        return Task.CompletedTask;
    }

    /// <summary>When set, <see cref="SteerAsync"/> fails with this exception.</summary>
    public Exception? SteerError { get; set; }

    /// <summary>Completes when <see cref="SteerAsync"/> is first called (even if it fails).</summary>
    public TaskCompletionSource SteerAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task AbortAsync()
    {
        Aborts++;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string model, string? reasoningEffort, CancellationToken cancellationToken)
    {
        ModelChanges.Add((model, reasoningEffort));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

public sealed class CopilotSdkBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PipelineRunner Runner(CopilotSdkBackend backend) => new(new PipelineRunnerOptions { Backend = backend });

    private static RunOptions Options(TempDir dir, List<PipelineEvent>? events = null, string? logsRoot = null) => new()
    {
        LogsRoot = logsRoot ?? dir.Combine("run"),
        WorkingDirectory = dir.Path,
        Backoff = BackoffConfig.Immediate,
        MaxSteps = 100,
        OnEvent = events is null ? null : e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        },
    };

    private static List<string> Logs(List<PipelineEvent> events, string node)
    {
        lock (events)
        {
            return events.OfType<StageLog>().Where(e => e.NodeId == node).Select(e => e.Message).ToList();
        }
    }

    private const string OneStage = """
        digraph G {
          start [shape=Mdiamond]
          exit [shape=Msquare]
          implement [prompt="Implement it", llm_model="claude-sonnet-4-5", reasoning_effort="minimal"]
          start -> implement -> exit
        }
        """;

    [Fact]
    public async Task Stage_runs_one_copilot_turn_and_records_activity()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime
        {
            OnTurn = (c, _, _) =>
            {
                c.Emit(new CopilotActivity.AssistantMessage("Working on it.", "claude-sonnet-4.5"));
                c.Emit(new CopilotActivity.ToolStarted("t1", "create", JsonNode.Parse("""{"path":"a.txt"}""")));
                c.Emit(new CopilotActivity.ToolCompleted("t1", true, null));
                c.Emit(new CopilotActivity.ToolStarted("t2", "bash", JsonNode.Parse("""{"command":"npm test"}""")));
                c.Emit(new CopilotActivity.ToolCompleted("t2", false, "exit code 1"));
                c.Emit(new CopilotActivity.ToolStarted("t3", "view", null) { FromSubagent = true });
                c.Emit(new CopilotActivity.Usage("claude-sonnet-4.5", 1000, 200, 50, 10));
                c.Emit(new CopilotActivity.Usage("claude-sonnet-4.5", 500, 100, 0, 0));
                c.Emit(new CopilotActivity.UsageCheckpoint(2_500_000_000));
                return Task.FromResult<string?>("All done.");
            },
        };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);
        var events = new List<PipelineEvent>();

        var result = await Runner(backend).RunAsync(OneStage, Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        var created = Assert.Single(runtime.Created);
        Assert.Equal("claude-sonnet-4.5", created.Model);
        Assert.Equal("low", created.ReasoningEffort);
        Assert.Equal(dir.Path, created.WorkingDirectory);
        Assert.Empty(created.AdditionalDirectories);
        Assert.True(Guid.TryParse(created.SessionId, out _));

        var conversation = runtime.Conversations[0];
        Assert.Contains("Implement it", conversation.Prompts[0]);
        Assert.Contains("Reporting the stage outcome", conversation.Prompts[0]);
        Assert.True(conversation.Disposed);

        Assert.Equal("All done.", File.ReadAllText(dir.Combine("run", "implement", "response.md")));
        var metadata = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "implement", "backend.json")))!;
        Assert.Equal("copilot-sdk", metadata["backend"]!.GetValue<string>());
        Assert.Equal(3, metadata["tool_calls"]!.GetValue<int>());
        Assert.Equal(1, metadata["tool_errors"]!.GetValue<int>());
        Assert.Equal(2, metadata["model_calls"]!.GetValue<int>());
        Assert.Equal(1500, metadata["input_tokens"]!.GetValue<long>());
        Assert.Equal(300, metadata["output_tokens"]!.GetValue<long>());
        Assert.Equal(2.5, metadata["ai_units"]!.GetValue<double>());
        Assert.Equal(created.SessionId, metadata["session_id"]!.GetValue<string>());

        var logs = Logs(events, "implement");
        Assert.Contains(logs, l => l.StartsWith("→ create a.txt", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("✗ bash: exit code 1", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("[subagent] → view", StringComparison.Ordinal));
        Assert.Contains("tool.start", File.ReadAllText(dir.Combine("run", "implement", "copilot-events.jsonl")));
    }

    [Fact]
    public async Task Last_assistant_message_is_used_when_the_reply_is_empty()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime
        {
            OnTurn = (c, _, _) =>
            {
                c.Emit(new CopilotActivity.AssistantMessage("Final answer.", "gpt-5"));
                c.Emit(new CopilotActivity.AssistantMessage("subagent chatter", "gpt-5") { FromSubagent = true });
                return Task.FromResult<string?>(null);
            },
        };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);

        await Runner(backend).RunAsync(OneStage, Options(dir), Ct);

        Assert.Equal("Final answer.", File.ReadAllText(dir.Combine("run", "implement", "response.md")));
    }

    [Fact]
    public async Task Full_fidelity_thread_continues_one_session_and_switches_models()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime();
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions { DefaultModel = "gpt-5" }, runtime);

        var result = await Runner(backend).RunAsync("""
            digraph G {
              graph [default_fidelity="full"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan"]
              build [prompt="Build", llm_model="claude-opus-4-6", reasoning_effort="high"]
              start -> plan -> build -> exit
            }
            """, Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        var conversation = Assert.Single(runtime.Conversations);
        Assert.Equal("gpt-5", conversation.Options.Model);
        Assert.Equal(2, conversation.Prompts.Count);
        Assert.Equal([("claude-opus-4.6", "high")], conversation.ModelChanges);
        Assert.False(conversation.Disposed);
        var second = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "build", "backend.json")))!;
        Assert.True(second["resumed"]!.GetValue<bool>());

        await backend.DisposeAsync();
        Assert.True(conversation.Disposed);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task Separate_stages_get_separate_sessions()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime();
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);

        await Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="A"]
              b [prompt="B"]
              start -> a -> b -> exit
            }
            """, Options(dir), Ct);

        Assert.Equal(2, runtime.Conversations.Count);
        Assert.All(runtime.Conversations, c => Assert.True(c.Disposed));
        Assert.Null(runtime.Created[0].Model);
        Assert.Null(runtime.Created[0].ReasoningEffort);
    }

    [Fact]
    public async Task Cancellation_aborts_the_copilot_turn()
    {
        using var dir = new TempDir();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeCopilotRuntime
        {
            OnTurn = async (_, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            },
        };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = Runner(backend).RunAsync(OneStage, Options(dir), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(result.Cancelled);
        Assert.Equal(1, runtime.Conversations[0].Aborts);
    }

    [Fact]
    public async Task Permanent_errors_fail_without_retries_and_transient_errors_retry()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime
        {
            OnTurn = (c, prompt, _) =>
            {
                c.Emit(prompt.Contains("flaky stage", StringComparison.Ordinal)
                    ? new CopilotActivity.Error("Service unavailable", "server", 503)
                    : new CopilotActivity.Error("Bad credentials", "authentication", 401));
                return Task.FromResult<string?>(null);
            },
        };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);

        var result = await Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              auth [prompt="auth stage", max_retries=2]
              flaky [prompt="flaky stage", max_retries=1]
              start -> auth
              auth -> flaky
              flaky -> exit
            }
            """, Options(dir), Ct);

        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["auth"].Status);
        Assert.Contains("Bad credentials", result.NodeOutcomes["auth"].FailureReason);
        Assert.Single(runtime.Conversations, c => c.Prompts.Any(p => p.Contains("auth stage", StringComparison.Ordinal)));
        Assert.Equal(2, runtime.Conversations.Count(c => c.Prompts.Any(p => p.Contains("flaky stage", StringComparison.Ordinal))));
        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["flaky"].Status);
    }

    [Fact]
    public async Task Tool_hooks_and_extra_directories_reach_the_session()
    {
        using var dir = new TempDir();
        using var logs = new TempDir();
        ToolHookSnapshot? hooks = null;
        var runtime = new FakeCopilotRuntime
        {
            OnTurn = (c, _, _) =>
            {
                var settings = c.Options.Hooks();
                hooks = new ToolHookSnapshot(settings?.PreCommand, settings?.PostCommand, settings?.NodeId);
                return Task.FromResult<string?>("ok");
            },
        };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);

        await Runner(backend).RunAsync("""
            digraph G {
              graph [tool_hooks.pre="audit-pre", tool_hooks.post="audit-post"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it", tool_hooks.pre="node-pre"]
              start -> implement -> exit
            }
            """, Options(dir, logsRoot: logs.Combine("run")), Ct);

        Assert.Equal(new ToolHookSnapshot("node-pre", "audit-post", "implement"), hooks);
        Assert.Equal([logs.Combine("run", "implement")], runtime.Created[0].AdditionalDirectories);
    }

    [Theory]
    [InlineData(429, null, "rate limited", true)]
    [InlineData(503, null, "unavailable", true)]
    [InlineData(401, null, "bad credentials", false)]
    [InlineData(null, "authentication", "token expired", false)]
    [InlineData(null, "quota", "out of credits", false)]
    [InlineData(null, null, "Model x is not available", false)]
    [InlineData(null, null, "socket closed", true)]
    public void Error_retryability_follows_status_type_and_message(int? status, string? type, string message, bool retryable) =>
        Assert.Equal(retryable, CopilotSdkBackend.IsRetryable(new CopilotActivity.Error(message, type, status)));

    private static List<CopilotModelInfo> Catalog() =>
    [
        new("gpt-5-mini", "GPT-5 mini", "enabled", 0, 264_000, ["low", "medium", "high"]),
        new("claude-sonnet-5", "Claude Sonnet 5", "enabled", 1, 1_000_000, ["low", "medium", "high", "xhigh", "max"]),
        new("claude-haiku-4.5", "Claude Haiku 4.5", "enabled", 0.33, 200_000, []),
        new("legacy-model", "Legacy", "disabled", 1, 100_000, []),
    ];

    [Fact]
    public async Task Unavailable_models_fail_fast_without_starting_a_session()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime { Models = Catalog() };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);

        var result = await Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="A", llm_model="claude-sonnet-4.5", max_retries=2]
              b [prompt="B", llm_model="legacy-model"]
              start -> a -> b -> exit
            }
            """, Options(dir), Ct);

        Assert.Empty(runtime.Conversations);
        Assert.Contains("'claude-sonnet-4.5' is not available", result.NodeOutcomes["a"].FailureReason);
        Assert.Contains("claude-sonnet-5, gpt-5-mini", result.NodeOutcomes["a"].FailureReason);
        Assert.DoesNotContain("legacy-model,", result.NodeOutcomes["a"].FailureReason);
        Assert.Contains("disabled by policy", result.NodeOutcomes["b"].FailureReason);
        Assert.Equal(1, runtime.ListCalls);
    }

    [Fact]
    public async Task Verified_models_use_catalog_ids_and_drop_unsupported_efforts()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime { Models = Catalog() };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);

        await Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="A", llm_model="Claude-Sonnet-5", reasoning_effort="max"]
              b [prompt="B", llm_model="claude-haiku-4-5", reasoning_effort="high"]
              start -> a -> b -> exit
            }
            """, Options(dir), Ct);

        Assert.Equal("claude-sonnet-5", runtime.Created[0].Model);
        Assert.Equal("max", runtime.Created[0].ReasoningEffort);
        Assert.Equal("claude-haiku-4.5", runtime.Created[1].Model);
        Assert.Null(runtime.Created[1].ReasoningEffort);
    }

    [Fact]
    public async Task Model_substitution_by_the_runtime_is_reported()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime
        {
            Models = Catalog(),
            OnTurn = (c, _, _) =>
            {
                c.Emit(new CopilotActivity.AssistantMessage("done", "claude-opus-4.8"));
                return Task.FromResult<string?>("done");
            },
        };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);
        var events = new List<PipelineEvent>();

        await Runner(backend).RunAsync(OneStage.Replace("claude-sonnet-4-5", "gpt-5-mini", StringComparison.Ordinal), Options(dir, events), Ct);

        Assert.Contains(Logs(events, "implement"), l => l.Contains("ran on 'claude-opus-4.8' instead of the requested 'gpt-5-mini'", StringComparison.Ordinal));
        var metadata = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "implement", "backend.json")))!;
        Assert.Equal("claude-opus-4.8", metadata["model"]!.GetValue<string>());
        Assert.Equal("gpt-5-mini", metadata["requested_model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Model_verification_is_skipped_when_the_runtime_cannot_list_models()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime();
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions(), runtime);
        var events = new List<PipelineEvent>();

        var result = await Runner(backend).RunAsync(OneStage, Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Contains(Logs(events, "implement"), l => l.Contains("requested models are not verified", StringComparison.Ordinal));
        Assert.Equal("claude-sonnet-4.5", runtime.Created[0].Model);
    }

    [Fact]
    public async Task Steering_file_is_sent_to_the_running_turn()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime
        {
            OnTurn = async (c, _, ct) =>
            {
                await File.WriteAllTextAsync(dir.Combine("run", "implement", "steering.md"), "Use the existing helper.\n", ct);
                var steering = await c.FirstSteering.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return "done after: " + steering;
            },
        };
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions { SteeringPollInterval = TimeSpan.FromMilliseconds(20) }, runtime);
        var events = new List<PipelineEvent>();

        var result = await Runner(backend).RunAsync(OneStage, Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["Use the existing helper."], runtime.Conversations[0].Steered);
        Assert.Equal("done after: Use the existing helper.", File.ReadAllText(dir.Combine("run", "implement", "response.md")));
        Assert.False(File.Exists(dir.Combine("run", "implement", "steering.md")));
        Assert.Equal("Use the existing helper.\n", File.ReadAllText(dir.Combine("run", "implement", "steering.consumed-1.md")));
        Assert.Contains(Logs(events, "implement"), l => l.StartsWith("steering sent to Copilot: Use the existing helper.", StringComparison.Ordinal));
        Assert.Contains("\"type\":\"steering\"", File.ReadAllText(dir.Combine("run", "implement", "copilot-events.jsonl")));
    }

    [Fact]
    public async Task Steering_that_cannot_be_sent_is_logged_and_the_stage_continues()
    {
        using var dir = new TempDir();
        var runtime = new FakeCopilotRuntime
        {
            OnTurn = async (c, _, ct) =>
            {
                await File.WriteAllTextAsync(dir.Combine("run", "implement", "steering.md"), "Stop and summarize.", ct);
                await c.SteerAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return "finished";
            },
        };
        runtime.OnCreated = c => c.SteerError = new InvalidOperationException("session is closing");
        await using var backend = new CopilotSdkBackend(new CopilotSdkBackendOptions { SteeringPollInterval = TimeSpan.FromMilliseconds(20) }, runtime);
        var events = new List<PipelineEvent>();

        var result = await Runner(backend).RunAsync(OneStage, Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Empty(runtime.Conversations[0].Steered);
        Assert.Contains(Logs(events, "implement"), l => l.Contains("could not send steering: session is closing", StringComparison.Ordinal));
    }

    private sealed record ToolHookSnapshot(string? Pre, string? Post, string? NodeId);
}

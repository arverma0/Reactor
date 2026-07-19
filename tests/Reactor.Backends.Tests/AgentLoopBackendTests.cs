using System.Text.Json.Nodes;
using Reactor.Agent;
using Reactor.Agent.Tests.Session;
using Reactor.Backends.Tests.Support;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Llm;

namespace Reactor.Backends.Tests;

public sealed class AgentLoopBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RetryPolicy FastRetry = new() { MaxRetries = 1, BaseDelay = 0.001, MaxDelay = 1, Jitter = false };

    private static (AgentLoopBackend Backend, FakeLlm Llm) Create(Func<string, string, IProviderProfile>? profiles = null, TimeSpan? steeringPoll = null)
    {
        var llm = new FakeLlm("test");
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["test"] = llm }, "test");
        var backend = new AgentLoopBackend(new AgentLoopBackendOptions
        {
            Client = client,
            DefaultProvider = "test",
            DefaultModel = "test-model",
            ProfileFactory = profiles,
            SessionConfig = new SessionConfig { LlmRetryPolicy = FastRetry },
            SteeringPollInterval = steeringPoll ?? TimeSpan.FromMilliseconds(50),
        });
        return (backend, llm);
    }

    private static string FirstUser(Request request) => request.Messages.First(m => m.Role == Role.User).Text;

    private static int ToolResultCount(Request request) => request.Messages.Count(m => m.Role == Role.Tool);

    private static string Args(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public async Task Agent_stage_uses_real_tools_and_records_artifacts()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        llm.Responder = r => ToolResultCount(r) == 0
            ? FakeLlm.ToolCalls(("c1", "write_file", Args(new { file_path = "hello.txt", content = "hi there" })))
            : FakeLlm.Text("Created hello.txt");
        var events = new List<PipelineEvent>();

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              graph [goal="Greet"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Create hello.txt to $goal"]
              start -> implement -> exit
            }
            """, Pipelines.Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal("hi there", File.ReadAllText(dir.Combine("hello.txt")));
        var stage = dir.Combine("run", "implement");
        Assert.Equal("Created hello.txt", File.ReadAllText(Path.Combine(stage, "response.md")));
        var prompt = File.ReadAllText(Path.Combine(stage, "prompt.md"));
        Assert.Contains("Create hello.txt to Greet", prompt);
        Assert.Contains("Reporting the stage outcome", prompt);
        var backendJson = JsonNode.Parse(File.ReadAllText(Path.Combine(stage, "backend.json")))!;
        Assert.Equal("agent", backendJson["backend"]!.GetValue<string>());
        Assert.Equal("test", backendJson["provider"]!.GetValue<string>());
        Assert.Equal("test-model", backendJson["model"]!.GetValue<string>());
        Assert.Equal(1, backendJson["tool_calls"]!.GetValue<int>());
        Assert.Equal(43, backendJson["total_tokens"]!.GetValue<int>());
        Assert.Contains("-> write_file", File.ReadAllText(Path.Combine(stage, "transcript.md")));
        Assert.Contains("ToolCallStart", File.ReadAllText(Path.Combine(stage, "agent-events.jsonl")));
        Assert.Contains(Pipelines.Logs(events, "implement"), m => m.StartsWith("→ write_file hello.txt", StringComparison.Ordinal));

        // The system prompt carries the OpenAI-family profile for an unknown provider, with the real working directory.
        var system = llm.Requests[0].Messages[0].Text;
        Assert.Contains("Working directory: " + dir.Path, system);
        Assert.Contains(llm.Requests[0].Tools!, t => t.Name == "apply_patch");
    }

    [Fact]
    public async Task Agent_written_status_file_drives_routing()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        var statusPath = dir.Combine("run", "implement", "status.json");
        llm.Responder = r =>
        {
            if (FirstUser(r).Contains("Fix it", StringComparison.Ordinal))
            {
                return FakeLlm.Text("fixed");
            }

            return ToolResultCount(r) == 0
                ? FakeLlm.ToolCalls(("c1", "write_file", Args(new
                {
                    file_path = statusPath,
                    content = """{"outcome":"fail","failure_reason":"tests are failing","context_updates":{"failing_tests":"3"}}""",
                })))
                : FakeLlm.Text("I could not make the tests pass.");
        };

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Implement it"]
              fixer [prompt="Fix it"]
              start -> implement
              implement -> exit [condition="outcome=success"]
              implement -> fixer [condition="outcome=fail"]
              fixer -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Contains("fixer", result.CompletedNodes);
        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["implement"].Status);
        Assert.Equal("tests are failing", result.NodeOutcomes["implement"].FailureReason);
        Assert.Equal("3", result.Context["failing_tests"]?.ToString());
    }

    [Fact]
    public async Task Full_fidelity_stages_on_one_thread_share_the_session()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        llm.Responder = r => FakeLlm.Text("done: " + r.Messages.Last(m => m.Role == Role.User).Text[..10]);

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              graph [default_fidelity="full"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="First task: plan"]
              b [prompt="Second task: build"]
              start -> a -> b -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        var second = llm.Requests.Single(r => r.Messages.Any(m => m.Role == Role.User && m.Text.StartsWith("Second task", StringComparison.Ordinal)));
        Assert.Contains(second.Messages, m => m.Role == Role.User && m.Text.StartsWith("First task", StringComparison.Ordinal));
        Assert.Contains(second.Messages, m => m.Role == Role.Assistant);
        var sessionA = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "a", "backend.json")))!["session_id"]!.GetValue<string>();
        var sessionB = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "b", "backend.json")))!["session_id"]!.GetValue<string>();
        Assert.Equal(sessionA, sessionB);
        Assert.Equal(1, backend.ThreadSessionCount);
    }

    [Fact]
    public async Task Non_full_fidelity_stages_get_fresh_sessions()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        llm.Responder = _ => FakeLlm.Text("ok");

        await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="First task: plan"]
              b [prompt="Second task: build"]
              start -> a -> b -> exit
            }
            """, Pipelines.Options(dir), Ct);

        var second = llm.Requests[1];
        Assert.DoesNotContain(second.Messages, m => m.Role == Role.Assistant);
        Assert.Equal(2, second.Messages.Count);
        Assert.Equal(0, backend.ThreadSessionCount);
    }

    [Fact]
    public async Task Node_model_provider_and_reasoning_effort_select_the_profile()
    {
        using var dir = new TempDir();
        var anthropic = new FakeLlm("anthropic");
        anthropic.Responder = _ => FakeLlm.Text("ok");
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["anthropic"] = anthropic }, "anthropic");
        var backend = new AgentLoopBackend(new AgentLoopBackendOptions { Client = client });

        await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              review [prompt="Review", llm_model="claude-sonnet-4-5", reasoning_effort="medium"]
              start -> review -> exit
            }
            """, Pipelines.Options(dir), Ct);

        var request = Assert.Single(anthropic.Requests);
        Assert.Equal("claude-sonnet-4-5", request.Model);
        Assert.Equal("anthropic", request.Provider);
        Assert.Equal("medium", request.ReasoningEffort);
        Assert.Contains(request.Tools!, t => t.Name == "edit_file");
        Assert.DoesNotContain(request.Tools!, t => t.Name == "apply_patch");
    }

    [Fact]
    public async Task Stylesheet_assigned_models_reach_the_agent()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        llm.Responder = _ => FakeLlm.Text("ok");

        await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              graph [model_stylesheet="* { llm_provider: test; llm_model: base-model; } .hard { llm_model: big-model; reasoning_effort: low; }"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              easy [prompt="Easy"]
              tough [prompt="Tough", class="hard"]
              start -> easy -> tough -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(["base-model", "big-model"], llm.Requests.Select(r => r.Model));
        Assert.Equal(["high", "low"], llm.Requests.Select(r => r.ReasoningEffort));
    }

    [Fact]
    public async Task Tool_hooks_can_block_tools_and_audit_calls()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        llm.Responder = r => ToolResultCount(r) == 0
            ? FakeLlm.ToolCalls(
                ("c1", "shell", Args(new { command = "echo should-not-run" })),
                ("c2", "write_file", Args(new { file_path = "x.txt", content = "x" })))
            : FakeLlm.Text("done");
        var graph = Pipelines.Runner(backend).Prepare("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it"]
              start -> implement -> exit
            }
            """).Graph;
        graph.Attributes["tool_hooks.pre"] = OperatingSystem.IsWindows()
            ? "if \"%REACTOR_TOOL_NAME%\"==\"shell\" (echo shell is not allowed 1>&2 & exit /b 1) else (exit /b 0)"
            : "if [ \"$REACTOR_TOOL_NAME\" = shell ]; then echo 'shell is not allowed' >&2; exit 1; fi";
        graph.Attributes["tool_hooks.post"] = OperatingSystem.IsWindows()
            ? "echo %REACTOR_HOOK% %REACTOR_TOOL_NAME% %REACTOR_TOOL_IS_ERROR%>> hooks.log"
            : "echo \"$REACTOR_HOOK $REACTOR_TOOL_NAME $REACTOR_TOOL_IS_ERROR\" >> hooks.log";
        var events = new List<PipelineEvent>();

        var result = await Pipelines.Runner(backend).RunAsync(graph, Pipelines.Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        var toolResults = llm.Requests[1].Messages.Where(m => m.Role == Role.Tool).SelectMany(m => m.Content).Select(p => p.ToolResult!).ToList();
        Assert.True(toolResults[0].IsError);
        Assert.Contains("pre-hook exited with code 1", toolResults[0].ContentText);
        Assert.Contains("shell is not allowed", toolResults[0].ContentText);
        Assert.False(toolResults[1].IsError);
        Assert.True(File.Exists(dir.Combine("x.txt")));
        var audit = File.ReadAllText(dir.Combine("hooks.log"));
        Assert.Contains("post write_file false", audit);
        Assert.Contains("post shell true", audit);
        Assert.Contains(Pipelines.Logs(events, "implement"), m => m.Contains("tool_hooks.pre blocked shell", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_steering_file_is_injected_into_the_running_agent()
    {
        using var dir = new TempDir();
        var steeringTool = new RegisteredTool(
            new ToolDefinition("pause", "Wait a moment.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{}}""")!),
            async (_, ctx) =>
            {
                await File.WriteAllTextAsync(dir.Combine("run", "implement", "steering.md"), "Focus on the failing test first.", ctx.CancellationToken);
                for (var i = 0; i < 100 && File.Exists(dir.Combine("run", "implement", "steering.md")); i++)
                {
                    await Task.Delay(20, ctx.CancellationToken);
                }

                await Task.Delay(50, ctx.CancellationToken);
                return "paused";
            });
        var (backend, llm) = Create(profiles: (provider, model) =>
        {
            var profile = ProviderProfiles.Create(provider, model);
            profile.ToolRegistry.Register(steeringTool);
            return profile;
        });
        llm.Responder = r => ToolResultCount(r) == 0 ? FakeLlm.ToolCalls(("c1", "pause", "{}")) : FakeLlm.Text("done");
        var events = new List<PipelineEvent>();

        await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it"]
              start -> implement -> exit
            }
            """, Pipelines.Options(dir, events), Ct);

        Assert.Equal("Focus on the failing test first.", llm.Requests[1].Messages[^1].Text);
        Assert.True(File.Exists(dir.Combine("run", "implement", "steering.consumed-1.md")));
        Assert.Contains(Pipelines.Logs(events, "implement"), m => m.Contains("steering", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turn_limit_without_status_file_fails_the_stage()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        var n = 0;
        llm.Responder = _ => FakeLlm.ToolCalls(($"c{Interlocked.Increment(ref n)}", "glob", Args(new { pattern = $"*.{n}" })));

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it", agent.max_tool_rounds=2]
              start -> implement -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(2, llm.Requests.Count);
        var outcome = result.NodeOutcomes["implement"];
        Assert.Equal(StageStatus.Fail, outcome.Status);
        Assert.Contains("turn limit", outcome.FailureReason);
        var backendJson = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "implement", "backend.json")))!;
        Assert.Equal("max_tool_rounds_per_input", backendJson["stopped_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Authentication_errors_fail_the_stage_without_retries()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        llm.Responder = _ => throw new AuthenticationError("bad key", "test", 401);

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it", max_retries=3]
              start -> implement -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Single(llm.Requests);
        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["implement"].Status);
        Assert.Contains("bad key", result.NodeOutcomes["implement"].FailureReason);
    }

    [Fact]
    public async Task Transient_errors_are_retried_by_the_engine()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        var calls = 0;
        llm.Responder = _ => Interlocked.Increment(ref calls) <= 2 ? throw new ServerError("overloaded", "test", 503) : FakeLlm.Text("recovered");

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it", max_retries=2]
              start -> implement -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Equal("recovered", File.ReadAllText(dir.Combine("run", "implement", "response.md")));
    }

    [Fact]
    public async Task Cancelling_the_run_aborts_the_agent_promptly()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.BeforeRespond = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        llm.Responder = _ => FakeLlm.Text("never");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it"]
              start -> implement -> exit
            }
            """, Pipelines.Options(dir), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(result.Cancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Node_timeout_cancels_a_slow_agent()
    {
        using var dir = new TempDir();
        var (backend, llm) = Create();
        llm.BeforeRespond = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        llm.Responder = _ => FakeLlm.Text("never");

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              implement [prompt="Do it", timeout="300ms"]
              start -> implement -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["implement"].Status);
        Assert.Contains("timed out", result.NodeOutcomes["implement"].FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_resolution_prefers_node_settings_then_defaults_then_catalog()
    {
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["anthropic"] = new FakeLlm("anthropic") }, "anthropic");
        static StageSettings Stage(string? provider, string? model) => new() { NodeId = "n", Provider = provider, Model = model, WorkingDirectory = "." };

        Assert.Equal(("openai", "gpt-5.2"), ModelResolution.Resolve(Stage(null, "gpt-5.2"), client, null, null));
        Assert.Equal(("custom", "my-model"), ModelResolution.Resolve(Stage("custom", "my-model"), client, null, null));
        Assert.Equal(("anthropic", "claude-opus-4-6"), ModelResolution.Resolve(Stage(null, null), client, null, "claude-opus-4-6"));
        var (provider, latest) = ModelResolution.Resolve(Stage("anthropic", null), client, null, "gpt-5.2");
        Assert.Equal("anthropic", provider);
        Assert.StartsWith("claude-", latest);
        Assert.Throws<ConfigurationError>(() => ModelResolution.Resolve(Stage("nobody", null), client, null, null));
    }
}

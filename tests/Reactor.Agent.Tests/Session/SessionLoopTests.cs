using System.Diagnostics;
using System.Text.Json.Nodes;
using Reactor.Llm;
using static Reactor.Agent.Tests.Session.FakeLlm;
using static Reactor.Agent.Tests.Session.SessionHarness;

namespace Reactor.Agent.Tests.Session;

public sealed class SessionLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Text_only_response_completes_naturally_with_streaming_events()
    {
        var (session, llm, _, events) = Create();
        llm.ThenText("All done here.");

        await session.SubmitAsync("Say something", Ct);

        Assert.Equal(SessionState.Idle, session.State);
        Assert.Collection(session.History,
            t => Assert.Equal("Say something", Assert.IsType<UserTurn>(t).Content),
            t => Assert.Equal("All done here.", Assert.IsType<AssistantTurn>(t).Content));
        var kinds = Kinds(events);
        Assert.Equal(EventKind.SessionStart, kinds[0]);
        Assert.Equal(EventKind.UserInput, kinds[1]);
        Assert.Equal(EventKind.AssistantTextStart, kinds[2]);
        Assert.Contains(EventKind.AssistantTextDelta, kinds);
        Assert.Equal(EventKind.ProcessingEnd, kinds[^1]);
        var deltas = string.Concat(Of(events, EventKind.AssistantTextDelta).Select(e => e.GetString("delta")));
        Assert.Equal("All done here.", deltas);
        Assert.Equal("All done here.", Assert.Single(Of(events, EventKind.AssistantTextEnd)).GetString("text"));
        Assert.Equal(15, session.TotalUsage.TotalTokens);
    }

    [Fact]
    public async Task Non_streaming_mode_uses_complete()
    {
        var (session, llm, _, events) = Create(c => c.StreamResponses = false);
        llm.ThenTools(("c1", "echo", """{"text":"hi"}""")).ThenText("finished");

        await session.SubmitAsync("go", Ct);

        Assert.Equal("finished", session.LastAssistantText);
        Assert.DoesNotContain(EventKind.AssistantTextDelta, Kinds(events));
        Assert.Equal(2, Of(events, EventKind.AssistantTextEnd).Count);
    }

    [Fact]
    public async Task Tool_calls_execute_and_results_are_sent_back_to_the_model()
    {
        var (session, llm, _, events) = Create();
        llm.ThenTools(("call_1", "echo", """{"text":"hello"}""")).ThenText("done");

        await session.SubmitAsync("use the tool", Ct);

        Assert.Equal(2, llm.Requests.Count);
        var result = Assert.Single(ToolResults(llm.Requests[1]));
        Assert.Equal("call_1", result.ToolCallId);
        Assert.Equal("echo: hello", result.ContentText);
        Assert.False(result.IsError);

        // The assistant message with the tool call precedes the tool result in the second request.
        var assistant = llm.Requests[1].Messages.Last(m => m.Role == Role.Assistant);
        Assert.Equal("call_1", Assert.Single(assistant.ToolCalls).Id);

        var start = Assert.Single(Of(events, EventKind.ToolCallStart));
        Assert.Equal("echo", start.GetString("tool_name"));
        Assert.Equal("call_1", start.GetString("call_id"));
        var end = Assert.Single(Of(events, EventKind.ToolCallEnd));
        Assert.Equal("echo: hello", end.GetString("output"));
        Assert.IsType<ToolResultsTurn>(session.History[2]);
    }

    [Fact]
    public async Task Unknown_tool_returns_an_error_result_and_the_loop_continues()
    {
        var (session, llm, _, events) = Create();
        llm.ThenTools(("c1", "does_not_exist", "{}")).ThenText("recovered");

        await session.SubmitAsync("go", Ct);

        var result = Assert.Single(ToolResults(llm.Requests[1]));
        Assert.True(result.IsError);
        Assert.Equal("Unknown tool: does_not_exist", result.ContentText);
        Assert.Equal("recovered", session.LastAssistantText);
        Assert.Contains("Unknown tool", Assert.Single(Of(events, EventKind.ToolCallEnd)).GetString("error"));
    }

    [Fact]
    public async Task Schema_violations_return_validation_errors_without_executing()
    {
        var executed = false;
        var tools = TestTools((_, _) =>
        {
            executed = true;
            return Task.FromResult<ToolOutput>("ran");
        });
        tools.Register(new RegisteredTool(
            new ToolDefinition("strict", "Needs a number.", Schema("""{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"]}""")),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult<ToolOutput>("ran");
            }));
        var (session, llm, _, _) = Create(profile: Profile(tools));
        llm.ThenTools(("c1", "strict", """{"n":"not a number"}"""), ("c2", "echo", "{}")).ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var results = ToolResults(llm.Requests[1]);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.StartsWith("Invalid arguments for tool: strict", results[0].ContentText);
        Assert.StartsWith("Invalid arguments for tool: echo", results[1].ContentText);
        Assert.False(executed);
    }

    [Fact]
    public async Task Tool_exceptions_become_error_results()
    {
        var (session, llm, _, _) = Create();
        llm.ThenTools(("c1", "fail", "{}")).ThenText("handled");

        await session.SubmitAsync("go", Ct);

        var result = Assert.Single(ToolResults(llm.Requests[1]));
        Assert.True(result.IsError);
        Assert.Equal("Tool error (fail): boom", result.ContentText);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Parallel_tool_calls_run_concurrently_and_keep_call_order()
    {
        var running = 0;
        var maxConcurrent = 0;
        var tools = TestTools(async (args, ctx) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(150, ctx.CancellationToken);
            Interlocked.Decrement(ref running);
            return "value " + args["value"]!.GetValue<string>();
        });
        var (session, llm, _, _) = Create(profile: Profile(tools, parallel: true));
        llm.ThenTools(("a", "custom", """{"value":"1"}"""), ("b", "custom", """{"value":"2"}"""), ("c", "custom", """{"value":"3"}""")).ThenText("ok");

        await session.SubmitAsync("go", Ct);

        Assert.Equal(3, maxConcurrent);
        var results = ToolResults(llm.Requests[1]);
        Assert.Equal(["a", "b", "c"], results.Select(r => r.ToolCallId));
        Assert.Equal(["value 1", "value 2", "value 3"], results.Select(r => r.ContentText));
    }

    [Fact]
    public async Task Profiles_without_parallel_support_run_tools_sequentially()
    {
        var running = 0;
        var maxConcurrent = 0;
        var tools = TestTools(async (_, ctx) =>
        {
            InterlockedMax(ref maxConcurrent, Interlocked.Increment(ref running));
            await Task.Delay(30, ctx.CancellationToken);
            Interlocked.Decrement(ref running);
            return "ok";
        });
        var (session, llm, _, _) = Create(profile: Profile(tools, parallel: false));
        llm.ThenTools(("a", "custom", "{}"), ("b", "custom", "{}")).ThenText("ok");

        await session.SubmitAsync("go", Ct);

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task Large_tool_output_is_truncated_for_the_model_but_full_in_events()
    {
        var (session, llm, _, events) = Create();
        llm.ThenTools(("c1", "big", """{"size":100000}""")).ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var sent = Assert.Single(ToolResults(llm.Requests[1])).ContentText;
        Assert.True(sent.Length < 100_000);
        Assert.Contains("characters were removed from the middle", sent);
        Assert.Equal(100_000, Assert.Single(Of(events, EventKind.ToolCallEnd)).GetString("output")!.Length);
    }

    [Fact]
    public async Task Tool_round_limit_stops_the_loop_with_turn_limit_event()
    {
        var (session, llm, _, events) = Create(c => c.MaxToolRoundsPerInput = 3);
        var n = 0;
        llm.Responder = _ => ToolCalls(($"c{Interlocked.Increment(ref n)}", "echo", $$"""{"text":"{{n}}"}"""));

        await session.SubmitAsync("loop forever", Ct);

        Assert.Equal(3, llm.Requests.Count);
        Assert.Equal(3, session.History.OfType<ToolResultsTurn>().Count());
        var limit = Assert.Single(Of(events, EventKind.TurnLimit));
        Assert.Equal("max_tool_rounds_per_input", limit.GetString("limit"));
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Max_turns_limits_assistant_turns_across_inputs()
    {
        var (session, llm, _, events) = Create(c => c.MaxTurns = 3);
        var n = 0;
        llm.Responder = _ => ToolCalls(($"c{Interlocked.Increment(ref n)}", "echo", $$"""{"text":"{{n}}"}"""));

        await session.SubmitAsync("first", Ct);
        Assert.Equal(3, session.AssistantTurnCount);

        await session.SubmitAsync("second", Ct);
        Assert.Equal(3, session.AssistantTurnCount);
        Assert.Equal(2, Of(events, EventKind.TurnLimit).Count);
        Assert.All(Of(events, EventKind.TurnLimit), e => Assert.Equal("max_turns", e.GetString("limit")));
    }

    [Fact]
    public async Task Steering_before_submit_is_injected_after_the_user_message()
    {
        var (session, llm, _, events) = Create();
        llm.ThenText("ok");
        session.Steer("Prefer small changes.");

        await session.SubmitAsync("do the work", Ct);

        var users = llm.Requests[0].Messages.Where(m => m.Role == Role.User).Select(m => m.Text).ToList();
        Assert.Equal(["do the work", "Prefer small changes."], users);
        Assert.IsType<SteeringTurn>(session.History[1]);
        Assert.Equal("Prefer small changes.", Assert.Single(Of(events, EventKind.SteeringInjected)).GetString("content"));
    }

    [Fact]
    public async Task Steering_during_a_tool_round_is_injected_before_the_next_model_call()
    {
        Agent.Session? captured = null;
        var tools = TestTools((_, _) =>
        {
            captured!.Steer("Focus on tests now.");
            return Task.FromResult<ToolOutput>("tool ran");
        });
        var (session, llm, _, _) = Create(profile: Profile(tools));
        captured = session;
        llm.ThenTools(("c1", "custom", "{}")).ThenText("ok");

        await session.SubmitAsync("start", Ct);

        var second = llm.Requests[1].Messages;
        Assert.Equal(Role.Tool, second[^2].Role);
        Assert.Equal(Role.User, second[^1].Role);
        Assert.Equal("Focus on tests now.", second[^1].Text);
        Assert.Collection(session.History,
            t => Assert.IsType<UserTurn>(t),
            t => Assert.IsType<AssistantTurn>(t),
            t => Assert.IsType<ToolResultsTurn>(t),
            t => Assert.IsType<SteeringTurn>(t),
            t => Assert.IsType<AssistantTurn>(t));
    }

    [Fact]
    public async Task Follow_ups_are_processed_after_the_current_input()
    {
        var (session, llm, _, events) = Create();
        llm.ThenText("first answer").ThenText("second answer");
        session.FollowUp("and then this");

        await session.SubmitAsync("first", Ct);

        Assert.Equal(["first", "and then this"], session.History.OfType<UserTurn>().Select(u => u.Content));
        Assert.Equal("second answer", session.LastAssistantText);
        Assert.Equal(2, Of(events, EventKind.UserInput).Count);
        Assert.Single(Of(events, EventKind.ProcessingEnd));
    }

    [Fact]
    public async Task Repeating_tool_calls_trigger_loop_detection_warning()
    {
        var (session, llm, _, events) = Create(c => c.LoopDetectionWindow = 4);
        var n = 0;
        llm.Responder = _ => Interlocked.Increment(ref n) <= 4 ? ToolCalls(($"c{n}", "echo", """{"text":"same"}""")) : Text("stopping");

        await session.SubmitAsync("go", Ct);

        var detection = Assert.Single(Of(events, EventKind.LoopDetection));
        Assert.Contains("Loop detected", detection.GetString("message"));
        var steering = Assert.Single(session.History.OfType<SteeringTurn>());
        Assert.Contains("repeating pattern", steering.Content);
        Assert.Contains(llm.Requests[4].Messages, m => m.Role == Role.User && m.Text.StartsWith("Loop detected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loop_detection_can_be_disabled()
    {
        var (session, llm, _, events) = Create(c =>
        {
            c.LoopDetectionWindow = 3;
            c.EnableLoopDetection = false;
        });
        var n = 0;
        llm.Responder = _ => Interlocked.Increment(ref n) <= 5 ? ToolCalls(($"c{n}", "echo", """{"text":"same"}""")) : Text("done");

        await session.SubmitAsync("go", Ct);

        Assert.Empty(Of(events, EventKind.LoopDetection));
    }

    [Fact]
    public async Task Reasoning_effort_changes_apply_to_the_next_call()
    {
        var (session, llm, _, _) = Create(c => c.ReasoningEffort = "high");
        llm.ThenText("one").ThenText("two");

        await session.SubmitAsync("first", Ct);
        session.Config.ReasoningEffort = "low";
        await session.SubmitAsync("second", Ct);

        Assert.Equal("high", llm.Requests[0].ReasoningEffort);
        Assert.Equal("low", llm.Requests[1].ReasoningEffort);
    }

    [Fact]
    public async Task Authentication_error_closes_the_session_and_surfaces()
    {
        var (session, llm, env, events) = Create();
        llm.ThenThrow(new AuthenticationError("bad key", "test", 401));

        await Assert.ThrowsAsync<AuthenticationError>(() => session.SubmitAsync("go", Ct));

        Assert.Equal(SessionState.Closed, session.State);
        Assert.Single(llm.Requests);
        Assert.Equal(nameof(AuthenticationError), Assert.Single(Of(events, EventKind.Error)).GetString("error_type"));
        Assert.Equal(EventKind.SessionEnd, Kinds(events)[^1]);
        Assert.Equal(1, env.CleanupCount);
        Assert.IsType<AuthenticationError>(session.LastError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SubmitAsync("again", Ct));
    }

    [Fact]
    public async Task Retryable_errors_are_retried_before_any_output()
    {
        var (session, llm, _, events) = Create();
        llm.ThenThrow(new ServerError("overloaded", "test", 503)).ThenThrow(new RateLimitError("slow down", "test", 429)).ThenText("finally");

        await session.SubmitAsync("go", Ct);

        Assert.Equal(3, llm.Requests.Count);
        Assert.Equal("finally", session.LastAssistantText);
        Assert.Empty(Of(events, EventKind.Error));
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task Stream_failures_are_retried_even_after_output_started(int afterEvents, bool warns)
    {
        var (session, llm, _, events) = Create();
        llm.Responder = _ => Text("complete answer");
        llm.FailNextStream(afterEvents, new StreamError("connection reset"));

        await session.SubmitAsync("go", Ct);

        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal("complete answer", session.LastAssistantText);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal(warns, Of(events, EventKind.Warning).Any(e => e.GetString("message")!.Contains("interrupted", StringComparison.Ordinal)));
        Assert.Single(session.History.OfType<AssistantTurn>());
    }

    [Fact]
    public async Task Closing_while_the_session_is_starting_cancels_the_loop()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var env = new FakeEnvironment
        {
            CommandHandler = _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return new ExecResult { ExitCode = 128 };
            },
        };
        var (session, llm, _, events) = Create(env: env);
        llm.Responder = _ => Text("should not be called");

        var run = Task.Run(() => session.SubmitAsync("go", Ct), Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var close = session.CloseAsync();
        release.Set();

        await Assert.ThrowsAsync<AbortError>(() => run);
        await close;
        Assert.Empty(llm.Requests);
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Equal("closed", Assert.Single(Of(events, EventKind.SessionEnd)).GetString("reason"));
    }

    [Fact]
    public async Task Retries_exhausted_closes_the_session()
    {
        var (session, llm, _, events) = Create();
        llm.Responder = _ => throw new ServerError("down", "test", 500);

        await Assert.ThrowsAsync<ServerError>(() => session.SubmitAsync("go", Ct));

        Assert.Equal(3, llm.Requests.Count);
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Single(Of(events, EventKind.Error));
    }

    [Fact]
    public async Task Context_length_error_warns_and_keeps_the_session_usable()
    {
        var (session, llm, _, events) = Create();
        llm.ThenThrow(new ContextLengthError("too long", "test", 400)).ThenText("fine now");

        await session.SubmitAsync("huge input", Ct);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Contains(Of(events, EventKind.Warning), e => e.GetString("message")!.Contains("Context window exceeded", StringComparison.Ordinal));
        Assert.Empty(Of(events, EventKind.Error));

        await session.SubmitAsync("smaller", Ct);
        Assert.Equal("fine now", session.LastAssistantText);
    }

    [Fact]
    public async Task Context_usage_warning_fires_once_above_80_percent()
    {
        var (session, llm, _, events) = Create(profile: Profile(contextWindow: 1000));
        llm.ThenText("ok").ThenText("ok again");

        await session.SubmitAsync(new string('a', 3600), Ct);
        await session.SubmitAsync("more", Ct);

        var warnings = Of(events, EventKind.Warning).Where(e => e.GetString("message")!.StartsWith("Context usage", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(warnings);
        Assert.Contains("90%", warnings[0].GetString("message"));
    }

    [Fact]
    public async Task Question_without_tool_calls_moves_to_awaiting_input()
    {
        var (session, llm, _, _) = Create();
        llm.ThenText("Which database should I use?").ThenText("Using Postgres.");

        await session.SubmitAsync("set up storage", Ct);
        Assert.Equal(SessionState.AwaitingInput, session.State);

        await session.SubmitAsync("Postgres", Ct);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Abort_during_model_call_cancels_and_closes()
    {
        var (session, llm, env, events) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.BeforeRespond = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var run = session.SubmitAsync("long task", Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var sw = Stopwatch.StartNew();
        await session.AbortAsync();

        await Assert.ThrowsAsync<AbortError>(() => run);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"abort took {sw.Elapsed}");
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Single(Of(events, EventKind.SessionEnd));
        Assert.Equal(EventKind.SessionEnd, Kinds(events)[^1]);
        Assert.Equal(1, env.CleanupCount);
    }

    [Fact]
    public async Task Abort_during_tool_execution_cancels_the_tool()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolCancelled = false;
        var tools = TestTools(async (_, ctx) =>
        {
            toolStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                toolCancelled = true;
                throw;
            }

            return "never";
        });
        var (session, llm, _, _) = Create(profile: Profile(tools));
        llm.ThenTools(("c1", "custom", "{}")).ThenText("unused");

        var run = session.SubmitAsync("go", Ct);
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await session.AbortAsync();

        await Assert.ThrowsAsync<AbortError>(() => run);
        Assert.True(toolCancelled);
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_aborts_the_session()
    {
        var (session, llm, _, _) = Create();
        llm.BeforeRespond = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<AbortError>(() => session.SubmitAsync("go", cts.Token));
        Assert.Equal(SessionState.Closed, session.State);
    }

    [Fact]
    public async Task Close_emits_session_end_and_completes_event_readers()
    {
        var (session, llm, env, _) = Create();
        llm.ThenText("hi");
        var collected = new List<EventKind>();
        var reader = Task.Run(async () =>
        {
            await foreach (var evt in session.Events.ReadAllAsync(Ct))
            {
                collected.Add(evt.Kind);
            }
        }, Ct);
        await Task.Delay(50, Ct);

        await session.SubmitAsync("hello", Ct);
        await session.CloseAsync();
        await reader.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(EventKind.SessionEnd, collected[^1]);
        Assert.Contains(EventKind.SessionStart, collected);
        Assert.Equal(1, env.CleanupCount);
        await session.CloseAsync();
        Assert.Equal(1, env.CleanupCount);
    }

    [Fact]
    public async Task Concurrent_submissions_are_serialized()
    {
        var (session, llm, _, _) = Create();
        var active = 0;
        var maxActive = 0;
        llm.BeforeRespond = async (_, ct) =>
        {
            InterlockedMax(ref maxActive, Interlocked.Increment(ref active));
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref active);
        };
        llm.Responder = r => Text("answer to " + r.Messages.Last(m => m.Role == Role.User).Text);

        await Task.WhenAll(session.SubmitAsync("a", Ct), session.SubmitAsync("b", Ct), session.SubmitAsync("c", Ct));

        Assert.Equal(1, maxActive);
        Assert.Equal(3, session.History.OfType<UserTurn>().Count());
        Assert.Equal(6, session.History.Count);
    }

    [Fact]
    public async Task System_prompt_layers_base_environment_tools_project_docs_and_user_instructions()
    {
        var env = new FakeEnvironment();
        env.Files[env.ResolvePath("AGENTS.md")] = "Always run the tests.";
        env.Files[env.ResolvePath("CLAUDE.md")] = "Claude-specific note.";
        var (session, llm, _, _) = Create(c => c.UserInstructions = "User override wins.", env: env);
        llm.ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var system = llm.Requests[0].Messages[0];
        Assert.Equal(Role.System, system.Role);
        var text = system.Text;
        var order = new[] { "You are a test agent.", "<environment>", "Working directory: ", "# Available tools", "- echo: Echo text back.", "Always run the tests.", "User override wins." }
            .Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0, text));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.DoesNotContain("Claude-specific note.", text);
        Assert.Contains("Is git repository: false", text);
        Assert.Contains("Platform: linux", text);
    }

    [Fact]
    public async Task Git_context_is_captured_in_the_environment_block()
    {
        var env = new FakeEnvironment
        {
            CommandHandler = cmd => cmd switch
            {
                "git rev-parse --show-toplevel" => new ExecResult { Stdout = Path.GetFullPath("/work") + "\n" },
                "git rev-parse --abbrev-ref HEAD" => new ExecResult { Stdout = "feature/x\n" },
                "git status --porcelain" => new ExecResult { Stdout = " M a.cs\n?? b.cs\n M c.cs\n" },
                "git log --oneline -n 8" => new ExecResult { Stdout = "abc123 Fix bug\ndef456 Add feature\n" },
                _ => new ExecResult { ExitCode = 1 },
            },
        };
        var (session, llm, _, _) = Create(env: env);
        llm.ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var text = llm.Requests[0].Messages[0].Text;
        Assert.Contains("Is git repository: true", text);
        Assert.Contains("Git branch: feature/x", text);
        Assert.Contains("Git status: 2 modified, 1 untracked", text);
        Assert.Contains("- abc123 Fix bug", text);
    }

    [Fact]
    public async Task Interceptor_can_deny_a_tool_call()
    {
        var interceptor = new RecordingInterceptor(deny: "echo");
        var (session, llm, _, _) = Create(options: new SessionOptions { Interceptors = [interceptor] });
        llm.ThenTools(("c1", "echo", """{"text":"x"}"""), ("c2", "custom", """{"value":"y"}""")).ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var results = ToolResults(llm.Requests[1]);
        Assert.True(results[0].IsError);
        Assert.Equal("Tool call blocked: echo is not allowed", results[0].ContentText);
        Assert.False(results[1].IsError);
        Assert.Equal(["echo", "custom"], interceptor.Before.OrderBy(x => x == "custom").ToList());
        Assert.Contains(("echo", true), interceptor.After);
        Assert.Contains(("custom", false), interceptor.After);
    }

    [Fact]
    public async Task Tool_output_deltas_are_emitted()
    {
        var tools = TestTools((_, ctx) =>
        {
            ctx.OnOutputDelta?.Invoke("line 1\n");
            ctx.OnOutputDelta?.Invoke("line 2\n");
            return Task.FromResult<ToolOutput>("line 1\nline 2\n");
        });
        var (session, llm, _, events) = Create(profile: Profile(tools));
        llm.ThenTools(("c1", "custom", "{}")).ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var deltas = Of(events, EventKind.ToolCallOutputDelta);
        Assert.Equal(["line 1\n", "line 2\n"], deltas.Select(d => d.GetString("delta")));
        Assert.All(deltas, d => Assert.Equal("c1", d.GetString("call_id")));
    }

    [Fact]
    public async Task Thinking_parts_are_preserved_in_history_for_round_tripping()
    {
        var (session, llm, _, _) = Create();
        llm.Then(_ => new Response
        {
            Id = "r1",
            Model = "test-model",
            Message = new Message
            {
                Role = Role.Assistant,
                Content =
                [
                    ContentPart.ThinkingPart("pondering", "sig-123"),
                    ContentPart.ToolCallPart("c1", "echo", JsonNode.Parse("""{"text":"a"}""")),
                ],
            },
            FinishReason = FinishReason.ToolCalls,
            Usage = new Usage { InputTokens = 1, OutputTokens = 1, TotalTokens = 2 },
        }).ThenText("done");

        await session.SubmitAsync("go", Ct);

        var replayed = llm.Requests[1].Messages.First(m => m.Role == Role.Assistant);
        var thinking = Assert.Single(replayed.Content, p => p.Kind == ContentKind.Thinking);
        Assert.Equal("sig-123", thinking.Thinking!.Signature);
        Assert.Equal("pondering", Assert.IsType<AssistantTurn>(session.History[1]).Reasoning);
    }

    [Fact]
    public async Task Request_carries_model_provider_tools_and_auto_tool_choice()
    {
        var (session, llm, _, _) = Create();
        llm.ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var request = llm.Requests[0];
        Assert.Equal("test-model", request.Model);
        Assert.Equal("test", request.Provider);
        Assert.Equal(ToolChoice.Auto.Mode, request.ToolChoice!.Mode);
        var names = request.Tools!.Select(t => t.Name).ToList();
        Assert.Equal(["echo", "fail", "big", "custom", "spawn_agent", "send_input", "wait", "close_agent"], names);
    }

    [Fact]
    public async Task Transcript_renders_the_conversation()
    {
        var (session, llm, _, _) = Create();
        llm.ThenTools(("c1", "echo", """{"text":"hi"}""")).ThenText("bye");

        await session.SubmitAsync("hello", Ct);

        var transcript = session.Transcript();
        Assert.Contains("## User\nhello", transcript);
        Assert.Contains("-> echo {\"text\":\"hi\"}", transcript);
        Assert.Contains("<- echo: hi", transcript);
        Assert.Contains("bye", transcript);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class RecordingInterceptor(string deny) : IToolInterceptor
    {
        public List<string> Before { get; } = [];

        public List<(string Tool, bool IsError)> After { get; } = [];

        public Task<ToolInterceptResult> BeforeAsync(ToolCallInfo call, CancellationToken cancellationToken)
        {
            lock (Before)
            {
                Before.Add(call.ToolName);
            }

            return Task.FromResult(call.ToolName == deny ? ToolInterceptResult.Deny($"{deny} is not allowed") : ToolInterceptResult.Allow);
        }

        public Task AfterAsync(ToolCallInfo call, string output, bool isError, CancellationToken cancellationToken)
        {
            lock (After)
            {
                After.Add((call.ToolName, isError));
            }

            return Task.CompletedTask;
        }
    }
}

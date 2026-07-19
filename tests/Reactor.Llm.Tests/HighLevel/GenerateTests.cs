using System.Text.Json.Nodes;
using Reactor.Llm.Tests.Support;

namespace Reactor.Llm.Tests.HighLevel;

public class GenerateTests
{
    private static readonly RetryPolicy FastRetry = new() { MaxRetries = 2, BaseDelay = 0.001, Jitter = false };

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static (Client Client, ScriptedAdapter Adapter) Setup(string provider = "scripted")
    {
        var adapter = new ScriptedAdapter(provider);
        return (Client.Create(adapter), adapter);
    }

    [Fact]
    public async Task Simple_prompt_generates_text_with_system_prepended()
    {
        var (client, adapter) = Setup();
        adapter.ThenText("Hello there");

        var result = await Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "Say hi", System = "Be brief", Client = client });

        Assert.Equal("Hello there", result.Text);
        Assert.Single(result.Steps);
        var request = Assert.Single(adapter.Requests);
        Assert.Equal(Role.System, request.Messages[0].Role);
        Assert.Equal("Be brief", request.Messages[0].Text);
        Assert.Equal(Role.User, request.Messages[1].Role);
        Assert.Equal("scripted", request.Provider);
        Assert.Equal(15, result.TotalUsage.TotalTokens);
    }

    [Fact]
    public async Task Prompt_and_messages_together_is_rejected()
    {
        var (client, _) = Setup();
        await Assert.ThrowsAsync<ConfigurationError>(() => Ai.GenerateAsync(new GenerateOptions
        {
            Model = "m",
            Prompt = "x",
            Messages = [Message.User("y")],
            Client = client,
        }));
    }

    [Fact]
    public async Task Full_messages_list_is_sent_as_is()
    {
        var (client, adapter) = Setup();
        adapter.ThenText("ok");

        await Ai.GenerateAsync(new GenerateOptions
        {
            Model = "m",
            Messages = [Message.User("a"), Message.Assistant("b"), Message.User("c")],
            Client = client,
        });

        Assert.Equal(["a", "b", "c"], adapter.Requests[0].Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task Tool_loop_executes_active_tools_and_feeds_results_back()
    {
        var (client, adapter) = Setup();
        adapter.ThenToolCalls(("call_1", "get_weather", """{"city":"SF"}""")).ThenText("It is sunny in SF.");
        var weather = Tool.Create("get_weather", "Weather", Schema("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}"""),
            args => $"72F and sunny in {args["city"]}");

        var result = await Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "weather?", Tools = [weather], MaxToolRounds = 3, Client = client });

        Assert.Equal("It is sunny in SF.", result.Text);
        Assert.Equal(2, result.Steps.Count);
        var toolResult = Assert.Single(result.Steps[0].ToolResults);
        Assert.Equal("call_1", toolResult.ToolCallId);
        Assert.False(toolResult.IsError);
        Assert.Equal("72F and sunny in SF", toolResult.ContentText);

        var second = adapter.Requests[1];
        Assert.Equal(Role.Assistant, second.Messages[^2].Role);
        Assert.Equal(Role.Tool, second.Messages[^1].Role);
        Assert.Equal("call_1", second.Messages[^1].ToolCallId);
        Assert.Equal(15 + 28, result.TotalUsage.TotalTokens);
        Assert.Equal(15, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task Parallel_tool_calls_run_concurrently_and_results_preserve_order()
    {
        var (client, adapter) = Setup();
        adapter.ThenToolCalls(("c1", "slow", """{"ms":150}"""), ("c2", "slow", """{"ms":10}"""), ("c3", "slow", """{"ms":80}""")).ThenText("done");
        var active = 0;
        var maxActive = 0;
        var slow = Tool.Create("slow", "Sleeps", Schema("""{"type":"object","properties":{"ms":{"type":"integer"}}}"""), async (args, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(args["ms"]!.GetValue<int>(), ct);
            Interlocked.Decrement(ref active);
            return (object?)$"slept {args["ms"]}";
        });

        var result = await Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Tools = [slow], MaxToolRounds = 2, Client = client });

        Assert.True(maxActive > 1, "tool calls should execute concurrently");
        Assert.Equal(["c1", "c2", "c3"], result.Steps[0].ToolResults.Select(r => r.ToolCallId));
        Assert.Equal(["slept 150", "slept 10", "slept 80"], result.Steps[0].ToolResults.Select(r => r.ContentText));
        Assert.Equal(2, adapter.Requests.Count);
        Assert.Equal(3, adapter.Requests[1].Messages.Count(m => m.Role == Role.Tool));
    }

    [Fact]
    public async Task Unknown_tool_failing_tool_and_invalid_args_become_error_results()
    {
        var (client, adapter) = Setup();
        adapter.ThenToolCalls(
                ("u", "does_not_exist", "{}"),
                ("f", "fails", "{}"),
                ("v", "strict", """{"n":"not a number"}"""))
            .ThenText("recovered");
        var fails = Tool.Create("fails", "Always fails", Schema("""{"type":"object"}"""), _ => throw new InvalidOperationException("kaboom"));
        var strict = Tool.Create("strict", "Needs a number", Schema("""{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"]}"""), _ => "ok");

        var result = await Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Tools = [fails, strict], MaxToolRounds = 1, Client = client });

        var results = result.Steps[0].ToolResults;
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Contains("Unknown tool: does_not_exist", results[0].ContentText, StringComparison.Ordinal);
        Assert.Contains("kaboom", results[1].ContentText, StringComparison.Ordinal);
        Assert.Contains("Invalid arguments for tool strict", results[2].ContentText, StringComparison.Ordinal);
        Assert.Equal("recovered", result.Text);
    }

    [Fact]
    public async Task Repair_hook_fixes_invalid_arguments()
    {
        var (client, adapter) = Setup();
        adapter.ThenToolCalls(("v", "strict", """{"n":"5"}""")).ThenText("ok");
        var strict = Tool.Create("strict", "Needs a number", Schema("""{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"]}"""), args => $"n={args["n"]}");

        var result = await Ai.GenerateAsync(new GenerateOptions
        {
            Model = "m",
            Prompt = "go",
            Tools = [strict],
            Client = client,
            RepairToolCall = (call, _, _) => Task.FromResult<ToolCall?>(call with { Arguments = new JsonObject { ["n"] = 5 } }),
        });

        Assert.False(result.Steps[0].ToolResults[0].IsError);
        Assert.Equal("n=5", result.Steps[0].ToolResults[0].ContentText);
    }

    [Fact]
    public async Task Max_tool_rounds_zero_returns_tool_calls_without_execution()
    {
        var (client, adapter) = Setup();
        adapter.ThenToolCalls(("c1", "t", "{}"));
        var executed = false;
        var tool = Tool.Create("t", "t", Schema("""{"type":"object"}"""), _ =>
        {
            executed = true;
            return "x";
        });

        var result = await Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Tools = [tool], MaxToolRounds = 0, Client = client });

        Assert.False(executed);
        Assert.Single(result.ToolCalls);
        Assert.Single(adapter.Requests);
    }

    [Fact]
    public async Task Max_tool_rounds_bounds_total_llm_calls()
    {
        var (client, adapter) = Setup();
        for (var i = 0; i < 5; i++)
        {
            adapter.ThenToolCalls(($"c{i}", "t", "{}"));
        }

        var tool = Tool.Create("t", "t", Schema("""{"type":"object"}"""), _ => "x");
        var result = await Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Tools = [tool], MaxToolRounds = 2, Client = client });

        Assert.Equal(3, adapter.Requests.Count);
        Assert.Equal(3, result.Steps.Count);
        Assert.Empty(result.Steps[^1].ToolResults);
    }

    [Fact]
    public async Task Passive_tools_return_calls_to_the_caller()
    {
        var (client, adapter) = Setup();
        adapter.ThenToolCalls(("c1", "approve", """{"x":1}"""));
        var passive = Tool.Passive("approve", "Needs a human", Schema("""{"type":"object"}"""));

        var result = await Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Tools = [passive], MaxToolRounds = 5, Client = client });

        Assert.Single(adapter.Requests);
        Assert.Equal("approve", Assert.Single(result.ToolCalls).Name);
        Assert.Empty(result.ToolResults);
    }

    [Fact]
    public async Task Stop_when_ends_the_loop()
    {
        var (client, adapter) = Setup();
        adapter.ThenToolCalls(("c1", "t", "{}")).ThenToolCalls(("c2", "t", "{}")).ThenText("never");
        var tool = Tool.Create("t", "t", Schema("""{"type":"object"}"""), _ => "x");

        var result = await Ai.GenerateAsync(new GenerateOptions
        {
            Model = "m",
            Prompt = "go",
            Tools = [tool],
            MaxToolRounds = 10,
            StopWhen = steps => steps.Count >= 1,
            Client = client,
        });

        Assert.Single(result.Steps);
        Assert.Single(adapter.Requests);
    }

    [Fact]
    public async Task Transient_errors_are_retried_per_step()
    {
        var (client, adapter) = Setup();
        adapter.ThenThrow(new RateLimitError("slow down", "scripted")).ThenThrow(new ServerError("oops", "scripted")).ThenText("finally");
        var retries = new List<int>();

        var result = await Ai.GenerateAsync(new GenerateOptions
        {
            Model = "m",
            Prompt = "go",
            Client = client,
            RetryPolicy = FastRetry with { OnRetry = (_, attempt, _) => retries.Add(attempt) },
        });

        Assert.Equal("finally", result.Text);
        Assert.Equal([1, 2], retries);
    }

    [Fact]
    public async Task Non_retryable_errors_are_raised_immediately()
    {
        var (client, adapter) = Setup();
        adapter.ThenThrow(new AuthenticationError("bad key", "scripted")).ThenText("unreachable");

        await Assert.ThrowsAsync<AuthenticationError>(() => Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Client = client, RetryPolicy = FastRetry }));
        Assert.Single(adapter.Requests);
    }

    [Fact]
    public async Task Retry_after_beyond_max_delay_is_not_waited_for()
    {
        var (client, adapter) = Setup();
        adapter.ThenThrow(new RateLimitError("later", "scripted", retryAfter: 3600)).ThenText("unreachable");

        var error = await Assert.ThrowsAsync<RateLimitError>(() => Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Client = client, RetryPolicy = FastRetry }));
        Assert.Equal(3600, error.RetryAfter);
        Assert.Single(adapter.Requests);
    }

    [Fact]
    public async Task Max_retries_zero_disables_retries()
    {
        var (client, adapter) = Setup();
        adapter.ThenThrow(new ServerError("oops", "scripted")).ThenText("unreachable");

        await Assert.ThrowsAsync<ServerError>(() => Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Client = client, MaxRetries = 0 }));
        Assert.Single(adapter.Requests);
    }

    [Fact]
    public async Task Cancellation_raises_abort_error()
    {
        var (client, adapter) = Setup();
        adapter.Delay = TimeSpan.FromSeconds(10);
        adapter.ThenText("late");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<AbortError>(() => Ai.GenerateAsync(new GenerateOptions { Model = "m", Prompt = "go", Client = client }, cts.Token));
    }

    [Fact]
    public async Task Total_and_per_step_timeouts_raise_request_timeout_errors()
    {
        var (client, adapter) = Setup();
        adapter.Delay = TimeSpan.FromSeconds(10);
        adapter.ThenText("late").ThenText("late");

        await Assert.ThrowsAsync<RequestTimeoutError>(() => Ai.GenerateAsync(new GenerateOptions
        {
            Model = "m",
            Prompt = "go",
            Client = client,
            Timeout = TimeSpan.FromMilliseconds(50),
        }));

        await Assert.ThrowsAsync<RequestTimeoutError>(() => Ai.GenerateAsync(new GenerateOptions
        {
            Model = "m",
            Prompt = "go",
            Client = client,
            Timeout = new TimeoutConfig { PerStep = TimeSpan.FromMilliseconds(50) },
        }));
    }

    [Fact]
    public async Task Default_client_can_be_overridden()
    {
        var adapter = new ScriptedAdapter();
        adapter.ThenText("from default");
        Ai.SetDefaultClient(Client.Create(adapter));
        try
        {
            var result = await Ai.GenerateAsync("m", "hi");
            Assert.Equal("from default", result.Text);
        }
        finally
        {
            Ai.SetDefaultClient(null);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

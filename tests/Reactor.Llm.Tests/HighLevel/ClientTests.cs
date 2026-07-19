using Reactor.Llm.Tests.Support;

namespace Reactor.Llm.Tests.HighLevel;

public class ClientTests
{
    [Fact]
    public async Task Requests_route_by_provider_and_fall_back_to_the_default()
    {
        var a = new ScriptedAdapter("alpha").ThenText("from alpha").ThenText("default alpha");
        var b = new ScriptedAdapter("beta").ThenText("from beta");
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["alpha"] = a, ["beta"] = b }, defaultProvider: "alpha");

        Assert.Equal("from beta", (await client.CompleteAsync(new Request { Model = "m", Messages = [Message.User("x")], Provider = "beta" })).Text);
        Assert.Equal("from alpha", (await client.CompleteAsync(new Request { Model = "m", Messages = [Message.User("x")], Provider = "alpha" })).Text);
        Assert.Equal("default alpha", (await client.CompleteAsync(new Request { Model = "m", Messages = [Message.User("x")] })).Text);
        Assert.Equal("alpha", a.Requests[^1].Provider);
    }

    [Fact]
    public async Task Missing_provider_configuration_raises_configuration_error()
    {
        var empty = new Client();
        await Assert.ThrowsAsync<ConfigurationError>(() => empty.CompleteAsync(new Request { Model = "m", Messages = [Message.User("x")] }));

        var noDefault = new Client(new Dictionary<string, IProviderAdapter> { ["alpha"] = new ScriptedAdapter("alpha") });
        await Assert.ThrowsAsync<ConfigurationError>(() => noDefault.CompleteAsync(new Request { Model = "m", Messages = [Message.User("x")] }));
        await Assert.ThrowsAsync<ConfigurationError>(() => noDefault.CompleteAsync(new Request { Model = "m", Messages = [Message.User("x")], Provider = "nope" }));
        Assert.Throws<ConfigurationError>(() => new Client(defaultProvider: "ghost"));
    }

    [Fact]
    public async Task Middleware_runs_in_registration_order_for_requests_and_reverse_for_responses()
    {
        var log = new List<string>();
        ILlmMiddleware Named(string name) => LlmMiddleware.Create(complete: async (req, next, ct) =>
        {
            log.Add($"{name}:request");
            var response = await next(req, ct);
            log.Add($"{name}:response");
            return response;
        });

        var adapter = new ScriptedAdapter().ThenText("ok");
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["scripted"] = adapter }, "scripted", [Named("first"), Named("second")]);

        await client.CompleteAsync(new Request { Model = "m", Messages = [Message.User("x")] });

        Assert.Equal(["first:request", "second:request", "second:response", "first:response"], log);
    }

    [Fact]
    public async Task Middleware_can_modify_requests_and_observe_streams()
    {
        var observed = new List<StreamEventType>();
        var rewrite = LlmMiddleware.Create(complete: (req, next, ct) => next(req with { Model = "rewritten" }, ct));
        var adapter = new ScriptedAdapter().ThenText("a").ThenText("bb");
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["scripted"] = adapter }, "scripted",
            [rewrite, LlmMiddleware.ObserveStream((_, evt) => observed.Add(evt.Type))]);

        await client.CompleteAsync(new Request { Model = "original", Messages = [Message.User("x")] });
        Assert.Equal("rewritten", adapter.Requests[0].Model);

        await foreach (var _ in client.StreamAsync(new Request { Model = "m", Messages = [Message.User("x")] }))
        {
        }

        Assert.Equal(StreamEventType.StreamStart, observed[0]);
        Assert.Equal(StreamEventType.Finish, observed[^1]);
    }

    [Fact]
    public void From_env_registers_only_configured_providers_and_first_becomes_default()
    {
        var env = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = "sk-ant-test",
            ["GOOGLE_API_KEY"] = "g-test",
        };

        var client = Client.FromEnv(env);

        Assert.Equal(["anthropic", "gemini"], client.ProviderNames.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("anthropic", client.DefaultProvider);

        var none = Client.FromEnv(new Dictionary<string, string?>());
        Assert.Empty(none.ProviderNames);
        Assert.Null(none.DefaultProvider);

        var explicitDefault = Client.FromEnv(new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "sk", ["GEMINI_API_KEY"] = "g" }, defaultProvider: "gemini");
        Assert.Equal("gemini", explicitDefault.DefaultProvider);
    }

    [Fact]
    public void Model_catalog_lookups()
    {
        Assert.Equal("anthropic", ModelCatalog.GetModelInfo("claude-opus-4-6")!.Provider);
        Assert.Equal("claude-sonnet-4-5", ModelCatalog.GetModelInfo("sonnet")!.Id);
        Assert.Null(ModelCatalog.GetModelInfo("no-such-model"));
        Assert.All(ModelCatalog.ListModels("gemini"), m => Assert.Equal("gemini", m.Provider));
        Assert.Equal("claude-opus-4-6", ModelCatalog.GetLatestModel("anthropic")!.Id);
        Assert.Equal("gpt-5.2", ModelCatalog.GetLatestModel("openai", "reasoning")!.Id);
        Assert.Equal("openai", ModelCatalog.InferProvider("gpt-9-turbo"));
        Assert.Equal("anthropic", ModelCatalog.InferProvider("claude-future"));
        Assert.Equal("gemini", ModelCatalog.InferProvider("gemini-9"));
        Assert.Null(ModelCatalog.InferProvider("llama3"));
    }

    [Fact]
    public async Task Standalone_retry_utility_retries_and_reports()
    {
        var attempts = 0;
        var reported = new List<int>();
        var value = await Retry.ExecuteAsync(
            _ =>
            {
                attempts++;
                return attempts < 3 ? throw new NetworkError("flaky") : Task.FromResult(42);
            },
            new RetryPolicy { MaxRetries = 3, BaseDelay = 0.001, Jitter = false, OnRetry = (_, n, _) => reported.Add(n) });

        Assert.Equal(42, value);
        Assert.Equal([1, 2], reported);
    }

    [Fact]
    public void Backoff_delays_grow_exponentially_and_are_capped()
    {
        var policy = new RetryPolicy { BaseDelay = 1, BackoffMultiplier = 2, MaxDelay = 5, Jitter = false };
        Assert.Equal(TimeSpan.FromSeconds(1), policy.CalculateDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.CalculateDelay(3));

        var jittered = new RetryPolicy { BaseDelay = 1, Jitter = true };
        for (var i = 0; i < 50; i++)
        {
            var d = jittered.CalculateDelay(0).TotalSeconds;
            Assert.InRange(d, 0.5, 1.5);
        }
    }
}

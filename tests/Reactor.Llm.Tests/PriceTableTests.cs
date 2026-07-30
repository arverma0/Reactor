namespace Reactor.Llm.Tests;

public sealed class PriceTableTests
{
    [Fact]
    public void Built_in_table_is_sourced_and_finds_models_by_id_or_display_name()
    {
        var table = PriceTable.LoadCopilot(null);

        Assert.Null(table.LoadError);
        Assert.Equal(0.01, table.AiCreditUsd);
        Assert.Null(table.PremiumRequestUsd);
        Assert.Contains(table.Sources, s => s.Url.StartsWith("https://docs.github.com/", StringComparison.Ordinal) && s.Retrieved is { Length: > 0 });

        var opus = table.Find("claude-opus-4.8");
        Assert.NotNull(opus);
        Assert.Equal(new TokenRates { Input = 5.00, CachedInput = 0.50, CacheWrite = 6.25, Output = 25.00 }, opus.Rates);
        Assert.Same(opus, table.Find("Claude Opus 4.8"));
        Assert.Same(opus, table.Find("CLAUDE-OPUS-4.8"));
        Assert.Null(table.Find("no-such-model"));
        Assert.Null(table.Find(null));
        Assert.Equal("$5/$25", opus.RateText());
        Assert.Equal("$0.25/$2", table.Find("gpt-5-mini")!.RateText());

        // Every priced entry has both an input and an output rate; unsourced rates are absent, not zero.
        Assert.All(table.Models.Where(m => m.Input is not null || m.Output is not null), m => Assert.True(m.Rates.IsComplete, m.Id));
        Assert.Null(table.Find("gpt-5.1")!.Input);
        Assert.Equal(3, table.Find("gpt-5.1")!.LegacyPremiumMultiplier);
    }

    [Fact]
    public void Token_prices_reproduce_the_ai_units_the_copilot_runtime_reported()
    {
        // A real claude-sonnet-5 stage (examples/stylesheet.dot 'architecture' on the copilot backend): the runtime reported
        // 10,794,840,000 nano AIU for 8 uncached input, 59,512 cache-read, 22,444 cache-write, and 3,992 output tokens
        // (81,964 input in total).
        var sonnet = PriceTable.LoadCopilot(null).Find("claude-sonnet-5")!;

        var usd = sonnet.EstimateUsd(new TokenUsage(Input: 81_964, Output: 3_992, CacheRead: 59_512, CacheWrite: 22_444));

        Assert.NotNull(usd);
        Assert.Equal(10.79484, usd.Value / 0.01, 6);
    }

    [Fact]
    public void Long_context_rates_apply_per_request_above_the_threshold()
    {
        var sol = PriceTable.LoadCopilot(null).Find("gpt-5.6-sol")!;
        var usage = new TokenUsage(Input: 300_000, Output: 1_000);

        Assert.Equal((300_000 * 5.00 + 1_000 * 30.00) / 1e6, sol.EstimateUsd(usage)!.Value, 9);
        Assert.Equal((300_000 * 10.00 + 1_000 * 45.00) / 1e6, sol.EstimateUsd(usage, requestInputTokens: 300_000)!.Value, 9);
        Assert.Equal((300_000 * 5.00 + 1_000 * 30.00) / 1e6, sol.EstimateUsd(usage, requestInputTokens: 272_000)!.Value, 9);
    }

    [Fact]
    public void Anthropic_style_usage_prices_cache_tokens_on_top_of_input_and_missing_cache_rates_fall_back_to_input()
    {
        var price = new ModelPrice { Id = "m", Input = 2, Output = 10 };

        Assert.Equal((100 * 2.0 + 50 * 2.0 + 25 * 2.0 + 10 * 10.0) / 1e6, price.EstimateUsd(new TokenUsage(100, 10, CacheRead: 50, CacheWrite: 25, InputIncludesCache: false))!.Value, 12);
        Assert.Equal((25 * 2.0 + 50 * 2.0 + 25 * 2.0 + 10 * 10.0) / 1e6, price.EstimateUsd(new TokenUsage(100, 10, CacheRead: 50, CacheWrite: 25))!.Value, 12);
        Assert.Null(new ModelPrice { Id = "x", Input = 1 }.EstimateUsd(new TokenUsage(1, 1)));
    }

    [Fact]
    public void Override_file_replaces_and_adds_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reactor-prices-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // Comments are allowed.
              "premium_request_usd": 0.05,
              "models": [
                { "id": "claude-opus-4.8", "input": 1, "output": 2 },
                { "id": "my-model", "display_name": "My Model", "input": 0.5, "output": 1.5 },
              ]
            }
            """);
        try
        {
            var table = PriceTable.LoadCopilot(path);

            Assert.Null(table.LoadError);
            Assert.Equal(0.01, table.AiCreditUsd);
            Assert.Equal(0.05, table.PremiumRequestUsd);
            Assert.Equal(1, table.Find("claude-opus-4.8")!.Input);
            Assert.Null(table.Find("claude-opus-4.8")!.CachedInput);
            Assert.Equal(1.5, table.Find("My Model")!.Output);
            Assert.NotNull(table.Find("gpt-5.6-terra"));

            File.WriteAllText(path, """{ "replace": true, "models": [ { "id": "only", "input": 1, "output": 1 } ] }""");
            var replaced = PriceTable.LoadCopilot(path);
            Assert.Equal(["only"], replaced.Models.Select(m => m.Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unreadable_override_keeps_the_built_in_table_and_reports_why()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reactor-prices-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var table = PriceTable.LoadCopilot(path);

            Assert.NotNull(table.LoadError);
            Assert.Contains(PriceTable.OverrideVariable, table.LoadError, StringComparison.Ordinal);
            Assert.NotNull(table.Find("claude-opus-4.8"));
            Assert.Contains("missing", PriceTable.LoadCopilot(path + ".missing").LoadError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Catalog_prices_become_model_prices()
    {
        var sonnet = ModelPrice.FromCatalog(ModelCatalog.GetModelInfo("claude-sonnet-4-5"));

        Assert.NotNull(sonnet);
        Assert.Equal(3.0, sonnet.Input);
        Assert.Equal(15.0, sonnet.Output);
        Assert.Null(ModelPrice.FromCatalog(ModelCatalog.GetModelInfo("gpt-5.2")));
    }
}

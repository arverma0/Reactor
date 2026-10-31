namespace Reactor.Llm.Tests;

public sealed class PriceTableTests
{
    [Fact]
    public void Built_in_table_is_sourced_and_finds_models_by_id_or_display_name()
    {
        var table = PriceTable.LoadCopilot(null);

        Assert.Null(table.LoadError);
        Assert.Equal(0.01, table.AiCreditUsd);
        Assert.Equal(0.04, table.PremiumRequestUsd);
        Assert.Contains(table.Sources, s => s.Url.StartsWith("https://docs.github.com/", StringComparison.Ordinal) && s.Retrieved is { Length: > 0 });

        var opus = table.Find("claude-opus-5.5");
        Assert.NotNull(opus);
        Assert.Equal(new TokenRates { Input = 4.00, CachedInput = 0.20, CacheWrite = 5.00, Output = 20.00 }, opus.Rates);
        Assert.Same(opus, table.Find("Claude Opus 5.5"));
        Assert.Same(opus, table.Find("CLAUDE-OPUS-5.5"));
        Assert.Null(table.Find("no-such-model"));
        Assert.Null(table.Find(null));
        Assert.Equal("$4/$20", opus.RateText());
        Assert.Equal("$0.10/$0.50", table.Find("claude-haiku-5.5")!.RateText());

        // Every priced entry has both an input and an output rate; unsourced rates are absent, not zero.
        Assert.All(table.Models.Where(m => m.Input is not null || m.Output is not null), m => Assert.True(m.Rates.IsComplete, m.Id));
        Assert.Null(table.Find("gpt-5.1")!.Input);
        Assert.Equal(3, table.Find("gpt-5.1")!.LegacyPremiumMultiplier);
    }

    [Fact]
    public void Token_prices_reproduce_the_ai_units_the_copilot_cli_reported()
    {
        // A real claude-opus-5.5 stage (NEON 'design'): the CLI's usage checkpoint reported 152,722,960,000 nano AIU for
        // 22 uncached input, 521,733 cache-read, 80,591 cache-write, and 50,992 output tokens (602,346 input in total).
        var opus = PriceTable.LoadCopilot(null).Find("claude-opus-5.5")!;

        var usd = opus.EstimateUsd(new TokenUsage(Input: 602_346, Output: 50_992, CacheRead: 521_733, CacheWrite: 80_591));

        Assert.NotNull(usd);
        Assert.Equal(152.72296, usd.Value / 0.01, 6);
    }

    [Fact]
    public void Long_context_rates_apply_per_request_above_the_threshold()
    {
        var sol = PriceTable.LoadCopilot(null).Find("gpt-5.6-sol")!;
        var usage = new TokenUsage(Input: 300_000, Output: 1_000);

        Assert.Equal((300_000 * 4.00 + 1_000 * 20.00) / 1e6, sol.EstimateUsd(usage)!.Value, 9);
        Assert.Equal((300_000 * 8.00 + 1_000 * 30.00) / 1e6, sol.EstimateUsd(usage, requestInputTokens: 300_000)!.Value, 9);
        Assert.Equal((300_000 * 4.00 + 1_000 * 20.00) / 1e6, sol.EstimateUsd(usage, requestInputTokens: 272_000)!.Value, 9);
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
                { "id": "claude-opus-5.5", "input": 1, "output": 2 },
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
            Assert.Equal(1, table.Find("claude-opus-5.5")!.Input);
            Assert.Null(table.Find("claude-opus-5.5")!.CachedInput);
            Assert.Equal(1.5, table.Find("My Model")!.Output);
            Assert.NotNull(table.Find("gpt-6-sol"));

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
            Assert.NotNull(table.Find("claude-opus-5.5"));
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

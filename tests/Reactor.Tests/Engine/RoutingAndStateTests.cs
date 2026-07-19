using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;

namespace Reactor.Tests.Engine;

public class EdgeSelectionTests
{
    private static (Graph Graph, Node Node) Build(string edges)
    {
        var graph = DotParser.Parse($"digraph G {{ n [prompt=\"x\"] {edges} }}");
        return (graph, graph.Nodes["n"]);
    }

    private static string? Select(string edges, Outcome outcome, PipelineContext? context = null)
    {
        var (graph, node) = Build(edges);
        return EdgeSelector.Select(node, outcome, context ?? new PipelineContext(), graph)?.Edge.To;
    }

    [Fact]
    public void Condition_match_wins_over_weight()
    {
        Assert.Equal("cond", Select("n -> heavy [weight=100] n -> cond [condition=\"outcome=success\"]", Outcome.Success()));
    }

    [Fact]
    public void Among_condition_matches_weight_then_lexical_decides()
    {
        Assert.Equal("b", Select("n -> a [condition=\"outcome=success\"] n -> b [condition=\"outcome=success\", weight=2]", Outcome.Success()));
        Assert.Equal("a", Select("n -> b [condition=\"outcome=success\"] n -> a [condition=\"outcome=success\"]", Outcome.Success()));
    }

    [Fact]
    public void Weight_breaks_ties_for_unconditional_edges_and_lexical_order_is_final()
    {
        Assert.Equal("z", Select("n -> a n -> z [weight=5] n -> m [weight=1]", Outcome.Success()));
        Assert.Equal("alpha", Select("n -> charlie n -> alpha n -> bravo", Outcome.Success()));
    }

    [Fact]
    public void Preferred_label_matches_after_normalization()
    {
        var outcome = new Outcome { PreferredLabel = "  APPROVE " };
        Assert.Equal("ship", Select("n -> fix [label=\"[F] Fix\"] n -> ship [label=\"[A] Approve\"]", outcome));
        Assert.Equal("ship", Select("n -> fix [label=\"F) Fix\"] n -> ship [label=\"A - Approve\"]", outcome));
    }

    [Fact]
    public void Suggested_next_ids_are_used_when_no_label_matches()
    {
        var outcome = new Outcome { PreferredLabel = "nothing", SuggestedNextIds = ["ghost", "b"] };
        Assert.Equal("b", Select("n -> a [weight=9] n -> b", outcome));
    }

    [Fact]
    public void Preferred_label_and_suggestions_ignore_conditional_edges()
    {
        var outcome = new Outcome { PreferredLabel = "Fix", SuggestedNextIds = ["fix"] };
        Assert.Equal("other", Select("n -> fix [label=\"Fix\", condition=\"context.x=1\"] n -> other", outcome));
    }

    [Fact]
    public void No_edges_or_only_unmatched_conditions_select_nothing()
    {
        Assert.Null(Select("", Outcome.Success()));
        Assert.Null(Select("n -> a [condition=\"outcome=fail\"]", Outcome.Success()));
    }

    [Fact]
    public void Explicit_routing_only_skips_implicit_unconditional_edges()
    {
        var (graph, node) = Build("n -> a n -> b [label=\"Retry\"]");
        Assert.Null(EdgeSelector.Select(node, Outcome.Fail("x"), new PipelineContext(), graph, explicitRoutingOnly: true));
        Assert.Equal("b", EdgeSelector.Select(node, Outcome.Fail("x") with { PreferredLabel = "retry" }, new PipelineContext(), graph, explicitRoutingOnly: true)!.Edge.To);
    }

    [Theory]
    [InlineData("[Y] Yes, deploy", "Y")]
    [InlineData("Y) Yes, deploy", "Y")]
    [InlineData("Y - Yes, deploy", "Y")]
    [InlineData("Yes, deploy", "Y")]
    [InlineData("approve", "A")]
    public void Accelerator_keys_are_parsed(string label, string key)
    {
        Assert.Equal(key, Accelerators.ParseKey(label));
    }
}

public class StateTests
{
    [Fact]
    public void Context_is_thread_safe_and_normalizes_values()
    {
        var context = new PipelineContext();
        Parallel.For(0, 200, i => context.Set($"k{i}", i));
        Assert.Equal(200, context.Snapshot().Count);
        context.Set("flag", true);
        context.Set("list", new[] { 1, 2 });
        context.Set("map", new Dictionary<string, object> { ["a"] = 1.5 });
        Assert.Equal("true", context.GetString("flag"));
        Assert.Equal("[1,2]", context.GetString("list"));
        Assert.Equal("{\"a\":1.5}", context.GetString("map"));
        Assert.Equal("fallback", context.GetString("missing", "fallback"));
    }

    [Fact]
    public void Clone_is_a_deep_isolated_copy()
    {
        var context = new PipelineContext();
        context.Set("list", new List<object?> { "a" });
        context.AppendLog("one");
        var clone = context.Clone();
        clone.Set("only_clone", 1);
        ((List<object?>)clone.Get("list")!).Add("mutated-copy");
        clone.AppendLog("two");

        Assert.False(context.ContainsKey("only_clone"));
        Assert.Equal("[\"a\"]", context.GetString("list"));
        Assert.Single(context.Logs);
        Assert.Equal(2, clone.Logs.Count);
    }

    [Fact]
    public void Outcome_status_file_round_trips()
    {
        var outcome = new Outcome
        {
            Status = StageStatus.PartialSuccess,
            PreferredLabel = "Fix",
            SuggestedNextIds = ["a", "b"],
            ContextUpdates = new Dictionary<string, object?> { ["n"] = 2, ["nested.key"] = "v" },
            Notes = "notes",
            FailureReason = "why",
        };

        var parsed = Outcome.FromStatusJson(outcome.ToStatusJson());

        Assert.Equal(outcome.Status, parsed.Status);
        Assert.Equal(outcome.PreferredLabel, parsed.PreferredLabel);
        Assert.Equal(outcome.SuggestedNextIds, parsed.SuggestedNextIds);
        Assert.Equal(2L, parsed.ContextUpdates["n"]);
        Assert.Equal("v", parsed.ContextUpdates["nested.key"]);
        Assert.Equal("why", parsed.FailureReason);
        Assert.Throws<FormatException>(() => Outcome.FromStatusJson(System.Text.Json.Nodes.JsonNode.Parse("""{"notes":"no outcome"}""")));
    }

    [Fact]
    public void Artifact_store_keeps_small_items_in_memory_and_large_items_on_disk()
    {
        using var dir = new Support.TempDir();
        var store = new ArtifactStore(dir.Path, fileBackingThreshold: 1024);

        var small = store.Store("small", "Small", new Dictionary<string, object?> { ["x"] = 1 });
        var large = store.Store("large", "Large", new string('x', 5000));

        Assert.False(small.IsFileBacked);
        Assert.True(large.IsFileBacked);
        Assert.True(File.Exists(Path.Combine(dir.Path, "artifacts", "large.json")));
        Assert.Equal(new string('x', 5000), store.Retrieve("large"));
        Assert.Equal(2, store.List().Count);
        Assert.True(store.Remove("large"));
        Assert.False(File.Exists(Path.Combine(dir.Path, "artifacts", "large.json")));
        Assert.Throws<KeyNotFoundException>(() => store.Retrieve("large"));
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Backoff_delays_follow_the_formula()
    {
        var backoff = new BackoffConfig { InitialDelayMs = 200, BackoffFactor = 2.0, MaxDelayMs = 1000, Jitter = false };
        Assert.Equal([200, 400, 800, 1000], Enumerable.Range(1, 4).Select(a => backoff.DelayForAttempt(a).TotalMilliseconds));
        var jittered = backoff with { Jitter = true };
        Assert.All(Enumerable.Range(0, 50), _ => Assert.InRange(jittered.DelayForAttempt(1).TotalMilliseconds, 100, 300));
    }

    [Fact]
    public void Retry_policy_presets_and_node_overrides()
    {
        var graph = DotParser.Parse("""digraph G { graph [default_max_retries=1] a b [max_retries=4] c [retry_policy="patient"] d [retry_policy="standard", max_retries=0] }""");
        Assert.Equal(2, NodeRetryPolicy.For(graph.Nodes["a"], graph).MaxAttempts);
        Assert.Equal(5, NodeRetryPolicy.For(graph.Nodes["b"], graph).MaxAttempts);
        Assert.Equal(3, NodeRetryPolicy.For(graph.Nodes["c"], graph).MaxAttempts);
        Assert.Equal(2000, NodeRetryPolicy.For(graph.Nodes["c"], graph).Backoff.InitialDelayMs);
        Assert.Equal(1, NodeRetryPolicy.For(graph.Nodes["d"], graph).MaxAttempts);
        Assert.True(RetryClassifier.IsRetryable(new TimeoutException()));
        Assert.False(RetryClassifier.IsRetryable(new ArgumentException()));
        Assert.True(RetryClassifier.IsRetryable(new FlaggedError(true)));
        Assert.False(RetryClassifier.IsRetryable(new FlaggedError(false)));
    }

    [Fact]
    public void Preamble_respects_fidelity_budgets()
    {
        var graph = DotParser.Parse("""digraph G { graph [goal="Ship it"] }""");
        var context = new PipelineContext();
        context.Set("tests_passed", true);
        context.Set("internal.secret", "hidden");
        var history = Enumerable.Range(1, 30)
            .Select(i => new StageRecord($"s{i}", $"Stage {i}", i % 7 == 0 ? StageStatus.Fail : StageStatus.Success, new string('n', 1000), i % 7 == 0 ? "boom" : "", DateTimeOffset.UtcNow, i % 5 == 0 ? 2 : 1))
            .ToList();

        Assert.Equal("", PreambleBuilder.Build(FidelityModes.Full, graph, context, history, "r1"));
        Assert.Equal("## Pipeline context\nGoal: Ship it\nRun: r1", PreambleBuilder.Build(FidelityModes.Truncate, graph, context, history, "r1"));
        var compact = PreambleBuilder.Build(FidelityModes.Compact, graph, context, history, "r1");
        Assert.Contains("tests_passed: true", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("internal.secret", compact, StringComparison.Ordinal);
        var low = PreambleBuilder.Build(FidelityModes.SummaryLow, graph, context, history, "r1");
        var medium = PreambleBuilder.Build(FidelityModes.SummaryMedium, graph, context, history, "r1");
        var high = PreambleBuilder.Build(FidelityModes.SummaryHigh, graph, context, history, "r1");
        Assert.True(low.Length <= 2400);
        Assert.True(medium.Length <= 6000);
        Assert.True(high.Length <= 12000);
        Assert.True(low.Length < medium.Length && medium.Length < high.Length);
        Assert.Contains("30 stage(s) completed", low, StringComparison.Ordinal);
        Assert.Contains("Notable events", medium, StringComparison.Ordinal);
    }

    private sealed class FlaggedError(bool retryable) : Exception("flagged")
    {
        public bool Retryable { get; } = retryable;
    }
}

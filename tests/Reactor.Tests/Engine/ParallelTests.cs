using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Tests.Support;

namespace Reactor.Tests.Engine;

public class ParallelTests
{
    private const string FanOut = """
        digraph P {
            graph [goal="try three approaches"]
            start [shape=Mdiamond] exit [shape=Msquare]
            fork [shape=component, max_parallel=3]
            a [prompt="approach a"]
            b1 [prompt="approach b step 1"]
            b2 [prompt="approach b step 2"]
            c [prompt="approach c"]
            join [shape=tripleoctagon]
            after [prompt="use the best"]
            start -> fork
            fork -> a
            fork -> b1
            fork -> c
            b1 -> b2
            a -> join
            b2 -> join
            c -> join
            join -> after -> exit
        }
        """;

    [Fact]
    public async Task Fan_out_runs_branches_concurrently_on_isolated_contexts_and_fan_in_selects_best()
    {
        using var dir = new TempDir();
        var active = 0;
        var maxActive = 0;
        var gate = new object();
        CodergenResult Slow(Node node, string prompt, PipelineContext ctx)
        {
            lock (gate)
            {
                active++;
                maxActive = Math.Max(maxActive, active);
            }

            Thread.Sleep(150);
            lock (gate)
            {
                active--;
            }

            ctx.Set("branch.marker", node.Id);
            var score = node.Id switch { "a" => 0.4, "b2" => 0.9, "c" => 0.7, _ => 0 };
            return Outcome.Success($"done {node.Id}", new Dictionary<string, object?> { ["score"] = score });
        }

        var backend = new ScriptedBackend().On("a", Slow).On("b1", Slow).On("b2", Slow).On("c", Slow);
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(FanOut, TestPipelines.Options(dir) with
        {
            OnEvent = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }
            },
        });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.True(maxActive >= 2, "branches should run concurrently");
        Assert.Equal("b1", result.Context["parallel.fan_in.best_id"]);
        Assert.Equal("success", result.Context["parallel.fan_in.best_outcome"]);
        Assert.Equal("join", result.Context["parallel.join_node"]);
        Assert.False(result.Context.ContainsKey("branch.marker"), "branch context changes must not leak into the parent");
        var results = Assert.IsType<List<object?>>(result.Context["parallel.results"]);
        Assert.Equal(3, results.Count);
        Assert.Contains("after", result.CompletedNodes);
        Assert.Equal(1, backend.CallCount("after"));
        Assert.Single(events.OfType<ParallelStarted>());
        Assert.Equal(3, events.OfType<ParallelBranchStarted>().Count());
        Assert.Equal(3, events.OfType<ParallelBranchCompleted>().Count());
        Assert.Equal(3, events.OfType<ParallelCompleted>().Single().SuccessCount);
    }

    [Fact]
    public async Task Wait_all_with_failures_is_partial_success_and_fan_in_skips_failed_candidates()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("a", Outcome.Fail("a broke")).On("c", Outcome.Fail("c broke"));

        var result = await TestPipelines.Runner(backend).RunAsync(FanOut, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.PartialSuccess, result.NodeOutcomes["fork"].Status);
        Assert.Equal("b1", result.Context["parallel.fan_in.best_id"]);
        Assert.Equal(StageStatus.Success, result.Status);
    }

    [Fact]
    public async Task All_branches_failing_fails_the_fan_in()
    {
        using var dir = new TempDir();
        // A branch's status is its last stage's: branch b fails at b2 even though b1 succeeded.
        var backend = new ScriptedBackend().On("a", Outcome.Fail("x")).On("b2", Outcome.Fail("y")).On("c", Outcome.Fail("z"));
        var source = FanOut.Replace("join [shape=tripleoctagon]", "join [shape=tripleoctagon, goal_gate=true]", StringComparison.Ordinal);

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["join"].Status);
        Assert.Contains("All parallel candidates failed", result.NodeOutcomes["join"].FailureReason, StringComparison.Ordinal);
        Assert.Equal(StageStatus.Fail, result.Status);
        Assert.DoesNotContain("after", result.CompletedNodes);
    }

    [Fact]
    public async Task First_success_policy_cancels_remaining_branches()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend()
            .On("a", (_, _, _) => "fast")
            .On("b1", (_, _, _) =>
            {
                Thread.Sleep(3000);
                return "slow";
            })
            .On("c", (_, _, _) =>
            {
                Thread.Sleep(3000);
                return "slow";
            });
        var source = FanOut.Replace("max_parallel=3", "max_parallel=3, join_policy=\"first_success\"", StringComparison.Ordinal);

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.NodeOutcomes["fork"].Status);
        Assert.Equal("a", result.Context["parallel.fan_in.best_id"]);
    }

    [Fact]
    public async Task Fan_in_can_ask_the_llm_to_choose()
    {
        using var dir = new TempDir();
        var source = FanOut.Replace("join [shape=tripleoctagon]", "join [shape=tripleoctagon, prompt=\"Pick the most robust approach\"]", StringComparison.Ordinal);
        var backend = new ScriptedBackend().On("join", (_, prompt, _) =>
        {
            Assert.Contains("### c", prompt, StringComparison.Ordinal);
            return "Considering robustness...\nBEST: c";
        });

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("c", result.Context["parallel.fan_in.best_id"]);
        Assert.Equal("llm", result.Context["parallel.fan_in.method"]);
    }
}

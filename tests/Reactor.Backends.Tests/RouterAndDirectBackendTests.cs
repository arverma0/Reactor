using Reactor.Agent.Tests.Session;
using Reactor.Backends.Tests.Support;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Llm;

namespace Reactor.Backends.Tests;

public sealed class RouterAndDirectBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NamedBackend(string name, bool statusFile = false, bool steering = false) : ICodergenBackend
    {
        public List<string> Nodes { get; } = [];

        public bool SupportsStatusFile => statusFile;

        public bool SupportsSteering => steering;

        public Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
        {
            lock (Nodes)
            {
                Nodes.Add(node.Id);
            }

            return Task.FromResult<CodergenResult>($"{name} handled {node.Id}");
        }
    }

    [Fact]
    public async Task Router_selects_by_backend_attribute_then_provider_then_default()
    {
        using var dir = new TempDir();
        var fallback = new NamedBackend("default");
        var special = new NamedBackend("special", statusFile: true);
        var anthropic = new NamedBackend("anthropic");
        var router = new BackendRouter(fallback).Add("special", special).Add("anthropic", anthropic);

        var result = await Pipelines.Runner(router).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="A"]
              b [prompt="B", backend="special"]
              c [prompt="C", llm_provider="anthropic"]
              d [prompt="D", llm_provider="openai"]
              start -> a -> b -> c -> d -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["a", "d"], fallback.Nodes);
        Assert.Equal(["b"], special.Nodes);
        Assert.Equal(["c"], anthropic.Nodes);

        // The status-file protocol is offered only to stages whose backend can write files.
        Assert.Contains("Reporting the stage outcome", File.ReadAllText(dir.Combine("run", "b", "prompt.md")));
        Assert.DoesNotContain("Reporting the stage outcome", File.ReadAllText(dir.Combine("run", "a", "prompt.md")));
    }

    [Fact]
    public async Task Router_fails_stages_that_request_an_unknown_backend()
    {
        using var dir = new TempDir();
        var router = new BackendRouter(new NamedBackend("default"));

        var result = await Pipelines.Runner(router).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="A", backend="nope"]
              start -> a -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["a"].Status);
        Assert.Contains("requests backend 'nope'", result.NodeOutcomes["a"].FailureReason);
    }

    [Fact]
    public async Task Direct_llm_backend_answers_without_tools_and_keeps_thread_history()
    {
        using var dir = new TempDir();
        var llm = new FakeLlm("test");
        llm.Responder = r => FakeLlm.Text("answer " + r.Messages.Count(m => m.Role == Role.User));
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["test"] = llm }, "test");
        var direct = new DirectLlmBackend(new DirectLlmBackendOptions { Client = client, DefaultModel = "test-model" });

        var result = await Pipelines.Runner(direct).RunAsync("""
            digraph G {
              graph [default_fidelity="full"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="Question one"]
              b [prompt="Question two"]
              start -> a -> b -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.All(llm.Requests, r => Assert.Null(r.Tools));
        Assert.Equal("answer 1", File.ReadAllText(dir.Combine("run", "a", "response.md")));
        Assert.Equal("answer 2", File.ReadAllText(dir.Combine("run", "b", "response.md")));
        Assert.Equal(Role.System, llm.Requests[0].Messages[0].Role);
        Assert.DoesNotContain("Reporting the stage outcome", File.ReadAllText(dir.Combine("run", "a", "prompt.md")));
    }

    [Fact]
    public void Router_reports_the_steering_capability_of_the_backend_each_node_uses()
    {
        var steerable = new NamedBackend("sdk", steering: true);
        var router = new BackendRouter(new NamedBackend("cli")).Add("copilot", steerable);
        ICodergenBackend asBackend = router;
        var plain = new Node("plain");
        var steered = new Node("steered") { [BackendRouter.BackendAttribute] = "copilot" };
        var unknown = new Node("unknown") { [BackendRouter.BackendAttribute] = "nope" };

        Assert.False(asBackend.SupportsSteering);
        Assert.False(asBackend.SupportsSteeringFor(plain));
        Assert.True(asBackend.SupportsSteeringFor(steered));
        Assert.False(asBackend.SupportsSteeringFor(unknown));
        Assert.True(((ICodergenBackend)new BackendRouter(steerable)).SupportsSteering);
    }

    [Fact]
    public async Task Direct_backend_sends_stage_instructions_without_house_rules_and_prices_tokens()
    {
        using var dir = new TempDir();
        var llm = new FakeLlm("test");
        llm.Responder = _ => FakeLlm.Text("ok") with { Model = "gpt-5" };
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["test"] = llm }, "test");
        var direct = new DirectLlmBackend(new DirectLlmBackendOptions { Client = client, DefaultProvider = "test" });

        var result = await Pipelines.Runner(direct).RunAsync("""
            digraph G {
              graph [instructions="Answer in English."]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="Question", llm_provider="test", llm_model="gpt-5", instructions="Be brief."]
              start -> a -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        var system = llm.Requests[0].Messages[0].Text;
        Assert.StartsWith("You are one stage of an automated software pipeline.", system, StringComparison.Ordinal);
        Assert.Contains("## Pipeline instructions\nAnswer in English.", system, StringComparison.Ordinal);
        Assert.Contains("## Stage instructions\nBe brief.", system, StringComparison.Ordinal);
        Assert.DoesNotContain("House rules", system, StringComparison.Ordinal);
        Assert.True(File.Exists(dir.Combine("run", "a", StageInstructions.FileName)));

        // gpt-5 costs $1.25 / $10 per 1M tokens in the catalog; FakeLlm reports 10 input and 5 output tokens.
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(dir.Combine("run", "a", "backend.json")))!;
        Assert.Equal(Math.Round(((10 * 1.25) + (5 * 10.0)) / 1e6, 6), metadata["cost_usd"]!.GetValue<double>());
        Assert.Equal("tokens", metadata["cost_basis"]!.GetValue<string>());
        Assert.True(metadata["cost_estimated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_failed_direct_call_still_records_the_model()
    {
        using var dir = new TempDir();
        var llm = new FakeLlm("test");
        llm.Responder = _ => throw new AuthenticationError("bad key", "test", 401);
        var client = new Client(new Dictionary<string, IProviderAdapter> { ["test"] = llm }, "test");
        var direct = new DirectLlmBackend(new DirectLlmBackendOptions { Client = client, DefaultModel = "test-model", RetryPolicy = new RetryPolicy { MaxRetries = 0 } });

        await Pipelines.Runner(direct).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              a [prompt="Question"]
              start -> a -> exit
            }
            """, Pipelines.Options(dir), Ct);

        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(dir.Combine("run", "a", "backend.json")))!;
        Assert.Equal("direct", metadata["backend"]!.GetValue<string>());
        Assert.Equal("test-model", metadata["model"]!.GetValue<string>());
        Assert.Equal("error", metadata["outcome"]!.GetValue<string>());
        Assert.Contains("bad key", metadata["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}

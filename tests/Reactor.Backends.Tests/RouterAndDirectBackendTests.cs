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

    private sealed class NamedBackend(string name, bool statusFile = false) : ICodergenBackend
    {
        public List<string> Nodes { get; } = [];

        public bool SupportsStatusFile => statusFile;

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
}

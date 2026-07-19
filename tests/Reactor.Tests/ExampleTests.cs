using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;
using Reactor.Tests.Support;

namespace Reactor.Tests;

/// <summary>Keeps the shipped example pipelines valid and runnable.</summary>
public sealed class ExampleTests
{
    public static TheoryData<string> Examples => new(Directory.GetFiles(ExamplesDirectory(), "*.dot").Select(Path.GetFileName).Order(StringComparer.Ordinal)!);

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_parses_and_validates_without_errors(string name)
    {
        var graph = DotParser.ParseFile(Path.Combine(ExamplesDirectory(), name));
        var prepared = TestPipelines.Runner().Prepare(graph);

        Assert.False(prepared.HasErrors, string.Join("\n", prepared.Diagnostics.Select(d => $"{d.Severity} {d.Rule}: {d.Message}")));
        Assert.Equal(Path.Combine(ExamplesDirectory(), name), graph.SourcePath);
    }

    [Theory]
    [InlineData("smoke.dot")]
    [InlineData("human-review.dot")]
    [InlineData("parallel-review.dot")]
    [InlineData("stylesheet.dot")]
    [InlineData("child-worker.dot")]
    [InlineData("supervisor.dot")]
    public async Task Example_completes_in_simulation_mode(string name)
    {
        using var dir = new TempDir();
        var runner = TestPipelines.Runner(interviewer: new AutoApproveInterviewer());

        var result = await runner.RunAsync(DotParser.ParseFile(Path.Combine(ExamplesDirectory(), name)), TestPipelines.Options(dir), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
    }

    [Fact]
    public async Task Smoke_example_meets_the_spec_integration_assertions()
    {
        // Spec 11.13, with a scripted backend standing in for the real LLM.
        using var dir = new TempDir();
        var graph = DotParser.ParseFile(Path.Combine(ExamplesDirectory(), "smoke.dot"));
        Assert.Equal("Create a hello world Python script", graph.Goal);
        Assert.Equal(5, graph.Nodes.Count);
        Assert.Equal(6, graph.Edges.Count);

        var backend = new ScriptedBackend();
        var result = await TestPipelines.Runner(backend).RunAsync(graph, TestPipelines.Options(dir), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Contains("implement", result.CompletedNodes);
        foreach (var stage in new[] { "plan", "implement", "review" })
        {
            foreach (var artifact in new[] { "prompt.md", "response.md", "status.json" })
            {
                Assert.True(File.Exists(Path.Combine(result.LogsRoot, stage, artifact)), $"{stage}/{artifact}");
            }
        }

        Assert.Equal(StageStatus.Success, result.NodeOutcomes["implement"].Status);
        Assert.Contains("Plan how to create a hello world script for: Create a hello world Python script", backend.Calls.First(c => c.NodeId == "plan").Prompt);

        var checkpoint = Checkpoint.Load(Path.Combine(result.LogsRoot, "checkpoint.json"));
        Assert.Equal("done", checkpoint.CurrentNode);
        Assert.Contains("plan", checkpoint.CompletedNodes);
        Assert.Contains("implement", checkpoint.CompletedNodes);
        Assert.Contains("review", checkpoint.CompletedNodes);
    }

    internal static string ExamplesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Reactor.slnx")))
            {
                return Path.Combine(directory.FullName, "examples");
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root (Reactor.slnx).");
    }
}

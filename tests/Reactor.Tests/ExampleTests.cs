using Reactor.Events;
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
    [InlineData("artifacts.dot")]
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

    [Fact]
    public async Task Artifacts_example_is_lint_clean_and_records_every_artifact_in_simulation()
    {
        var graph = DotParser.ParseFile(Path.Combine(ExamplesDirectory(), "artifacts.dot"));
        var runner = TestPipelines.Runner();
        Assert.Empty(runner.Prepare(graph).Diagnostics);

        using var dir = new TempDir();
        var events = new List<PipelineEvent>();
        var result = await runner.RunAsync(graph, TestPipelines.Options(dir) with { OnEvent = events.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "spec", "architect", "implement", "test", "review", "exit"], result.CompletedNodes);
        Assert.Equal(["spec", "design", "changes", "test_report", "verdict"], events.OfType<ArtifactProduced>().Select(e => e.Name));
        var report = Assert.IsType<Dictionary<string, object?>>(result.Context["artifacts.test_report"]);
        Assert.StartsWith("No tests were run", (string)report["text"]!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "docs")), "simulated stages do not write files");

        var implement = File.ReadAllText(Path.Combine(result.LogsRoot, "implement", "prompt.md"));
        Assert.Contains("- `design` — file `docs/DESIGN.md` (written by stage `architect`). Read it.", implement, StringComparison.Ordinal);
        Assert.Contains("- `verdict` — not produced yet in this run.", implement, StringComparison.Ordinal);
        Assert.Contains("- `changes` → your final response is recorded as this artifact", implement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifacts_example_loops_review_feedback_back_to_the_implementer()
    {
        using var dir = new TempDir();
        var reviews = 0;
        string WriteDoc(Node node, PipelineContext context, string path)
        {
            var full = Path.Combine(context.Run!.WorkingDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, $"# {node.Label}\n");
            return $"wrote {path}";
        }

        var backend = new ScriptedBackend { SupportsStatusFile = true }
            .On("spec", (node, _, context) => WriteDoc(node, context, "docs/SPEC.md"))
            .On("architect", (node, _, context) => WriteDoc(node, context, "docs/DESIGN.md"))
            .On("review", (node, prompt, context) =>
            {
                Assert.Contains("- `test_report` — text from stage `test`", prompt, StringComparison.Ordinal);
                if (++reviews == 1)
                {
                    File.WriteAllText(Path.Combine(context.Run!.StageDirectory(node.Id), "status.json"), """{"outcome":"success","preferred_label":"Revise"}""");
                    return "Handle empty strings.";
                }

                Assert.Contains("- `test_report` — text from stage `test` (version 2):", prompt, StringComparison.Ordinal);
                return "Ready to merge.";
            });

        var result = await TestPipelines.Runner(backend).RunAsync(DotParser.ParseFile(Path.Combine(ExamplesDirectory(), "artifacts.dot")), TestPipelines.Options(dir), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["review"].Status);
        Assert.Equal(2, backend.CallCount("implement"));
        var second = backend.Calls.Where(c => c.NodeId == "implement").Last().Prompt;
        Assert.Contains("- `verdict` — text from stage `review`:\n  > Handle empty strings.", second, StringComparison.Ordinal);
        var verdict = Assert.IsType<Dictionary<string, object?>>(result.Context["artifacts.verdict"]);
        Assert.Equal(2L, verdict["version"]);
        Assert.Equal("Ready to merge.", verdict["text"]);
        Assert.Equal(64, ((string)Assert.IsType<Dictionary<string, object?>>(result.Context["artifacts.design"])["sha256"]!).Length);
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

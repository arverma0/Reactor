using System.Collections.Concurrent;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Tests.Support;

namespace Reactor.Tests.Engine;

/// <summary><see cref="PipelineRunnerOptions.PersistRunState"/>: in-memory runs for throwaway executions such as simulations.</summary>
public sealed class InMemoryRunTests
{
    private const string Pipeline = """
        digraph M {
            graph [goal="remember nothing"]
            start [shape=Mdiamond] exit [shape=Msquare]
            plan [prompt="plan"]
            check [prompt="check", max_retries=1]
            again [prompt="again"]
            start -> plan -> check
            check -> again [condition="context.loops=1"]
            check -> exit [condition="context.loops=2"]
            again -> check
        }
        """;

    [Fact]
    public async Task In_memory_runs_behave_like_persisted_runs_but_write_no_files()
    {
        using var dir = new TempDir();
        var persistedEvents = new List<PipelineEvent>();
        var memoryEvents = new List<PipelineEvent>();

        var persisted = await Runner(persist: true).RunAsync(Pipeline, TestPipelines.Options(dir, "persisted") with { OnEvent = persistedEvents.Add }, TestContext.Current.CancellationToken);
        var inMemory = await Runner(persist: false).RunAsync(Pipeline, TestPipelines.Options(dir, "memory") with { OnEvent = memoryEvents.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, inMemory.Status);
        Assert.Equal(["start", "plan", "check", "again", "check", "exit"], inMemory.CompletedNodes);
        Assert.Equal(persisted.CompletedNodes, inMemory.CompletedNodes);
        Assert.Equal(persisted.NodeOutcomes.ToDictionary(kv => kv.Key, kv => kv.Value.Status), inMemory.NodeOutcomes.ToDictionary(kv => kv.Key, kv => kv.Value.Status));
        Assert.Equal(persisted.Context.Keys.Order(), inMemory.Context.Keys.Order());
        Assert.Equal(
            persistedEvents.Where(e => e is not CheckpointSaved).Select(e => e.Type),
            memoryEvents.Select(e => e.Type));

        foreach (var file in new[] { "graph.dot", "manifest.json", "checkpoint.json", "result.json", "events.jsonl", Path.Combine("check", "status.json") })
        {
            Assert.True(File.Exists(Path.Combine(persisted.LogsRoot, file)), file);
        }

        Assert.True(Directory.Exists(Path.Combine(persisted.LogsRoot, "check", "history", "visit-1")));
        Assert.False(Directory.Exists(inMemory.LogsRoot), "an in-memory run with no event log writes nothing");
        Assert.DoesNotContain(memoryEvents, e => e is CheckpointSaved);
    }

    [Fact]
    public async Task In_memory_loop_restarts_create_no_directories()
    {
        const string source = """
            digraph L {
                start [shape=Mdiamond] exit [shape=Msquare]
                work [prompt="work"]
                check [prompt="check"]
                start -> work -> check
                check -> work [condition="context.loops=1", loop_restart=true]
                check -> exit [condition="context.loops=2"]
            }
            """;
        using var dir = new TempDir();
        var events = new List<PipelineEvent>();

        var result = await Runner(persist: false).RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(1, result.LoopRestarts);
        Assert.Single(events.OfType<LoopRestarted>());
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handler_written_status_files_are_read_once_per_attempt(bool persist)
    {
        const string source = """
            digraph S {
                start [shape=Mdiamond] exit [shape=Msquare]
                agent [type="file.writer", max_retries=1, auto_status=true]
                start -> agent -> exit
            }
            """;
        using var dir = new TempDir();
        var runner = new PipelineRunner(new PipelineRunnerOptions { PersistRunState = persist, WriteEventLog = true, LogsBaseDirectory = dir.Path });
        var writer = new StatusFileWriter();
        runner.RegisterHandler("file.writer", writer);
        var events = new List<PipelineEvent>();

        var result = await runner.RunAsync(source, TestPipelines.Options(dir, "run") with { OnEvent = events.Add }, TestContext.Current.CancellationToken);

        // Attempt 1 reports failure through status.json; attempt 2 writes nothing, so auto_status applies (no stale read).
        Assert.Equal(2, writer.Calls);
        Assert.Equal("written by the handler", events.OfType<StageFailed>().Single(e => e.NodeId == "agent").Error);
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["agent"].Status);
        Assert.Equal(StageStatus.Success, result.Status);
        Assert.True(File.Exists(Path.Combine(result.LogsRoot, "events.jsonl")));
        Assert.Equal(persist, File.Exists(Path.Combine(result.LogsRoot, "checkpoint.json")));
        Assert.Equal(persist, File.Exists(Path.Combine(result.LogsRoot, "result.json")));
        Assert.Equal(persist, File.Exists(Path.Combine(result.LogsRoot, "graph.dot")));
        Assert.Equal(persist, File.Exists(Path.Combine(result.LogsRoot, "agent", "status.json")));
    }

    private static PipelineRunner Runner(bool persist)
    {
        var runner = new PipelineRunner(new PipelineRunnerOptions
        {
            PersistRunState = persist,
            WriteEventLog = persist,
            LogsBaseDirectory = Path.Combine(Path.GetTempPath(), "reactor-tests", "runs"),
        });
        var handler = new LoopCountingHandler();
        runner.RegisterHandler(HandlerTypes.Codergen, handler);
        runner.Handlers.DefaultHandler = handler;
        return runner;
    }

    /// <summary>Writes no files; fails <c>check</c>'s first attempt when it may retry, and counts <c>check</c> successes into <c>loops</c>.</summary>
    private sealed class LoopCountingHandler : INodeHandler
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
        private int _checkSuccesses;

        public Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
        {
            var call = _calls.AddOrUpdate(node.Id, 1, (_, count) => count + 1);
            if (node.Id == "check" && call == 1 && node.MaxRetries is > 0)
            {
                return Task.FromResult<Outcome?>(Outcome.Fail("first attempt fails"));
            }

            var updates = node.Id == "check"
                ? new Dictionary<string, object?> { ["loops"] = (long)Interlocked.Increment(ref _checkSuccesses) }
                : null;
            return Task.FromResult<Outcome?>(Outcome.Success("done " + node.Id, updates));
        }
    }

    /// <summary>Reports a failure the way agents do (status.json, no returned outcome) on its first call only.</summary>
    private sealed class StatusFileWriter : INodeHandler
    {
        public int Calls;

        public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                var stageDir = Path.Combine(logsRoot, node.Id);
                Directory.CreateDirectory(stageDir);
                await File.WriteAllTextAsync(Path.Combine(stageDir, "status.json"), """{"outcome": "fail", "failure_reason": "written by the handler"}""", cancellationToken);
            }

            return null;
        }
    }
}

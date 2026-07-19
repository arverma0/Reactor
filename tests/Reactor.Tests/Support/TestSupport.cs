using System.Collections.Concurrent;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Tests.Support;

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reactor-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A codergen backend scripted per node id. Each entry is a queue of responses consumed on successive visits; the last
/// response repeats. Records every call.
/// </summary>
internal sealed class ScriptedBackend : ICodergenBackend
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<Node, string, PipelineContext, CodergenResult>>> _script = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<Node, string, PipelineContext, CodergenResult>> _last = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string NodeId, string Prompt)> Calls { get; } = new();

    public bool SupportsStatusFile { get; set; }

    public TimeSpan Delay { get; set; }

    public ScriptedBackend On(string nodeId, params CodergenResult[] results)
    {
        foreach (var result in results)
        {
            On(nodeId, (_, _, _) => result);
        }

        return this;
    }

    public ScriptedBackend On(string nodeId, Func<Node, string, PipelineContext, CodergenResult> responder)
    {
        _script.GetOrAdd(nodeId, _ => new ConcurrentQueue<Func<Node, string, PipelineContext, CodergenResult>>()).Enqueue(responder);
        return this;
    }

    public int CallCount(string nodeId) => Calls.Count(c => c.NodeId == nodeId);

    public async Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
    {
        Calls.Enqueue((node.Id, prompt));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (_script.TryGetValue(node.Id, out var queue) && queue.TryDequeue(out var responder))
        {
            _last[node.Id] = responder;
            return responder(node, prompt, context);
        }

        if (_last.TryGetValue(node.Id, out var repeat))
        {
            return repeat(node, prompt, context);
        }

        return $"ok:{node.Id}";
    }
}

internal static class TestPipelines
{
    public static PipelineRunner Runner(ICodergenBackend? backend = null, Human.IInterviewer? interviewer = null) => new(new PipelineRunnerOptions
    {
        Backend = backend,
        Interviewer = interviewer ?? new Human.AutoApproveInterviewer(),
        LogsBaseDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reactor-tests", "runs"),
    });

    public static RunOptions Options(TempDir dir, string name = "run") => new()
    {
        LogsRoot = dir.Combine(name),
        WorkingDirectory = dir.Path,
        Backoff = BackoffConfig.Immediate,

        // Routing bugs must fail fast in tests rather than loop forever.
        MaxSteps = 500,
    };
}

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Reactor.Server.Tests;

/// <summary>Writes a run directory the way the engine (or the <c>reactor run</c> CLI) leaves it, for history tests.</summary>
internal sealed class FakeRun
{
    private readonly StringBuilder _events = new();
    private DateTimeOffset _clock;

    private FakeRun(string directory, DateTimeOffset started)
    {
        Directory = directory;
        _clock = started;
    }

    public string Directory { get; }

    public string Id => Path.GetFileName(Directory);

    /// <summary>Creates the directory with a manifest and a PipelineStarted event.</summary>
    public static FakeRun Create(string runsDirectory, string dirName, string graphName, DateTimeOffset started)
    {
        var directory = Path.Combine(runsDirectory, dirName);
        System.IO.Directory.CreateDirectory(directory);
        var manifest = new JsonObject
        {
            ["name"] = graphName,
            ["goal"] = "goal of " + graphName,
            ["run_id"] = "id-" + dirName,
            ["started_at"] = started.ToString("O", CultureInfo.InvariantCulture),
            ["logs_root"] = directory,
            ["node_count"] = 4,
        };
        System.IO.File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToJsonString());
        System.IO.File.WriteAllText(Path.Combine(directory, "graph.dot"), $"digraph {graphName} {{ start [shape=Mdiamond] exit [shape=Msquare] start -> exit }}");
        var run = new FakeRun(directory, started);
        run.Event("PipelineStarted", new JsonObject { ["name"] = graphName, ["goal"] = "", ["logs_root"] = directory });
        return run;
    }

    public FakeRun Event(string type, JsonObject fields)
    {
        _clock = _clock.AddMilliseconds(10);
        fields["timestamp"] = _clock.ToString("O", CultureInfo.InvariantCulture);
        fields["run_id"] = "fake";
        fields["type"] = type;
        fields["description"] = type;
        _events.Append(fields.ToJsonString()).Append('\n');
        return this;
    }

    public FakeRun Raw(string line)
    {
        _events.Append(line).Append('\n');
        return this;
    }

    public FakeRun Started(string node, int attempt = 1) =>
        Event("StageStarted", new JsonObject { ["node_id"] = node, ["label"] = node, ["index"] = 1, ["handler_type"] = "codergen", ["attempt"] = attempt });

    public FakeRun Failed(string node, string error, bool willRetry) =>
        Event("StageFailed", new JsonObject { ["node_id"] = node, ["index"] = 1, ["error"] = error, ["will_retry"] = willRetry });

    public FakeRun Completed(string node, string status, int milliseconds) =>
        Event("StageCompleted", new JsonObject
        {
            ["node_id"] = node,
            ["index"] = 1,
            ["duration"] = TimeSpan.FromMilliseconds(milliseconds).ToString("c", CultureInfo.InvariantCulture),
            ["status"] = status,
            ["notes"] = "",
        });

    public FakeRun Edge(string from, string to) =>
        Event("EdgeSelected", new JsonObject { ["from"] = from, ["to"] = to, ["reason"] = "unconditional", ["label"] = "" });

    /// <summary>A <c>MetricsRecorded</c> event: the outcome metrics a stage's visit reported.</summary>
    public FakeRun Metrics(string node, params (string Name, double Value)[] metrics)
    {
        var values = new JsonObject();
        foreach (var (name, value) in metrics)
        {
            values[name] = value;
        }

        return Event("MetricsRecorded", new JsonObject { ["node_id"] = node, ["metrics"] = values });
    }

    /// <summary>A <c>FailedStageContinued</c> event: a failure that flowed onward along an unconditional edge.</summary>
    public FakeRun Continued(string node, string to, string reason = "it broke") =>
        Event("FailedStageContinued", new JsonObject { ["node_id"] = node, ["to"] = to, ["failure_reason"] = reason });

    /// <summary>A <c>VisitLimitReached</c> event and the <c>max_visits</c> hop that follows it.</summary>
    public FakeRun VisitLimit(string node, int max, string target) =>
        Event("VisitLimitReached", new JsonObject { ["node_id"] = node, ["max_visits"] = max, ["target"] = target })
            .Event("EdgeSelected", new JsonObject { ["from"] = node, ["to"] = target, ["reason"] = "max_visits", ["label"] = "" });

    /// <summary>A <c>BudgetExceeded</c> event.</summary>
    public FakeRun Budget(double spent, double limit, string next) =>
        Event("BudgetExceeded", new JsonObject { ["spent_usd"] = spent, ["limit_usd"] = limit, ["next_node_id"] = next });

    /// <summary>Writes <c>backend.json</c> for the latest visit of a stage, or for <c>history/visit-N</c>.</summary>
    public FakeRun Backend(string node, JsonObject metadata, int? visit = null) =>
        File(visit is { } n ? Path.Combine(node, "history", $"visit-{n}", "backend.json") : Path.Combine(node, "backend.json"), metadata.ToJsonString());

    public FakeRun File(string relativePath, string content)
    {
        var path = Path.Combine(Directory, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return this;
    }

    public FakeRun Result(string status, long durationMs, DateTimeOffset finishedAt, bool cancelled = false, string failure = "") =>
        File("result.json", new JsonObject
        {
            ["run_id"] = "fake",
            ["status"] = status,
            ["failure_reason"] = failure,
            ["cancelled"] = cancelled,
            ["duration_ms"] = durationMs,
            ["finished_at"] = finishedAt.ToString("O", CultureInfo.InvariantCulture),
        }.ToJsonString());

    /// <summary>Writes <c>events.jsonl</c>, optionally ending in a partial line (a run killed mid-write).</summary>
    public FakeRun Save(string? partialTail = null)
    {
        System.IO.File.WriteAllText(Path.Combine(Directory, "events.jsonl"), _events + (partialTail ?? ""));
        return this;
    }

    public static JsonObject Usage(string? model = null, double? aiUnits = null, long? input = null, long? output = null) => new()
    {
        ["model"] = model,
        ["ai_units"] = aiUnits,
        ["input_tokens"] = input,
        ["output_tokens"] = output,
    };
}

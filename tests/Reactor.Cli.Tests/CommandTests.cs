using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Reactor.Execution;

namespace Reactor.Cli.Tests;

public class CommandTests
{
    private const string Pipeline = """
        digraph cli_test {
            start [shape=Mdiamond]
            plan [prompt="plan it"]
            work [prompt="do it"]
            exit [shape=Msquare]
            start -> plan -> work -> exit
        }
        """;

    [Fact]
    public void Serve_and_studio_accept_the_flags_scripts_also_pass_to_run()
    {
        string[] shared = ["--backend", "simulate", "--model", "gpt-5-mini", "--provider", "openai", "--max-tokens", "2048", "--max-steps", "50", "--no-color", "-C", "."];

        Assert.Empty(PipelineCommands.Run().Parse(["pipeline.dot", .. shared]).Errors);
        Assert.Empty(HostCommands.Serve().Parse(shared).Errors);
        Assert.Empty(HostCommands.Studio().Parse([.. shared, "--no-browser"]).Errors);
        Assert.NotEmpty(HostCommands.Serve().Parse(["--max-tokens", "0"]).Errors);
    }

    [Fact]
    public async Task Run_without_logs_names_its_directory_with_a_utc_timestamp()
    {
        using var dir = new TempDir();
        var dot = dir.Write("pipeline.dot", Pipeline);
        var work = dir.CreateDirectory("work");
        var before = DateTime.UtcNow;

        Assert.Equal(ExitCodes.Success, await InvokeAsync("run", dot, "--backend", "simulate", "-C", work, "-y", "-q", "--no-color"));

        var after = DateTime.UtcNow;
        var name = Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(work, ".reactor", "runs"))));
        Assert.EndsWith("-cli_test", name, StringComparison.Ordinal);
        var stamp = DateTime.ParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.InRange(stamp, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.True(File.Exists(Path.Combine(work, ".reactor", ".gitignore")));
    }

    [Fact]
    public void Simulate_replay_commands_carry_the_initial_context()
    {
        Assert.Equal(
            "reactor simulate pipe.dot --scenario realistic --seed 42 --max-steps 50 --set \"mode=fast\" --set \"cfg={\\\"a\\\":1}\" --trace {run}",
            SimulateCommand.ReplayCommand("pipe.dot", "realistic", 42, 50, ["mode=fast", "cfg={\"a\":1}"]));
        Assert.Equal("reactor simulate pipe.dot --scenario happy --seed 7 --trace {run}", SimulateCommand.ReplayCommand("pipe.dot", "happy", 7, 200, []));
    }

    [Fact]
    public void Run_directory_names_use_the_invariant_calendar()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // th-TH formats dates in the Buddhist calendar (year 2569) unless the invariant culture is used.
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("20260729-122702-my-pipeline", PipelineCommands.RunDirectoryName("my pipeline", new DateTime(2026, 7, 29, 12, 27, 2, DateTimeKind.Utc)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Run_and_resume_with_explicit_logs_create_nothing_in_the_working_directory()
    {
        using var dir = new TempDir();
        var dot = dir.Write("pipeline.dot", Pipeline);
        var work = dir.CreateDirectory("work");
        var logs = Path.Combine(dir.Path, "elsewhere", "my-run");

        Assert.Equal(ExitCodes.Success, await InvokeAsync("run", dot, "--backend", "simulate", "-C", work, "--logs", logs, "-y", "-q", "--no-color"));
        Assert.Equal(ExitCodes.Success, await InvokeAsync("resume", logs, "--backend", "simulate", "-C", work, "-y", "-q", "--no-color"));

        Assert.True(File.Exists(Path.Combine(logs, Checkpoint.FileName)));
        Assert.Empty(Directory.GetFileSystemEntries(work));
    }

    [Fact]
    public void Run_and_resume_accept_no_track_changes()
    {
        Assert.Empty(PipelineCommands.Run().Parse(["pipeline.dot", "--no-track-changes"]).Errors);
        Assert.Empty(PipelineCommands.Resume().Parse(["run-dir", "--no-track-changes"]).Errors);
    }

    [Fact]
    public async Task Run_with_logs_inside_the_reactor_directory_still_gets_the_gitignore()
    {
        using var dir = new TempDir();
        var dot = dir.Write("pipeline.dot", Pipeline);
        var work = dir.CreateDirectory("work");

        Assert.Equal(ExitCodes.Success, await InvokeAsync("run", dot, "--backend", "simulate", "-C", work, "--logs", Path.Combine(work, ".reactor", "mine"), "-y", "-q", "--no-color"));

        Assert.True(File.Exists(Path.Combine(work, ".reactor", ".gitignore")));
        Assert.False(Directory.Exists(Path.Combine(work, ".reactor", "runs")));
    }

    [Fact]
    public async Task Render_refuses_a_format_that_disagrees_with_the_output_file()
    {
        using var dir = new TempDir();
        var dot = dir.Write("pipeline.dot", Pipeline);
        var target = Path.Combine(dir.Path, "graph.png");

        Assert.Equal(ExitCodes.InvalidInput, await InvokeAsync("render", dot, "-o", target, "-f", "svg"));
        Assert.Equal(ExitCodes.InvalidInput, await InvokeAsync("render", dot, "-o", dot, "-f", "dot"));

        Assert.False(File.Exists(target));
        Assert.Equal(Pipeline, File.ReadAllText(dot));
    }

    [Fact]
    public async Task Render_run_paints_the_stage_in_progress_as_running_and_finished_stages_by_status()
    {
        using var dir = new TempDir();
        var dot = dir.Write("pipeline.dot", Pipeline);
        var run = dir.CreateDirectory("run");

        // The state of a run working on 'work': the checkpoint's current_node is the last finished stage.
        new Checkpoint
        {
            CurrentNode = "plan",
            CompletedNodes = ["start", "plan"],
            NodeOutcomes = { ["start"] = Outcome.Success(), ["plan"] = Outcome.Fail("bad plan") },
        }.Save(Path.Combine(run, Checkpoint.FileName));
        File.WriteAllLines(Path.Combine(run, "events.jsonl"),
        [
            Event("PipelineStarted"),
            Event("StageStarted", "start"),
            Event("StageCompleted", "start", "success"),
            Event("StageStarted", "plan"),
            Event("StageCompleted", "plan", "fail"),
            Event("StageStarted", "work"),
        ]);
        var target = Path.Combine(dir.Path, "status.gv");

        Assert.Equal(ExitCodes.Success, await InvokeAsync("render", dot, "--run", run, "-o", target));

        var lines = File.ReadAllLines(target);
        string NodeLine(string id) => Assert.Single(lines, line => line.TrimStart().StartsWith(id + " [", StringComparison.Ordinal));
        Assert.Contains("#bbdefb", NodeLine("work"), StringComparison.Ordinal);
        Assert.Contains("#ffcdd2", NodeLine("plan"), StringComparison.Ordinal);
        Assert.Contains("#c8e6c9", NodeLine("start"), StringComparison.Ordinal);
        Assert.DoesNotContain("fillcolor", NodeLine("exit"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_run_reports_a_directory_that_is_not_a_run()
    {
        using var dir = new TempDir();
        var dot = dir.Write("pipeline.dot", Pipeline);

        Assert.Equal(ExitCodes.InvalidInput, await InvokeAsync("render", dot, "--run", dir.CreateDirectory("empty"), "-o", Path.Combine(dir.Path, "x.dot")));
        Assert.Equal(ExitCodes.InvalidInput, await InvokeAsync("render", dot, "--run", Path.Combine(dir.Path, "missing"), "-o", Path.Combine(dir.Path, "x.dot")));
    }

    private static string Event(string type, string? node = null, string? status = null)
    {
        var json = new JsonObject { ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"), ["type"] = type };
        if (node is not null)
        {
            json["node_id"] = node;
        }

        if (status is not null)
        {
            json["status"] = status;
        }

        return json.ToJsonString();
    }

    private static Task<int> InvokeAsync(params string[] args)
    {
        var root = new RootCommand { PipelineCommands.Run(), PipelineCommands.Resume(), PipelineCommands.Render() };
        return root.Parse(args).InvokeAsync();
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reactor-cli-tests", Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    public string CreateDirectory(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

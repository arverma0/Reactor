using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Handlers;

namespace Reactor.Server.Tests;

public sealed class FlightsApiTests
{
    private const string Simple = """
        digraph Simple {
          graph [goal="Say hello"]
          start [shape=Mdiamond]
          exit [shape=Msquare]
          plan [prompt="Plan: $goal"]
          implement [prompt="Implement"]
          start -> plan -> implement -> exit
        }
        """;

    private const string Forkable = """
        digraph Forkable {
          start [shape=Mdiamond]
          exit [shape=Msquare]
          a [prompt="A"]
          b [prompt="B"]
          c [prompt="C"]
          start -> a -> b -> c -> exit
        }
        """;

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>A pipeline whose tool stage prints the directory it runs in (into <c>where/stdout.txt</c>).</summary>
    private static readonly string Workdir = $$"""
        digraph Workdir {
          start [shape=Mdiamond]
          exit [shape=Msquare]
          plan [prompt="Plan"]
          where [shape=parallelogram, tool_command="{{(OperatingSystem.IsWindows() ? "cd" : "pwd")}}"]
          start -> plan -> where -> exit
        }
        """;

    private static string RunsDir(ServerHarness server) => Path.Combine(server.Dir.Path, ".reactor", "runs");

    private static List<string> Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    private static async Task<JsonObject> StartJsonAsync(ServerHarness server, object body)
    {
        var response = await server.Client.PostAsJsonAsync("/pipelines", body);
        var json = await ServerHarness.ReadJsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToJsonString());
        return json;
    }

    private static async Task<JsonArray> RunsAsync(ServerHarness server, string query = "") =>
        (await server.GetJsonAsync("/studio/api/runs" + query))["runs"]!.AsArray();

    private static string DirName(JsonObject created) => Path.GetFileName(created["logs_root"]!.GetValue<string>());

    private static async Task<(HttpStatusCode Status, JsonObject Body)> ForkAsync(ServerHarness server, string run, object body)
    {
        var response = await server.Client.PostAsJsonAsync($"/studio/api/runs/{run}/fork", body);
        return (response.StatusCode, await ServerHarness.ReadJsonAsync(response));
    }

    /// <summary>The directory the fork's (or run's) tool stage ran in, as it printed it.</summary>
    private static string WhereItRan(JsonObject run) => File.ReadAllText(Path.Combine(run["logs_root"]!.GetValue<string>(), "where", "stdout.txt")).Trim();

    /// <summary>Starts a <see cref="Workdir"/> run in <paramref name="workingDirectory"/> and waits for it to land; returns its run directory name.</summary>
    private static async Task<string> FlyInAsync(ServerHarness server, string workingDirectory)
    {
        var created = await StartJsonAsync(server, new { dot = Workdir, working_directory = workingDirectory });
        await server.WaitForStatusAsync(created["id"]!.GetValue<string>(), "success");
        Assert.EndsWith(Path.GetFileName(workingDirectory), WhereItRan(created), StringComparison.OrdinalIgnoreCase);
        return DirName(created);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Condition not met.");
    }

    /// <summary>Writes the CLI-style fixtures used by the history tests; returns them by role.</summary>
    private static (FakeRun Factory1, FakeRun Factory2, FakeRun Looper) WriteHistoryFixtures(string runs)
    {
        var factory1 = FakeRun.Create(runs, "20210101-100000-factory", "factory", At("2021-01-01T10:00:00Z"))
            .Started("plan").Completed("plan", "success", 1000).Edge("plan", "build")
            .Started("build").Completed("build", "success", 2000).Edge("build", "plan")
            .Started("plan").Completed("plan", "success", 500)
            .Backend("plan", FakeRun.Usage("gpt-5", 1.5, 100, 10), visit: 1)
            .File(Path.Combine("plan", "history", "visit-1", "response.md"), "first")
            .Backend("plan", new JsonObject { ["model"] = "gpt-5", ["ai_units"] = 2, ["input_tokens"] = 200, ["output_tokens"] = 20, ["tool_calls"] = 3 })
            .File(Path.Combine("plan", "response.md"), "second")
            .Backend("build", new JsonObject { ["model"] = "claude", ["premium_requests"] = 2, ["cost_usd"] = 0.25, ["input_tokens"] = 50 })
            .Result("success", 4000, At("2021-01-01T10:00:04Z"))
            .Save();

        // Killed mid-run: no result.json, a corrupt line, and a half-written last line.
        var factory2 = FakeRun.Create(runs, "20210102-100000-factory", "factory", At("2021-01-02T10:00:00Z"))
            .Started("plan").Raw("{not json").Completed("plan", "success", 100).Started("build")
            .Save(partialTail: "{\"type\":\"StageCompl");

        // A loop restart continues in a sibling directory, which holds the result.
        var looper = FakeRun.Create(runs, "20210103-100000-looper", "looper", At("2021-01-03T10:00:00Z"))
            .Started("a").Completed("a", "success", 10).Save();
        var restartDir = Path.Combine(runs, "20210103-100000-looper-restart-1");
        Directory.CreateDirectory(restartDir);
        File.WriteAllText(Path.Combine(restartDir, "manifest.json"), """{"name":"looper","run_id":"id-looper","started_at":"2021-01-03T10:00:01Z"}""");
        File.WriteAllText(Path.Combine(restartDir, "events.jsonl"), string.Join('\n',
            """{"target_node_id":"a","restart_count":1,"logs_root":"x","timestamp":"2021-01-03T10:00:01Z","type":"LoopRestarted"}""",
            """{"node_id":"a","attempt":1,"timestamp":"2021-01-03T10:00:02Z","type":"StageStarted"}""",
            """{"node_id":"a","duration":"00:00:00.0200000","status":"success","timestamp":"2021-01-03T10:00:03Z","type":"StageCompleted"}""") + "\n");
        File.WriteAllText(Path.Combine(restartDir, "result.json"), """{"status":"success","cancelled":false,"duration_ms":30,"finished_at":"2021-01-03T10:00:03Z"}""");

        // Not runs: no manifest. Listed but unreadable: corrupt manifest and result.
        Directory.CreateDirectory(Path.Combine(runs, "20210104-100000-nomanifest"));
        File.WriteAllText(Path.Combine(runs, "20210104-100000-nomanifest", "events.jsonl"), "{}\n");
        Directory.CreateDirectory(Path.Combine(runs, "20210105-100000-broken"));
        File.WriteAllText(Path.Combine(runs, "20210105-100000-broken", "manifest.json"), "{broken");
        File.WriteAllText(Path.Combine(runs, "20210105-100000-broken", "result.json"), "{]");
        return (factory1, factory2, looper);
    }

    [Fact]
    public async Task History_lists_server_and_cli_runs_newest_first_with_filters_restarts_and_costs()
    {
        await using var server = await ServerHarness.StartAsync();
        var created = await StartJsonAsync(server, new { dot = Simple, studio = new { source_path = "pipelines/simple.dot" } });
        var liveId = created["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(liveId, "success");
        var (factory1, factory2, looper) = WriteHistoryFixtures(RunsDir(server));

        var runs = await RunsAsync(server);
        Assert.Equal([DirName(created), looper.Id, factory2.Id, factory1.Id, "20210105-100000-broken"], runs.Select(r => r!["id"]!.GetValue<string>()));

        var live = runs[0]!;
        Assert.Equal(liveId, live["live_id"]!.GetValue<string>());
        Assert.Equal(liveId, live["run_id"]!.GetValue<string>());
        Assert.Equal("Simple", live["name"]!.GetValue<string>());
        Assert.Equal("success", live["status"]!.GetValue<string>());
        Assert.Equal("pipelines/simple.dot", live["source_path"]!.GetValue<string>());
        Assert.Equal(4, live["stage_executions"]!.GetValue<int>());
        Assert.Equal(4, live["node_count"]!.GetValue<int>());
        Assert.False(live["simulated"]!.GetValue<bool>());
        Assert.NotNull(live["finished_at"]);
        Assert.Empty(live["cost"]!.AsObject());

        var restarted = runs[1]!;
        Assert.Equal(["20210103-100000-looper-restart-1"], Strings(restarted["restarts"]));
        Assert.Equal("success", restarted["status"]!.GetValue<string>());
        Assert.Equal(30, restarted["duration_ms"]!.GetValue<long>());
        Assert.Equal(2, restarted["stage_executions"]!.GetValue<int>());

        var interrupted = runs[2]!;
        Assert.Equal("interrupted", interrupted["status"]!.GetValue<string>());
        Assert.Null(interrupted["live_id"]);
        Assert.Null(interrupted["finished_at"]);
        Assert.Equal(2, interrupted["stage_executions"]!.GetValue<int>());

        var finished = runs[3]!;
        Assert.Equal("success", finished["status"]!.GetValue<string>());
        Assert.Equal("factory", finished["name"]!.GetValue<string>());
        Assert.Equal("id-20210101-100000-factory", finished["run_id"]!.GetValue<string>());
        Assert.Equal(4000, finished["duration_ms"]!.GetValue<long>());
        Assert.Equal(3, finished["stage_executions"]!.GetValue<int>());
        Assert.Equal(At("2021-01-01T10:00:04Z"), DateTimeOffset.Parse(finished["finished_at"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        var cost = finished["cost"]!.AsObject();
        Assert.Equal(3.5, cost["ai_units"]!.GetValue<double>());
        Assert.Equal(2, cost["premium_requests"]!.GetValue<double>());
        Assert.Equal(0.25, cost["cost_usd"]!.GetValue<double>());
        Assert.Equal(350, cost["input_tokens"]!.GetValue<long>());
        Assert.Equal(30, cost["output_tokens"]!.GetValue<long>());
        Assert.Equal(3, cost["tool_calls"]!.GetValue<long>());
        Assert.False(cost.ContainsKey("reasoning_tokens"));

        var broken = runs[4]!;
        Assert.Null(broken["name"]);
        Assert.Equal("interrupted", broken["status"]!.GetValue<string>());

        Assert.Equal([factory2.Id, factory1.Id], (await RunsAsync(server, "?name=factory")).Select(r => r!["id"]!.GetValue<string>()));
        Assert.Equal([DirName(created), looper.Id], (await RunsAsync(server, "?limit=2")).Select(r => r!["id"]!.GetValue<string>()));
        Assert.Empty(await RunsAsync(server, "?name=nothing"));

        var features = Strings((await server.GetJsonAsync("/studio/api/info"))["features"]);
        Assert.Contains("history", features);
        Assert.Contains("telemetry", features);
        Assert.Contains("fork", features);
        Assert.Contains("steering", features);
    }

    [Fact]
    public async Task Run_detail_describes_stages_checkpoints_and_files()
    {
        await using var server = await ServerHarness.StartAsync();
        var created = await StartJsonAsync(server, new { dot = Simple });
        await server.WaitForStatusAsync(created["id"]!.GetValue<string>(), "success");
        var (factory1, _, _) = WriteHistoryFixtures(RunsDir(server));

        var detail = await server.GetJsonAsync($"/studio/api/runs/{DirName(created)}");
        Assert.Equal(DirName(created), detail["summary"]!["id"]!.GetValue<string>());
        Assert.Equal("Simple", detail["manifest"]!["name"]!.GetValue<string>());
        Assert.Equal("success", detail["result"]!["status"]!.GetValue<string>());
        Assert.Equal("exit", detail["checkpoint"]!["current_node"]!.GetValue<string>());
        Assert.Equal(["start", "plan", "implement", "exit"], Strings(detail["checkpoint"]!["completed_nodes"]));
        Assert.Equal("success", detail["checkpoint"]!["node_outcomes"]!["plan"]!.GetValue<string>());
        Assert.Contains("digraph Simple", detail["graph_dot"]!.GetValue<string>());
        Assert.Null(detail["studio"]);
        Assert.Equal(["0001-start", "0002-plan", "0003-implement", "0004-exit"], Strings(detail["checkpoints"]));

        var stages = detail["stages"]!.AsArray();
        Assert.Equal(["start", "plan", "implement", "exit"], stages.Select(s => s!["node"]!.GetValue<string>()));
        var plan = stages[1]!;
        Assert.Equal(1, plan["visits"]!.GetValue<int>());
        Assert.Equal("success", plan["status"]!.GetValue<string>());
        Assert.Equal(0, plan["history_visits"]!.GetValue<int>());
        Assert.Contains("prompt.md", Strings(plan["files"]));
        Assert.Contains("status.json", Strings(plan["files"]));
        Assert.NotNull(plan["duration_ms"]);
        Assert.Equal(DirName(created), plan["segment"]!.GetValue<string>());

        var fixture = await server.GetJsonAsync($"/studio/api/runs/{factory1.Id}");
        var planStage = fixture["stages"]!.AsArray().Single(s => s!["node"]!.GetValue<string>() == "plan")!;
        Assert.Equal(2, planStage["visits"]!.GetValue<int>());
        Assert.Equal(1, planStage["history_visits"]!.GetValue<int>());
        Assert.Equal(1500, planStage["duration_ms"]!.GetValue<long>());
        Assert.Equal(3, planStage["backend"]!["tool_calls"]!.GetValue<int>());
        Assert.Equal(["backend.json", "response.md"], Strings(planStage["files"]));
        Assert.Null(fixture["checkpoint"]);
        Assert.Empty(fixture["checkpoints"]!.AsArray());

        var prompt = await server.Client.GetStringAsync($"/studio/api/runs/{DirName(created)}/stages/plan/files/prompt.md");
        Assert.Contains("Plan: Say hello", prompt);
        Assert.Equal("second", await server.Client.GetStringAsync($"/studio/api/runs/{factory1.Id}/stages/plan/files/response.md"));
        Assert.Equal("first", await server.Client.GetStringAsync($"/studio/api/runs/{factory1.Id}/stages/plan/files/response.md?visit=1"));
        Assert.Equal("second", await server.Client.GetStringAsync($"/studio/api/runs/{factory1.Id}/stages/plan/files/response.md?visit=2"));
        var json = await server.Client.GetAsync($"/studio/api/runs/{factory1.Id}/stages/build/files/backend.json");
        Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/runs/{factory1.Id}/stages/plan/files/response.md?visit=3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/runs/{factory1.Id}/stages/plan/files/response.md?visit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/runs/{factory1.Id}/stages/plan/files/missing.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/studio/api/runs/20210104-100000-nomanifest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/studio/api/runs/nope")).StatusCode);
    }

    [Fact]
    public async Task Run_detail_summarizes_what_each_stage_changed_in_the_workspace()
    {
        await using var server = await ServerHarness.StartAsync();
        var run = FakeRun.Create(RunsDir(server), "20210106-100000-changes", "changes", At("2021-01-06T10:00:00Z"))
            .Started("build").Completed("build", "success", 100)
            .File(Path.Combine("build", "changes.json"), """
                {"node":"build","mode":"git","summary":{"added":2,"modified":1,"deleted":0,"total":3},"changes":[],
                 "concurrent":true,"truncated":false,"write_scope":{"patterns":["src/**"],"enforced":false,"violations":["README.md"]}}
                """)
            .Save();

        var detail = await server.GetJsonAsync($"/studio/api/runs/{run.Id}");

        var build = detail["stages"]!.AsArray().Single(s => s!["node"]!.GetValue<string>() == "build")!;
        var changes = build["changes"]!;
        Assert.Equal((2, 1, 0, 1), (changes["added"]!.GetValue<int>(), changes["modified"]!.GetValue<int>(), changes["deleted"]!.GetValue<int>(), changes["out_of_scope"]!.GetValue<int>()));
        Assert.Equal("git", changes["mode"]!.GetValue<string>());
        Assert.True(changes["concurrent"]!.GetValue<bool>());
        Assert.Contains("changes.json", Strings(build["files"]));
        Assert.Contains("README.md", await server.Client.GetStringAsync($"/studio/api/runs/{run.Id}/stages/build/files/changes.json"));
    }

    [Theory]
    [InlineData("/studio/api/runs/..%2F..%2Fsecret")]
    [InlineData("/studio/api/runs/%2E%2E")]
    [InlineData("/studio/api/runs/%2E%2E/events")]
    [InlineData("/studio/api/runs/..%5C..%5Cwindows/events")]
    [InlineData("/studio/api/runs/C%3A%5Cwindows")]
    [InlineData("/studio/api/runs/{run}/stages/plan/files/..%2F..%2Fmanifest.json")]
    [InlineData("/studio/api/runs/{run}/stages/plan/files/..%5Cbackend.json")]
    [InlineData("/studio/api/runs/{run}/stages/plan/files/..")]
    [InlineData("/studio/api/runs/{run}/stages/..%2Fplan/files/response.md")]
    [InlineData("/studio/api/runs/{run}/stages/../files/manifest.json")]
    [InlineData("/studio/api/runs/{run}/stages/plan/files/response.md%3Astream")]
    [InlineData("/studio/api/runs/{run}%2F..%2F{run}/stages/plan/files/response.md")]
    public async Task History_paths_cannot_leave_the_runs_directory(string path)
    {
        await using var server = await ServerHarness.StartAsync();
        var (factory1, _, _) = WriteHistoryFixtures(RunsDir(server));
        File.WriteAllText(Path.Combine(server.Dir.Path, ".reactor", "secret"), "TOP-SECRET-CONTENT");
        File.WriteAllText(Path.Combine(server.Dir.Path, ".reactor", "manifest.json"), "TOP-SECRET-CONTENT");

        var response = await server.Client.GetAsync(path.Replace("{run}", factory1.Id, StringComparison.Ordinal));

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path}: {(int)response.StatusCode}");
        Assert.DoesNotContain("TOP-SECRET-CONTENT", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Events_are_paged_across_restart_directories_and_skip_corrupt_and_partial_lines()
    {
        await using var server = await ServerHarness.StartAsync();
        var created = await StartJsonAsync(server, new { dot = Simple });
        await server.WaitForStatusAsync(created["id"]!.GetValue<string>(), "success");
        var (_, factory2, looper) = WriteHistoryFixtures(RunsDir(server));
        var total = File.ReadAllLines(Path.Combine(created["logs_root"]!.GetValue<string>(), "events.jsonl")).Length;

        var first = await server.GetJsonAsync($"/studio/api/runs/{DirName(created)}/events?limit=3");
        Assert.Equal([1L, 2L, 3L], first["events"]!.AsArray().Select(e => e!["seq"]!.GetValue<long>()));
        Assert.Equal("PipelineStarted", first["events"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(3, first["next"]!.GetValue<long>());
        Assert.False(first["done"]!.GetValue<bool>());

        var rest = await server.GetJsonAsync($"/studio/api/runs/{DirName(created)}/events?since=3");
        Assert.Equal(total - 3, rest["events"]!.AsArray().Count);
        Assert.Equal(4, rest["events"]![0]!["seq"]!.GetValue<long>());
        Assert.Equal(total, rest["next"]!.GetValue<long>());
        Assert.True(rest["done"]!.GetValue<bool>());
        Assert.Equal("PipelineCompleted", rest["events"]!.AsArray()[^1]!["type"]!.GetValue<string>());

        var beyond = await server.GetJsonAsync($"/studio/api/runs/{DirName(created)}/events?since={total + 5}");
        Assert.Empty(beyond["events"]!.AsArray());
        Assert.True(beyond["done"]!.GetValue<bool>());

        var restarted = await server.GetJsonAsync($"/studio/api/runs/{looper.Id}/events");
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], restarted["events"]!.AsArray().Select(e => e!["seq"]!.GetValue<long>()));
        Assert.Equal("LoopRestarted", restarted["events"]![3]!["type"]!.GetValue<string>());
        var fromRestartDir = await server.GetJsonAsync("/studio/api/runs/20210103-100000-looper-restart-1/events?since=4");
        Assert.Equal([5L, 6L], fromRestartDir["events"]!.AsArray().Select(e => e!["seq"]!.GetValue<long>()));

        var broken = await server.GetJsonAsync($"/studio/api/runs/{factory2.Id}/events");
        Assert.Equal([1L, 2L, 4L, 5L], broken["events"]!.AsArray().Select(e => e!["seq"]!.GetValue<long>()));
        Assert.Equal(5, broken["next"]!.GetValue<long>());
        Assert.True(broken["done"]!.GetValue<bool>());
    }

    [Fact]
    public async Task History_picks_up_appended_events_and_new_results_incrementally()
    {
        await using var server = await ServerHarness.StartAsync();
        var (_, factory2, _) = WriteHistoryFixtures(RunsDir(server));
        var before = (await RunsAsync(server)).Single(r => r!["id"]!.GetValue<string>() == factory2.Id)!;
        Assert.Equal("interrupted", before["status"]!.GetValue<string>());
        Assert.Equal(2, before["stage_executions"]!.GetValue<int>());

        // The run "comes back": the half-written line is completed and more events and a result follow.
        File.AppendAllText(Path.Combine(factory2.Directory, "events.jsonl"),
            "eted\",\"node_id\":\"build\",\"status\":\"fail\",\"duration\":\"00:00:00.2500000\",\"timestamp\":\"2021-01-02T10:00:05Z\"}\n" +
            "{\"type\":\"StageFailed\",\"node_id\":\"verify\",\"error\":\"flaky\",\"will_retry\":true}\n" +
            "{\"type\":\"StageStarted\",\"node_id\":\"verify\",\"attempt\":1}\n");
        factory2.Result("fail", 5000, At("2021-01-02T10:00:05Z"), failure: "build failed");

        var after = (await RunsAsync(server)).Single(r => r!["id"]!.GetValue<string>() == factory2.Id)!;
        Assert.Equal("fail", after["status"]!.GetValue<string>());
        Assert.Equal("build failed", after["failure_reason"]!.GetValue<string>());
        Assert.Equal(3, after["stage_executions"]!.GetValue<int>());
        var build = (await server.GetJsonAsync($"/studio/api/runs/{factory2.Id}"))["stages"]!.AsArray().Single(s => s!["node"]!.GetValue<string>() == "build")!;
        Assert.Equal("fail", build["status"]!.GetValue<string>());
        Assert.Equal(250, build["duration_ms"]!.GetValue<long>());
        Assert.Equal(8, (await server.GetJsonAsync($"/studio/api/runs/{factory2.Id}/events"))["next"]!.GetValue<long>());
    }

    [Fact]
    public async Task Events_longer_than_the_read_buffer_are_read_intact()
    {
        await using var server = await ServerHarness.StartAsync();
        var message = string.Concat(Enumerable.Repeat("0123456789abcdef", 20_000));
        var run = FakeRun.Create(RunsDir(server), "20210301-000000-big", "big", At("2021-03-01T00:00:00Z"))
            .Started("plan")
            .Event("StageLog", new JsonObject { ["node_id"] = "plan", ["message"] = message, ["level"] = "info" })
            .Completed("plan", "success", 10)
            .Result("success", 10, At("2021-03-01T00:00:01Z"))
            .Save();

        var events = (await server.GetJsonAsync($"/studio/api/runs/{run.Id}/events"))["events"]!.AsArray();
        Assert.Equal(4, events.Count);
        Assert.Equal(message, events[2]!["message"]!.GetValue<string>());
        var summary = (await RunsAsync(server)).Single()!;
        Assert.Equal(1, summary["stage_executions"]!.GetValue<int>());
        Assert.Equal("success", summary["status"]!.GetValue<string>());
    }

    /// <summary>Three finished "tele" runs plus runs telemetry must ignore (simulated, unfinished, another pipeline).</summary>
    private static void WriteTelemetryFixtures(string runs)
    {
        FakeRun.Create(runs, "20210201-000000-tele", "tele", At("2021-02-01T00:00:00Z"))
            .Started("plan").Completed("plan", "success", 100).Edge("plan", "build")
            .Started("build").Completed("build", "success", 300)
            .Backend("plan", FakeRun.Usage("gpt-5", 1, 100, 10))
            .Backend("build", FakeRun.Usage("claude", 3, 300, 30))
            .Result("success", 1000, At("2021-02-01T00:00:01Z"))
            .Save();

        FakeRun.Create(runs, "20210202-000000-tele", "tele", At("2021-02-02T00:00:00Z"))
            .Started("plan").Failed("plan", "rate limited", willRetry: true).Started("plan", attempt: 2).Completed("plan", "success", 200).Edge("plan", "build")
            .Started("build").Failed("build", "tests failed", willRetry: false).Completed("build", "fail", 500).Edge("build", "plan")
            .Started("plan").Completed("plan", "success", 100).Edge("plan", "build")
            .Started("build").Failed("build", "tests failed\n  at Test.Run()", willRetry: false).Completed("build", "fail", 700)
            .Backend("plan", FakeRun.Usage("gpt-5", 2, 200, 20), visit: 1)
            .Backend("plan", FakeRun.Usage("gpt-5", 1, 100, 10))
            .Backend("build", FakeRun.Usage("claude", 4, 400, 40), visit: 1)
            .Backend("build", FakeRun.Usage("gpt-5", 6, 600, 60))
            .Result("fail", 3000, At("2021-02-02T00:00:03Z"), failure: "tests failed")
            .Save();

        FakeRun.Create(runs, "20210203-000000-tele", "tele", At("2021-02-03T00:00:00Z"))
            .Started("plan").Completed("plan", "success", 300).Edge("plan", "build")
            .Started("build")
            .Backend("plan", FakeRun.Usage("gpt-5", 3, 300, 30))
            .Result("fail", 2000, At("2021-02-03T00:00:02Z"), cancelled: true, failure: "Pipeline cancelled.")
            .Save();

        FakeRun.Create(runs, "20210204-000000-tele", "tele", At("2021-02-04T00:00:00Z"))
            .Started("plan").Completed("plan", "fail", 999)
            .File("studio.json", """{"simulated": true}""")
            .Result("fail", 999, At("2021-02-04T00:00:01Z"))
            .Save();
        FakeRun.Create(runs, "20210205-000000-tele", "tele", At("2021-02-05T00:00:00Z"))
            .Started("plan").Completed("plan", "fail", 999)
            .Save();
        FakeRun.Create(runs, "20210206-000000-other", "other", At("2021-02-06T00:00:00Z"))
            .Started("plan").Completed("plan", "fail", 999)
            .Result("fail", 999, At("2021-02-06T00:00:01Z"))
            .Save();
    }

    [Fact]
    public async Task Telemetry_aggregates_finished_non_simulated_runs_of_a_pipeline()
    {
        await using var server = await ServerHarness.StartAsync();
        WriteTelemetryFixtures(RunsDir(server));

        var t = await server.GetJsonAsync("/studio/api/telemetry?name=tele");

        Assert.Equal("tele", t["name"]!.GetValue<string>());
        Assert.Equal(3, t["runs"]!.GetValue<int>());
        Assert.Equal(At("2021-02-01T00:00:00Z"), DateTimeOffset.Parse(t["window"]!["from"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        Assert.Equal(At("2021-02-03T00:00:02Z"), DateTimeOffset.Parse(t["window"]!["to"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        Assert.Equal("""{"success":1,"fail":1,"cancelled":1,"partial_success":0}""", t["outcomes"]!.ToJsonString());
        Assert.Equal("""{"mean":2000,"p50":2000,"p95":2900}""", t["duration_ms"]!.ToJsonString());
        Assert.Equal(
            """{"ai_units":{"mean":6.666667,"total":20},"input_tokens":{"mean":666.666667,"total":2000},"output_tokens":{"mean":66.666667,"total":200}}""",
            t["cost"]!.ToJsonString());

        var plan = t["nodes"]!["plan"]!;
        Assert.Equal(3, plan["runs"]!.GetValue<int>());
        Assert.Equal(4, plan["visits"]!.GetValue<int>());
        Assert.Equal(1.333, plan["mean_visits"]!.GetValue<double>());
        Assert.Equal("""{"success":4}""", plan["outcomes"]!.ToJsonString());
        Assert.Equal(0, plan["fail_rate"]!.GetValue<double>());
        Assert.Equal(1, plan["retries"]!.GetValue<int>());
        Assert.Equal("""{"mean":175,"p50":150,"p95":285,"max":300}""", plan["duration_ms"]!.ToJsonString());
        Assert.Equal("""{"ai_units":1.75,"input_tokens":175,"output_tokens":17.5}""", plan["cost"]!.ToJsonString());
        Assert.Equal("""{"gpt-5":4}""", plan["models"]!.ToJsonString());
        Assert.Empty(plan["top_failures"]!.AsArray());

        var build = t["nodes"]!["build"]!;
        Assert.Equal(3, build["runs"]!.GetValue<int>());
        Assert.Equal(4, build["visits"]!.GetValue<int>());
        Assert.Equal("""{"fail":2,"success":1}""", build["outcomes"]!.ToJsonString());
        Assert.Equal(0.6667, build["fail_rate"]!.GetValue<double>());
        Assert.Equal(0, build["retries"]!.GetValue<int>());
        Assert.Equal("""{"mean":500,"p50":500,"p95":680,"max":700}""", build["duration_ms"]!.ToJsonString());
        Assert.Equal("""{"ai_units":4.333333,"input_tokens":433.333333,"output_tokens":43.333333}""", build["cost"]!.ToJsonString());
        Assert.Equal("""{"claude":2,"gpt-5":1}""", build["models"]!.ToJsonString());
        Assert.Equal("""[{"reason":"tests failed","count":2}]""", build["top_failures"]!.ToJsonString());
        Assert.Equal(["plan", "build"], t["nodes"]!.AsObject().Select(n => n.Key));

        Assert.Equal(
            """[{"from":"plan","to":"build","count":4,"runs":3},{"from":"build","to":"plan","count":1,"runs":1}]""",
            t["edges"]!.ToJsonString());

        var series = t["series"]!.AsArray();
        Assert.Equal(["20210201-000000-tele", "20210202-000000-tele", "20210203-000000-tele"], series.Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal(["success", "fail", "cancelled"], series.Select(s => s!["status"]!.GetValue<string>()));
        Assert.Equal([1000L, 3000L, 2000L], series.Select(s => s!["duration_ms"]!.GetValue<long>()));
        Assert.Equal([2, 4, 2], series.Select(s => s!["stage_executions"]!.GetValue<int>()));
        Assert.Equal([4.0, 13.0, 3.0], series.Select(s => s!["ai_units"]!.GetValue<double>()));
        Assert.All(series, s => Assert.Null(s!["cost_usd"]));

        var latest = await server.GetJsonAsync("/studio/api/telemetry?name=tele&limit=2");
        Assert.Equal(2, latest["runs"]!.GetValue<int>());
        Assert.Equal(["20210202-000000-tele", "20210203-000000-tele"], latest["series"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));

        var none = await server.GetJsonAsync("/studio/api/telemetry?name=missing");
        Assert.Equal(0, none["runs"]!.GetValue<int>());
        Assert.Null(none["window"]);
        Assert.Empty(none["nodes"]!.AsObject());
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/studio/api/telemetry")).StatusCode);
    }

    [Fact]
    public async Task Studio_metadata_is_saved_with_the_run_and_shown_in_status_and_history()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, MaxDotBytes = 8 * 1024 * 1024 });
        var created = await StartJsonAsync(server, new
        {
            dot = Simple,
            studio = new { source_path = "pipelines/simple.dot", layout = new { nodes = new { plan = new { x = 10, y = 20 } } } },
        });
        var id = created["id"]!.GetValue<string>();
        var studioFile = Path.Combine(created["logs_root"]!.GetValue<string>(), "studio.json");
        Assert.True(File.Exists(studioFile));
        Assert.Equal("pipelines/simple.dot", JsonNode.Parse(File.ReadAllText(studioFile))!["source_path"]!.GetValue<string>());

        var detail = await server.WaitForStatusAsync(id, "success");
        Assert.Equal("pipelines/simple.dot", detail["studio"]!["source_path"]!.GetValue<string>());
        Assert.Equal(10, detail["studio"]!["layout"]!["nodes"]!["plan"]!["x"]!.GetValue<int>());
        var summary = (await RunsAsync(server)).Single(r => r!["live_id"]?.GetValue<string>() == id)!;
        Assert.Equal("pipelines/simple.dot", summary["source_path"]!.GetValue<string>());
        Assert.Equal(10, (await server.GetJsonAsync($"/studio/api/runs/{DirName(created)}"))["studio"]!["layout"]!["nodes"]!["plan"]!["x"]!.GetValue<int>());

        // Runs without metadata have no studio.json. Simulated runs keep their directories apart (.reactor/simulations),
        // so the history and telemetry never see them; their studio.json still marks them.
        var plain = await StartJsonAsync(server, new { dot = Simple });
        await server.WaitForStatusAsync(plain["id"]!.GetValue<string>(), "success");
        Assert.False(File.Exists(Path.Combine(plain["logs_root"]!.GetValue<string>(), "studio.json")));
        Assert.Null((await server.GetJsonAsync($"/pipelines/{plain["id"]!.GetValue<string>()}"))["studio"]);
        var simulated = await StartJsonAsync(server, new { dot = Simple, simulation = new { } });
        await server.WaitForStatusAsync(simulated["id"]!.GetValue<string>(), "success");
        Assert.DoesNotContain(await RunsAsync(server), r => r!["live_id"]?.GetValue<string>() == simulated["id"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(simulated["logs_root"]!.GetValue<string>(), "studio.json")))!["simulated"]!.GetValue<bool>());
        Assert.Equal(2, (await server.GetJsonAsync("/studio/api/telemetry?name=Simple"))["runs"]!.GetValue<int>());

        var huge = await server.Client.PostAsJsonAsync("/pipelines", new { dot = Simple, studio = new { layout = new string('x', 5 * 1024 * 1024) } });
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Contains("studio", (await ServerHarness.ReadJsonAsync(huge))["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fork_continues_a_finished_run_after_a_stage_snapshot_with_a_modified_pipeline()
    {
        var calls = new List<(string Node, string Prompt)>();
        var backend = new ControllableBackend
        {
            Respond = node =>
            {
                lock (calls)
                {
                    calls.Add((node.Id, node.Prompt));
                }

                return CodergenResult.FromText("ok:" + node.Id);
            },
        };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });
        var original = await StartJsonAsync(server, new { dot = Forkable, studio = new { source_path = "forkable.dot" } });
        await server.WaitForStatusAsync(original["id"]!.GetValue<string>(), "success");
        Assert.Equal(["a", "b", "c"], calls.Select(c => c.Node));
        var source = DirName(original);
        Assert.Equal(["0001-start", "0002-a", "0003-b", "0004-c", "0005-exit"], Strings((await server.GetJsonAsync($"/studio/api/runs/{source}"))["checkpoints"]));

        calls.Clear();
        var modified = Forkable.Replace("b [prompt=\"B\"]", "b [prompt=\"B v2\"]", StringComparison.Ordinal);
        var response = await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { checkpoint = "0002-a", dot = modified, context = new { flag = "forked" } });
        var fork = await ServerHarness.ReadJsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, fork.ToJsonString());
        Assert.Equal(source, fork["forked_from"]!["id"]!.GetValue<string>());
        Assert.Equal("0002-a", fork["forked_from"]!["checkpoint"]!.GetValue<string>());
        Assert.Equal("a", fork["forked_from"]!["node"]!.GetValue<string>());
        var forkId = fork["id"]!.GetValue<string>();
        Assert.NotEqual(original["id"]!.GetValue<string>(), forkId);

        var detail = await server.WaitForStatusAsync(forkId, "success");
        Assert.Equal([("b", "B v2"), ("c", "C")], calls);
        Assert.Equal(["start", "a", "b", "c", "exit"], Strings(detail["result"]!["completed_nodes"]));
        Assert.Equal("0002-a", detail["studio"]!["forked_from"]!["checkpoint"]!.GetValue<string>());
        Assert.Equal("forkable.dot", detail["studio"]!["source_path"]!.GetValue<string>());
        var context = (await server.GetJsonAsync($"/pipelines/{forkId}/context"))["context"]!;
        Assert.Equal("forked", context["flag"]!.GetValue<string>());
        Assert.Equal(forkId, context["internal.run_id"]!.GetValue<string>());

        var forkDir = fork["logs_root"]!.GetValue<string>();
        Assert.Equal(modified, File.ReadAllText(Path.Combine(forkDir, "graph.dot")));
        Assert.Equal(forkId, JsonNode.Parse(File.ReadAllText(Path.Combine(forkDir, "manifest.json")))!["run_id"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(forkDir, "a")));
        var history = (await RunsAsync(server)).Single(r => r!["live_id"]?.GetValue<string>() == forkId)!;
        Assert.Equal(Path.GetFileName(forkDir), history["id"]!.GetValue<string>());
        Assert.Equal(source, history["forked_from"]!["id"]!.GetValue<string>());
        Assert.Equal("forkable.dot", history["source_path"]!.GetValue<string>());
        Assert.Equal(3, history["stage_executions"]!.GetValue<int>());
        Assert.Equal(["0003-b", "0004-c", "0005-exit"], Strings((await server.GetJsonAsync($"/studio/api/runs/{Path.GetFileName(forkDir)}"))["checkpoints"]));

        // Without a DOT the fork reruns the original pipeline from the snapshot.
        calls.Clear();
        var again = await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { checkpoint = "0003-b" });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        await server.WaitForStatusAsync((await ServerHarness.ReadJsonAsync(again))["id"]!.GetValue<string>(), "success");
        Assert.Equal([("c", "C")], calls);

        // Forks are partial re-flights: the telemetry only counts the original flight.
        var telemetry = await server.GetJsonAsync("/studio/api/telemetry?name=Forkable");
        Assert.Equal(1, telemetry["runs"]!.GetValue<int>());
        Assert.Equal(1, telemetry["nodes"]!["b"]!["visits"]!.GetValue<int>());
    }

    [Fact]
    public async Task Fork_rejects_invalid_pipelines_and_unknown_checkpoints()
    {
        await using var server = await ServerHarness.StartAsync();
        var original = await StartJsonAsync(server, new { dot = Forkable });
        await server.WaitForStatusAsync(original["id"]!.GetValue<string>(), "success");
        var source = DirName(original);
        var runsBefore = Directory.GetDirectories(RunsDir(server)).Length;

        var syntax = await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { checkpoint = "0002-a", dot = "digraph { a -> }" });
        Assert.Equal(HttpStatusCode.BadRequest, syntax.StatusCode);
        Assert.Contains("line", (await ServerHarness.ReadJsonAsync(syntax))["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        var invalid = await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { checkpoint = "0002-a", dot = "digraph Forkable { start [shape=Mdiamond] a [prompt=\"A\"] start -> a }" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains((await ServerHarness.ReadJsonAsync(invalid))["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "terminal_node");

        var missingNode = await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new
        {
            checkpoint = "0003-b",
            dot = "digraph Forkable { start [shape=Mdiamond] exit [shape=Msquare] a [prompt=\"A\"] start -> a -> exit }",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingNode.StatusCode);
        Assert.Contains("'b'", (await ServerHarness.ReadJsonAsync(missingNode))["error"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { checkpoint = "0042-a" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { checkpoint = "restart-1/0002-a" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { checkpoint = "../checkpoint" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.PostAsJsonAsync($"/studio/api/runs/{source}/fork", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.PostAsJsonAsync("/studio/api/runs/nope/fork", new { checkpoint = "0002-a" })).StatusCode);
        Assert.Equal(runsBefore, Directory.GetDirectories(RunsDir(server)).Length);
    }

    [Fact]
    public async Task Fork_continues_in_the_working_directory_of_a_run_in_a_client_chosen_directory()
    {
        using var project = new TempDir();
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, AllowClientWorkingDirectory = true });
        Assert.True((await server.GetJsonAsync("/studio/api/info"))["allow_client_working_directory"]!.GetValue<bool>());
        var source = await FlyInAsync(server, project.Path);
        var summary = (await RunsAsync(server)).Single(r => r!["id"]!.GetValue<string>() == source)!;
        Assert.Equal(project.Path, summary["working_directory"]!.GetValue<string>());
        Assert.Equal(project.Path, (await server.GetJsonAsync($"/studio/api/runs/{source}"))["summary"]!["working_directory"]!.GetValue<string>());
        var live = JsonNode.Parse(await server.Client.GetStringAsync("/pipelines"))!.AsArray().Single()!;
        Assert.Equal(project.Path, live["working_directory"]!.GetValue<string>());

        var (status, fork) = await ForkAsync(server, source, new { checkpoint = "0002-plan" });

        Assert.True(status == HttpStatusCode.Created, fork.ToJsonString());
        Assert.Equal(project.Path, fork["working_directory"]!.GetValue<string>());
        Assert.Equal("source_run", fork["working_directory_source"]!.GetValue<string>());
        Assert.Equal(project.Path, fork["source_working_directory"]!.GetValue<string>());
        Assert.Null(fork["working_directory_note"]);
        var detail = await server.WaitForStatusAsync(fork["id"]!.GetValue<string>(), "success");
        Assert.Equal(project.Path, detail["working_directory"]!.GetValue<string>());
        Assert.EndsWith(Path.GetFileName(project.Path), WhereItRan(fork), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(project.Path, JsonNode.Parse(File.ReadAllText(Path.Combine(fork["logs_root"]!.GetValue<string>(), "manifest.json")))!["working_directory"]!.GetValue<string>());
        Assert.Equal(project.Path, (await RunsAsync(server)).Single(r => r!["id"]!.GetValue<string>() == DirName(fork))!["working_directory"]!.GetValue<string>());

        // A fork of the fork stays there too; naming the source run's directory explicitly needs no confirmation.
        var sameDirectory = OperatingSystem.IsWindows() ? project.Path.ToUpperInvariant() : project.Path;
        var (again, refork) = await ForkAsync(server, DirName(fork), new { checkpoint = "0003-where", working_directory = sameDirectory });
        Assert.True(again == HttpStatusCode.Created, refork.ToJsonString());
        Assert.Equal("request", refork["working_directory_source"]!.GetValue<string>());
        await server.WaitForStatusAsync(refork["id"]!.GetValue<string>(), "success");
    }

    [Fact]
    public async Task Forking_into_another_directory_than_the_source_runs_needs_confirmation()
    {
        using var project = new TempDir();
        using var elsewhere = new TempDir();
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, AllowClientWorkingDirectory = true });
        var source = await FlyInAsync(server, project.Path);
        var runsBefore = Directory.GetDirectories(RunsDir(server)).Length;

        var (status, refused) = await ForkAsync(server, source, new { checkpoint = "0002-plan", working_directory = elsewhere.Path });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("working_directory_confirmation_required", refused["code"]!.GetValue<string>());
        Assert.Equal(elsewhere.Path, refused["working_directory"]!.GetValue<string>());
        Assert.Equal(project.Path, refused["source_working_directory"]!.GetValue<string>());
        Assert.Contains(project.Path, refused["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("confirm_working_directory", refused["error"]!.GetValue<string>(), StringComparison.Ordinal);

        // The server's own directory is another directory too.
        var (toServer, refusedServer) = await ForkAsync(server, source, new { checkpoint = "0002-plan", working_directory = server.Dir.Path, confirm_working_directory = false });
        Assert.Equal(HttpStatusCode.Conflict, toServer);
        Assert.Equal("working_directory_confirmation_required", refusedServer["code"]!.GetValue<string>());
        Assert.Equal(runsBefore, Directory.GetDirectories(RunsDir(server)).Length);

        var (confirmed, fork) = await ForkAsync(server, source, new { checkpoint = "0002-plan", working_directory = elsewhere.Path, confirm_working_directory = true });
        Assert.True(confirmed == HttpStatusCode.Created, fork.ToJsonString());
        Assert.Equal(elsewhere.Path, fork["working_directory"]!.GetValue<string>());
        Assert.Equal("request", fork["working_directory_source"]!.GetValue<string>());
        Assert.Equal(project.Path, fork["source_working_directory"]!.GetValue<string>());
        await server.WaitForStatusAsync(fork["id"]!.GetValue<string>(), "success");
        Assert.EndsWith(Path.GetFileName(elsewhere.Path), WhereItRan(fork), StringComparison.OrdinalIgnoreCase);

        var (missing, gone) = await ForkAsync(server, source, new { checkpoint = "0002-plan", working_directory = Path.Combine(elsewhere.Path, "nope"), confirm_working_directory = true });
        Assert.Equal(HttpStatusCode.Conflict, missing);
        Assert.Equal("working_directory_missing", gone["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Fork_refuses_a_source_directory_the_server_may_not_use_instead_of_using_its_own()
    {
        using var shared = new TempDir();
        using var project = new TempDir();
        var scratch = Path.Combine(shared.Path, ".reactor", "studio", "architect", "20260101-000000-abc123");
        Directory.CreateDirectory(scratch);
        string inProject, inScratch;

        // Runs in other directories, like `reactor run -C <dir>` runs or runs of a server that allowed client directories.
        await using (var first = await ServerHarness.StartAsync(_ => new ReactorServerOptions { WorkingDirectory = shared.Path, AllowClientWorkingDirectory = true }))
        {
            inProject = await FlyInAsync(first, project.Path);
            inScratch = await FlyInAsync(first, scratch);
        }

        await using var server = await ServerHarness.StartAsync(_ => new ReactorServerOptions { WorkingDirectory = shared.Path });
        Assert.False((await server.GetJsonAsync("/studio/api/info"))["allow_client_working_directory"]!.GetValue<bool>());
        Assert.Equal(project.Path, (await RunsAsync(server)).Single(r => r!["id"]!.GetValue<string>() == inProject)!["working_directory"]!.GetValue<string>());
        var runsDir = Path.Combine(shared.Path, ".reactor", "runs");
        var runsBefore = Directory.GetDirectories(runsDir).Length;

        var (status, refused) = await ForkAsync(server, inProject, new { checkpoint = "0002-plan" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("working_directory_not_allowed", refused["code"]!.GetValue<string>());
        Assert.Equal(project.Path, refused["working_directory"]!.GetValue<string>());
        Assert.Equal(project.Path, refused["source_working_directory"]!.GetValue<string>());
        Assert.Equal(shared.Path, refused["server_working_directory"]!.GetValue<string>());
        var error = refused["error"]!.GetValue<string>();
        Assert.Contains(project.Path, error, StringComparison.Ordinal);
        Assert.Contains("--allow-client-workdir", error, StringComparison.Ordinal);
        Assert.Contains(shared.Path, error, StringComparison.Ordinal);

        var (explicitly, refusedExplicit) = await ForkAsync(server, inProject, new { checkpoint = "0002-plan", working_directory = project.Path, confirm_working_directory = true });
        Assert.Equal(HttpStatusCode.Forbidden, explicitly);
        Assert.Equal("working_directory_not_allowed", refusedExplicit["code"]!.GetValue<string>());

        // Requested directories are refused before they are looked at (missing or not).
        var (probe, refusedProbe) = await ForkAsync(server, inProject, new { checkpoint = "0002-plan", working_directory = Path.Combine(project.Path, "nope"), confirm_working_directory = true });
        Assert.Equal(HttpStatusCode.Forbidden, probe);
        Assert.Equal("working_directory_not_allowed", refusedProbe["code"]!.GetValue<string>());
        Assert.Equal(runsBefore, Directory.GetDirectories(runsDir).Length);
        Assert.Empty(JsonNode.Parse(await server.Client.GetStringAsync("/pipelines"))!.AsArray());

        // Runs the server itself placed under its .reactor directory (the Architect's scratch directories) may be re-flown.
        var (scratchStatus, scratchFork) = await ForkAsync(server, inScratch, new { checkpoint = "0002-plan" });
        Assert.True(scratchStatus == HttpStatusCode.Created, scratchFork.ToJsonString());
        Assert.Equal(scratch, scratchFork["working_directory"]!.GetValue<string>());
        await server.WaitForStatusAsync(scratchFork["id"]!.GetValue<string>(), "success");

        // Moving to the server's own directory is the way forward, once confirmed.
        var (unconfirmed, confirmation) = await ForkAsync(server, inProject, new { checkpoint = "0002-plan", working_directory = shared.Path });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed);
        Assert.Equal("working_directory_confirmation_required", confirmation["code"]!.GetValue<string>());
        var (moved, fork) = await ForkAsync(server, inProject, new { checkpoint = "0002-plan", working_directory = shared.Path, confirm_working_directory = true });
        Assert.True(moved == HttpStatusCode.Created, fork.ToJsonString());
        Assert.Equal(shared.Path, fork["working_directory"]!.GetValue<string>());
        await server.WaitForStatusAsync(fork["id"]!.GetValue<string>(), "success");
        Assert.EndsWith(Path.GetFileName(shared.Path), WhereItRan(fork), StringComparison.OrdinalIgnoreCase);

        // Once the source run's directory is gone, that is what the error says (whatever the server allows).
        Directory.Delete(project.Path, recursive: true);
        var (missing, gone) = await ForkAsync(server, inProject, new { checkpoint = "0002-plan" });
        Assert.Equal(HttpStatusCode.Conflict, missing);
        Assert.Equal("working_directory_missing", gone["code"]!.GetValue<string>());
        Assert.Contains(project.Path, gone["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("no longer exists", gone["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(shared.Path, gone["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fork_fails_clearly_when_the_source_runs_working_directory_is_gone()
    {
        var project = new TempDir();
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, AllowClientWorkingDirectory = true });
        var source = await FlyInAsync(server, project.Path);
        project.Dispose();
        Assert.False(Directory.Exists(project.Path));
        var runsBefore = Directory.GetDirectories(RunsDir(server)).Length;

        var (status, refused) = await ForkAsync(server, source, new { checkpoint = "0002-plan" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("working_directory_missing", refused["code"]!.GetValue<string>());
        Assert.Equal(project.Path, refused["working_directory"]!.GetValue<string>());
        Assert.Contains(project.Path, refused["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("no longer exists", refused["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(runsBefore, Directory.GetDirectories(RunsDir(server)).Length);
    }

    [Fact]
    public async Task Fork_of_a_run_that_did_not_record_its_working_directory_uses_the_servers_and_says_so()
    {
        using var shared = new TempDir();
        string source;
        await using (var first = await ServerHarness.StartAsync(_ => new ReactorServerOptions { WorkingDirectory = shared.Path }))
        {
            var created = await first.StartRunAsync(Workdir);
            await first.WaitForStatusAsync(created["id"]!.GetValue<string>(), "success");
            source = DirName(created);
        }

        // Run directories of older versions have no working_directory in their manifest.
        var manifestPath = Path.Combine(shared.Path, ".reactor", "runs", source, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        Assert.True(manifest.Remove("working_directory"));
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        await using var server = await ServerHarness.StartAsync(_ => new ReactorServerOptions { WorkingDirectory = shared.Path });
        Assert.Null((await RunsAsync(server)).Single()!["working_directory"]);

        var (status, fork) = await ForkAsync(server, source, new { checkpoint = "0002-plan" });

        Assert.True(status == HttpStatusCode.Created, fork.ToJsonString());
        Assert.Equal(shared.Path, fork["working_directory"]!.GetValue<string>());
        Assert.Equal("server", fork["working_directory_source"]!.GetValue<string>());
        Assert.Null(fork["source_working_directory"]);
        Assert.Contains("did not record its working directory", fork["working_directory_note"]!.GetValue<string>(), StringComparison.Ordinal);
        await server.WaitForStatusAsync(fork["id"]!.GetValue<string>(), "success");
    }

    /// <summary>A backend whose stages a, b, and c change files in <paramref name="repo"/>.</summary>
    private static ControllableBackend FileWriter(GitRepo repo) => new()
    {
        Respond = node =>
        {
            switch (node.Id)
            {
                case "a":
                    repo.Write("app.txt", "v1");
                    repo.Write("notes/a.txt", "a");
                    break;
                case "b":
                    repo.Write("app.txt", "v2");
                    File.Delete(repo.Combine("seed.txt"));
                    repo.Write("b.txt", "b");
                    break;
                case "c":
                    repo.Write("app.txt", "v3");
                    repo.Write("c.txt", "c");
                    break;
            }

            return CodergenResult.FromText("ok:" + node.Id);
        },
    };

    [Fact]
    public async Task Fork_can_rewind_the_working_directory_to_its_checkpoint_and_records_how_to_undo_it()
    {
        using var repo = GitRepo.Create(new Dictionary<string, string> { ["seed.txt"] = "seed", [".gitignore"] = "ignored/\n" });
        var backend = FileWriter(repo);
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend, AllowClientWorkingDirectory = true });
        var original = await StartJsonAsync(server, new { dot = Forkable, working_directory = repo.Path });
        await server.WaitForStatusAsync(original["id"]!.GetValue<string>(), "success");
        var source = DirName(original);
        var detail = await server.GetJsonAsync($"/studio/api/runs/{source}");
        Assert.Equal("git", detail["workspace"]!["mode"]!.GetValue<string>());
        Assert.Equal(["0001-start", "0002-a", "0003-b", "0004-c", "0005-exit"], Strings(detail["workspace_checkpoints"]));

        // Work done by hand since the run: an untracked file, and ignored files the rewind leaves alone.
        repo.Write("scratch.txt", "mine");
        repo.Write("ignored/keep.txt", "keep");
        repo.Write("node_modules/pkg/index.js", "module");
        var head = repo.Git("rev-parse", "HEAD");
        var staged = repo.Git("ls-files", "--stage");

        // b, the first stage the fork runs, waits, so the rewound directory can be inspected.
        backend.Block.Add("b");
        var (status, fork) = await ForkAsync(server, source, new { checkpoint = "0002-a", restore_workspace = true });

        Assert.True(status == HttpStatusCode.Created, fork.ToJsonString());
        var restore = fork["workspace_restore"]!.AsObject();
        Assert.Equal("0002-a", restore["checkpoint"]!.GetValue<string>());
        Assert.Equal((2, 3), (restore["written"]!.GetValue<int>(), restore["deleted"]!.GetValue<int>()));
        Assert.Equal(["app.txt", "b.txt", "c.txt", "scratch.txt", "seed.txt"], Strings(restore["paths"]));
        Assert.Equal("v1", repo.Read("app.txt"));
        Assert.Equal("a", repo.Read("notes/a.txt"));
        Assert.Equal("seed", repo.Read("seed.txt"));
        Assert.False(repo.Exists("b.txt") || repo.Exists("c.txt") || repo.Exists("scratch.txt"));
        Assert.Equal("keep", repo.Read("ignored/keep.txt"));
        Assert.Equal("module", repo.Read("node_modules/pkg/index.js"));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Equal(staged, repo.Git("ls-files", "--stage"));

        // The state before the rewind is a commit on the fork's ref, recorded with the fork.
        var forkId = fork["id"]!.GetValue<string>();
        var undo = restore["undo_commit"]!.GetValue<string>();
        Assert.Equal($"refs/reactor/runs/{forkId}/before-rewind", restore["undo_ref"]!.GetValue<string>());
        Assert.Equal(undo, repo.Git("rev-parse", $"refs/reactor/runs/{forkId}/before-rewind"));
        Assert.Equal("mine", repo.Git("show", $"{undo}:scratch.txt"));
        Assert.Contains(undo, restore["undo_command"]!.GetValue<string>(), StringComparison.Ordinal);
        var forkDetail = await server.GetJsonAsync($"/studio/api/runs/{DirName(fork)}");
        Assert.Equal(undo, forkDetail["manifest"]!["workspace_restore"]!["undo_commit"]!.GetValue<string>());
        Assert.Equal(undo, forkDetail["studio"]!["workspace_restore"]!["undo_commit"]!.GetValue<string>());

        backend.Release.TrySetResult();
        await server.WaitForStatusAsync(forkId, "success");
        Assert.Equal("v3", repo.Read("app.txt"));
        Assert.False(repo.Exists("seed.txt"));

        // The documented undo brings the hand-made state back; a fork of the fork does not inherit the restore record.
        repo.Git("restore", "--source", undo, "--worktree", "--", ".");
        Assert.Equal("mine", repo.Read("scratch.txt"));
        var (again, refork) = await ForkAsync(server, DirName(fork), new { checkpoint = "0004-c" });
        Assert.True(again == HttpStatusCode.Created, refork.ToJsonString());
        Assert.Null(refork["workspace_restore"]);
        Assert.Null((await server.WaitForStatusAsync(refork["id"]!.GetValue<string>(), "success"))["studio"]?["workspace_restore"]);
    }

    [Fact]
    public async Task Fork_refuses_to_rewind_without_a_git_snapshot_into_another_directory_or_while_a_run_works_there()
    {
        using var repo = GitRepo.Create(new Dictionary<string, string> { ["seed.txt"] = "seed", ["sub/x.txt"] = "x" });
        using var plain = new TempDir();
        var backend = new ControllableBackend();
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend, AllowClientWorkingDirectory = true });
        var inRepo = await StartJsonAsync(server, new { dot = Forkable, working_directory = repo.Path });
        await server.WaitForStatusAsync(inRepo["id"]!.GetValue<string>(), "success");
        var inPlain = await StartJsonAsync(server, new { dot = Forkable, working_directory = plain.Path });
        await server.WaitForStatusAsync(inPlain["id"]!.GetValue<string>(), "success");
        var runsBefore = Directory.GetDirectories(RunsDir(server)).Length;

        async Task<JsonObject> RefusedAsync(string run, object body, string code)
        {
            var (status, refused) = await ForkAsync(server, run, body);
            Assert.True(status == HttpStatusCode.Conflict, refused.ToJsonString());
            Assert.Equal(code, refused["code"]!.GetValue<string>());
            return refused;
        }

        // Outside git the workspace is recorded with a file walk, which keeps no contents to go back to.
        var plainDetail = await server.GetJsonAsync($"/studio/api/runs/{DirName(inPlain)}");
        Assert.Equal("walk", plainDetail["workspace"]!["mode"]!.GetValue<string>());
        Assert.Empty(plainDetail["workspace_checkpoints"]!.AsArray());
        Assert.Contains("file walk", (await RefusedAsync(DirName(inPlain), new { checkpoint = "0002-a", restore_workspace = true }, "rewind_unavailable"))["error"]!.GetValue<string>(), StringComparison.Ordinal);

        // A checkpoint saved without a workspace snapshot.
        File.Delete(Path.Combine(inRepo["logs_root"]!.GetValue<string>(), "checkpoints", "0002-a.workspace.json"));
        await RefusedAsync(DirName(inRepo), new { checkpoint = "0002-a", restore_workspace = true }, "workspace_snapshot_missing");

        // Another directory than the one the snapshot recorded: here a subdirectory of the same repository.
        var mismatch = await RefusedAsync(DirName(inRepo), new { checkpoint = "0003-b", restore_workspace = true, working_directory = Path.Combine(repo.Path, "sub"), confirm_working_directory = true }, "workspace_mismatch");
        Assert.Equal(Path.Combine(repo.Path, "sub"), mismatch["working_directory"]!.GetValue<string>());

        // A run still working in the directory.
        backend.Block.Add("a");
        var active = await StartJsonAsync(server, new { dot = Forkable, working_directory = repo.Path });
        await RefusedAsync(DirName(inRepo), new { checkpoint = "0003-b", restore_workspace = true }, "workspace_busy");
        backend.Release.TrySetResult();
        await server.WaitForStatusAsync(active["id"]!.GetValue<string>(), "success");

        Assert.Equal(runsBefore + 1, Directory.GetDirectories(RunsDir(server)).Length);
        Assert.DoesNotContain("before-rewind", repo.Git("for-each-ref", "--format=%(refname)", "refs/reactor/"), StringComparison.Ordinal);
        Assert.Equal("seed", repo.Read("seed.txt"));
    }

    [Fact]
    public async Task Steering_is_written_into_the_running_stage_directory()
    {
        var backend = new ControllableBackend { Block = { "implement" } };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });
        var created = await server.StartRunAsync(Simple);
        var id = created["id"]!.GetValue<string>();
        await WaitUntilAsync(async () => (await server.GetJsonAsync($"/pipelines/{id}"))["current_node"]?.GetValue<string>() == "implement");
        var steeringFile = Path.Combine(created["logs_root"]!.GetValue<string>(), "implement", "steering.md");

        var response = await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = "Focus on the failing test first." });
        var body = await ServerHarness.ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("queued", body["status"]!.GetValue<string>());
        Assert.Equal("implement", body["node"]!.GetValue<string>());
        Assert.Equal(steeringFile, body["path"]!.GetValue<string>());
        Assert.Equal("Focus on the failing test first.\n", File.ReadAllText(steeringFile, Encoding.UTF8));

        var second = await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = "Keep the API stable.", node = "implement" });
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("Focus on the failing test first.\n\nKeep the API stable.\n", File.ReadAllText(steeringFile, Encoding.UTF8));
        Assert.Equal(["implement"], Strings((await server.GetJsonAsync($"/pipelines/{id}"))["active_nodes"]));

        Assert.Equal(HttpStatusCode.Conflict, (await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = "x", node = "plan" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = "x", node = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = new string('x', 17 * 1024) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.PostAsJsonAsync("/studio/api/pipelines/nope/steer", new { message = "x" })).StatusCode);

        backend.Release.SetResult();
        await server.WaitForStatusAsync(id, "success");
        Assert.Equal(HttpStatusCode.Conflict, (await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = "too late" })).StatusCode);

        var stream = await server.Client.GetStringAsync($"/pipelines/{id}/events");
        Assert.Contains("steering queued by user: Focus on the failing test first.", stream, StringComparison.Ordinal);
        Assert.Contains("steering queued by user: Keep the API stable.", stream, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stages_that_are_not_agent_stages_cannot_be_steered()
    {
        await using var server = await ServerHarness.StartAsync();
        var id = (await server.StartRunAsync("""
            digraph Gate {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              review [shape=hexagon, label="Ship it?"]
              start -> review
              review -> exit [label="[A] Approve"]
            }
            """))["id"]!.GetValue<string>();
        var question = (await server.WaitForQuestionsAsync(id))[0]!;

        var response = await server.Client.PostAsJsonAsync($"/studio/api/pipelines/{id}/steer", new { message = "approve it" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("wait.human", (await ServerHarness.ReadJsonAsync(response))["error"]!.GetValue<string>(), StringComparison.Ordinal);

        await server.Client.PostAsJsonAsync($"/pipelines/{id}/questions/{question["id"]!.GetValue<string>()}/answer", new { value = "A" });
        await server.WaitForStatusAsync(id, "success");
    }

    [Fact]
    public async Task History_survives_a_server_restart_and_lists_queued_runs()
    {
        using var shared = new TempDir();
        string finishedDir;
        await using (var first = await ServerHarness.StartAsync(_ => new ReactorServerOptions { WorkingDirectory = shared.Path }))
        {
            var created = await first.StartRunAsync(Simple);
            await first.WaitForStatusAsync(created["id"]!.GetValue<string>(), "success");
            finishedDir = DirName(created);
        }

        var backend = new ControllableBackend { Block = { "implement" } };
        await using var second = await ServerHarness.StartAsync(_ => new ReactorServerOptions { WorkingDirectory = shared.Path, Backend = backend, MaxConcurrentRuns = 1 });
        var running = (await second.StartRunAsync(Simple))["id"]!.GetValue<string>();
        await second.WaitForStatusAsync(running, "running");
        var queued = (await second.StartRunAsync(Simple))["id"]!.GetValue<string>();

        var runs = await RunsAsync(second);
        var old = runs.Single(r => r!["id"]!.GetValue<string>() == finishedDir)!;
        Assert.Equal("success", old["status"]!.GetValue<string>());
        Assert.Null(old["live_id"]);
        Assert.Equal("running", runs.Single(r => r!["live_id"]?.GetValue<string>() == running)!["status"]!.GetValue<string>());
        var waiting = runs.Single(r => r!["live_id"]?.GetValue<string>() == queued)!;
        Assert.Equal("queued", waiting["status"]!.GetValue<string>());
        Assert.Equal("Simple", waiting["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await second.Client.PostAsJsonAsync($"/studio/api/pipelines/{queued}/steer", new { message = "not yet" })).StatusCode);

        backend.Release.SetResult();
        await second.WaitForStatusAsync(running, "success");
        await second.WaitForStatusAsync(queued, "success");
        Assert.All(await RunsAsync(second), r => Assert.Equal("success", r!["status"]!.GetValue<string>()));
    }
}

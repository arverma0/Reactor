using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Reactor.Server.Tests;

/// <summary>
/// The flight record's view of the engine's flow-control and quality events: usage of retried attempts and earlier
/// visits, browsable attempt archives, outcome metrics, failures that flowed onward, visit limits, and budget stops — in
/// run summaries, run details, stage archives, and telemetry.
/// </summary>
public sealed class FlightRecordTests
{
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string RunsDir(ServerHarness server) => Path.Combine(server.Dir.Path, ".reactor", "runs");

    private static JsonObject Usd(double cost, string? outcome = null, string? error = null) => new()
    {
        ["model"] = "gpt-5",
        ["cost_usd"] = cost,
        ["input_tokens"] = 1000,
        ["model_calls"] = 2,
        ["outcome"] = outcome,
        ["error"] = error,
    };

    /// <summary>
    /// <c>build</c> ran twice. Visit 1 timed out once (archived attempt 1) and then succeeded; visit 2 errored, timed out,
    /// and failed for good. Every attempt left a <c>backend.json</c>.
    /// </summary>
    private static FakeRun WriteRetriedRun(string runs, string dir = "20210301-000000-retry", string name = "retry")
    {
        var visit1 = Path.Combine("build", "history", "visit-1");
        return FakeRun.Create(runs, dir, name, At("2021-03-01T00:00:00Z"))
            .Started("build").Failed("build", "Stage timed out after 10m", willRetry: true).Started("build", attempt: 2).Completed("build", "success", 1000)
            .Edge("build", "check").Started("check").Completed("check", "fail", 10).Edge("check", "build")
            .Started("build").Failed("build", "socket closed", willRetry: true).Started("build", attempt: 2)
            .Failed("build", "Stage timed out after 10m", willRetry: true).Started("build", attempt: 3).Completed("build", "fail", 900)
            .File(Path.Combine(visit1, "attempts", "attempt-1", "backend.json"), Usd(0.5, "timeout", "Stage timed out after 10m").ToJsonString())
            .File(Path.Combine(visit1, "attempts", "attempt-1", "response.partial.md"), "I was halfway through the parser.")
            .File(Path.Combine(visit1, "attempts", "attempt-1", "prompt.md"), "first try")
            .File(Path.Combine(visit1, "backend.json"), Usd(1).ToJsonString())
            .File(Path.Combine(visit1, "response.md"), "visit one done")
            .File(Path.Combine("build", "attempts", "attempt-1", "backend.json"), Usd(0.25, "error", "socket closed").ToJsonString())
            .File(Path.Combine("build", "attempts", "attempt-2", "backend.json"), Usd(0.75, "timeout").ToJsonString())
            .File(Path.Combine("build", "attempts", "attempt-2", "response.partial.md"), "Ran out of time while fixing tests.")
            .File(Path.Combine("build", "backend.json"), Usd(2).ToJsonString())
            .File(Path.Combine("build", "status.json"), """{"outcome":"fail","failure_reason":"3 tests fail"}""")
            .File(Path.Combine("build", "response.md"), "gave up")
            .Result("fail", 5000, At("2021-03-01T00:00:05Z"), failure: "3 tests fail")
            .Save();
    }

    [Fact]
    public async Task Costs_include_retried_attempts_and_earlier_visits()
    {
        await using var server = await ServerHarness.StartAsync();
        var run = WriteRetriedRun(RunsDir(server));

        var summary = (await server.GetJsonAsync("/studio/api/runs"))["runs"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == run.Id)!;
        var cost = summary["cost"]!.AsObject();
        // 0.5 + 1 (visit 1) + 0.25 + 0.75 + 2 (visit 2): the dead attempts' spend is part of the bill.
        Assert.Equal(4.5, cost["cost_usd"]!.GetValue<double>());
        Assert.Equal(5000, cost["input_tokens"]!.GetValue<long>());
        Assert.Equal(10, cost["model_calls"]!.GetValue<long>());
        Assert.Equal("cost_usd", cost.First().Key);

        var detail = await server.GetJsonAsync($"/studio/api/runs/{run.Id}");
        var build = detail["stages"]!.AsArray().Single(s => s!["node"]!.GetValue<string>() == "build")!;
        Assert.Equal(2, build["visits"]!.GetValue<int>());
        Assert.Equal(3, build["attempts"]!.GetValue<int>());
        Assert.Equal(4.5, build["cost"]!["cost_usd"]!.GetValue<double>());
        Assert.Equal(2, build["backend"]!["cost_usd"]!.GetValue<double>());

        var telemetry = await server.GetJsonAsync("/studio/api/telemetry?name=retry");
        var node = telemetry["nodes"]!["build"]!;
        // Per visit: 1.5 (visit 1 with its timed-out attempt) and 3 (visit 2 with two failed attempts).
        Assert.Equal(2.25, node["cost"]!["cost_usd"]!.GetValue<double>());
        Assert.Equal("""{"gpt-5":2}""", node["models"]!.ToJsonString());
        Assert.Equal(4.5, telemetry["cost"]!["cost_usd"]!["total"]!.GetValue<double>());
    }

    [Fact]
    public async Task Stage_archives_list_every_visit_and_retried_attempt_with_how_it_ended()
    {
        await using var server = await ServerHarness.StartAsync();
        var run = WriteRetriedRun(RunsDir(server));

        var archive = await server.GetJsonAsync($"/studio/api/runs/{run.Id}/stages/build/visits");
        Assert.Equal("build", archive["node"]!.GetValue<string>());
        Assert.Equal(run.Id, archive["run"]!.GetValue<string>());
        var visits = archive["visits"]!.AsArray();
        Assert.Equal([1, 2], visits.Select(v => v!["visit"]!.GetValue<int>()));
        Assert.Equal([false, true], visits.Select(v => v!["latest"]!.GetValue<bool>()));

        var first = visits[0]!;
        Assert.Null(first["outcome"]);
        Assert.Equal(["backend.json", "response.md"], first["files"]!.AsArray().Select(f => f!.GetValue<string>()));
        var timedOut = first["attempts"]!.AsArray().Single()!;
        Assert.Equal(1, timedOut["attempt"]!.GetValue<int>());
        Assert.Equal("timeout", timedOut["outcome"]!.GetValue<string>());
        Assert.Equal("Stage timed out after 10m", timedOut["error"]!.GetValue<string>());
        Assert.Equal(0.5, timedOut["backend"]!["cost_usd"]!.GetValue<double>());
        Assert.Equal(["backend.json", "prompt.md", "response.partial.md"], timedOut["files"]!.AsArray().Select(f => f!.GetValue<string>()));

        var latest = visits[1]!;
        Assert.Equal("fail", latest["outcome"]!.GetValue<string>());
        Assert.Equal("3 tests fail", latest["error"]!.GetValue<string>());
        Assert.Equal(["error", "timeout"], latest["attempts"]!.AsArray().Select(a => a!["outcome"]!.GetValue<string>()));

        // Attempt files: the latest visit's attempts, and an earlier visit's.
        Assert.Equal("Ran out of time while fixing tests.", await server.Client.GetStringAsync($"/studio/api/runs/{run.Id}/stages/build/files/response.partial.md?attempt=2"));
        Assert.Equal("Ran out of time while fixing tests.", await server.Client.GetStringAsync($"/studio/api/runs/{run.Id}/stages/build/files/response.partial.md?visit=2&attempt=2"));
        Assert.Equal("I was halfway through the parser.", await server.Client.GetStringAsync($"/studio/api/runs/{run.Id}/stages/build/files/response.partial.md?visit=1&attempt=1"));
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/runs/{run.Id}/stages/build/files/backend.json?attempt=3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/runs/{run.Id}/stages/build/files/backend.json?attempt=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/runs/{run.Id}/stages/build/files/backend.json?visit=1&attempt=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/studio/api/runs/{run.Id}/stages/missing/visits")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/studio/api/runs/nope/stages/build/visits")).StatusCode);
    }

    /// <summary>A polish loop with metrics, a visit limit, a failure that flowed on, and runs that stopped at their budget.</summary>
    private static void WriteQualityRuns(string runs)
    {
        // Landed, but "docs" failed and the run carried on: a dirty landing. Polish improved the score each round.
        FakeRun.Create(runs, "20210401-000000-quality", "quality", At("2021-04-01T00:00:00Z"))
            .Started("polish").Completed("polish", "success", 100).Metrics("polish", ("score", 2), ("qa_failed", 5))
            .Edge("polish", "review").Started("review").Completed("review", "fail", 10).Edge("review", "polish")
            .Started("polish").Completed("polish", "success", 100).Metrics("polish", ("score", 4), ("qa_failed", 1))
            .Edge("polish", "review").Started("review").Completed("review", "fail", 10).Edge("review", "polish")
            .VisitLimit("polish", 2, "approve")
            .Started("approve").Completed("approve", "success", 10).Edge("approve", "docs")
            .Started("docs").Completed("docs", "fail", 10).Continued("docs", "exit", "no README")
            .Result("success", 1000, At("2021-04-01T00:00:01Z"))
            .Save();

        // A goal gate sent the run back and the failed stage succeeded later: a clean landing.
        FakeRun.Create(runs, "20210402-000000-quality", "quality", At("2021-04-02T00:00:00Z"))
            .Started("polish").Completed("polish", "success", 100).Metrics("polish", ("score", 5), ("qa_failed", 0))
            .Started("docs").Completed("docs", "fail", 10).Continued("docs", "exit")
            .Started("docs").Completed("docs", "success", 10)
            .Result("success", 2000, At("2021-04-02T00:00:02Z"))
            .Save();

        // Stopped at its budget, resumed with a higher one, stopped again.
        FakeRun.Create(runs, "20210403-000000-quality", "quality", At("2021-04-03T00:00:00Z"))
            .Started("polish").Completed("polish", "success", 100).Metrics("polish", ("score", 3))
            .Budget(1.25, 1, "review").Event("PipelineFailed", new JsonObject { ["error"] = "Budget exceeded", ["duration"] = "00:00:01" })
            .Event("PipelineStarted", new JsonObject { ["name"] = "quality" })
            .Budget(5.5, 5, "review").Event("PipelineFailed", new JsonObject { ["error"] = "Budget exceeded", ["duration"] = "00:00:01" })
            .Result("fail", 3000, At("2021-04-03T00:00:03Z"), failure: "Budget exceeded: $5.50 spent, max_cost is $5.00.")
            .Save();

        // Stopped at its budget once, then resumed and landed: not a budget stop any more.
        FakeRun.Create(runs, "20210404-000000-quality", "quality", At("2021-04-04T00:00:00Z"))
            .Started("polish").Completed("polish", "success", 100)
            .Budget(1.25, 1, "review").Event("PipelineFailed", new JsonObject { ["error"] = "Budget exceeded", ["duration"] = "00:00:01" })
            .Event("PipelineStarted", new JsonObject { ["name"] = "quality" })
            .Started("review").Completed("review", "success", 10)
            .Result("success", 4000, At("2021-04-04T00:00:04Z"))
            .Save();
    }

    [Fact]
    public async Task Metrics_failures_that_flowed_on_visit_limits_and_budget_stops_reach_summaries_and_details()
    {
        await using var server = await ServerHarness.StartAsync();
        WriteQualityRuns(RunsDir(server));

        var runs = (await server.GetJsonAsync("/studio/api/runs?name=quality"))["runs"]!.AsArray().ToDictionary(r => r!["id"]!.GetValue<string>(), r => r!);
        var dirty = runs["20210401-000000-quality"];
        Assert.Equal("""{"score":4,"qa_failed":1}""", dirty["metrics"]!.ToJsonString());
        Assert.Equal(1, dirty["failures_continued"]!.GetValue<int>());
        Assert.Equal(["docs"], dirty["failed_stages"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(1, dirty["visit_limits"]!.GetValue<int>());
        Assert.Null(dirty["budget"]);

        var redeemed = runs["20210402-000000-quality"];
        Assert.Equal(1, redeemed["failures_continued"]!.GetValue<int>());
        Assert.Empty(redeemed["failed_stages"]!.AsArray());

        var stopped = runs["20210403-000000-quality"];
        Assert.Equal("fail", stopped["status"]!.GetValue<string>());
        Assert.Equal(5.5, stopped["budget"]!["spent_usd"]!.GetValue<double>());
        Assert.Equal(5, stopped["budget"]!["limit_usd"]!.GetValue<double>());
        Assert.Equal("review", stopped["budget"]!["next_node"]!.GetValue<string>());
        Assert.NotNull(stopped["budget"]!["at"]);
        Assert.Null(runs["20210404-000000-quality"]["budget"]);
        Assert.Empty(runs["20210404-000000-quality"]["metrics"]!.AsObject());

        var detail = await server.GetJsonAsync("/studio/api/runs/20210401-000000-quality");
        var stages = detail["stages"]!.AsArray().ToDictionary(s => s!["node"]!.GetValue<string>(), s => s!);
        Assert.Equal(
            """[{"visit":1,"metrics":{"score":2,"qa_failed":5}},{"visit":2,"metrics":{"score":4,"qa_failed":1}}]""",
            stages["polish"]["metrics"]!.ToJsonString());
        Assert.Equal(1, stages["polish"]["visit_limits"]!.GetValue<int>());
        Assert.Equal(1, stages["docs"]["failures_continued"]!.GetValue<int>());
        Assert.Empty(stages["review"]["metrics"]!.AsArray());
    }

    [Fact]
    public async Task Telemetry_aggregates_metrics_dirty_landings_visit_limits_and_budget_stops()
    {
        await using var server = await ServerHarness.StartAsync();
        WriteQualityRuns(RunsDir(server));

        var t = await server.GetJsonAsync("/studio/api/telemetry?name=quality");
        Assert.Equal(4, t["runs"]!.GetValue<int>());
        Assert.Equal(1, t["failed_stage_landings"]!.GetValue<int>());
        Assert.Equal(1, t["budget_stops"]!.GetValue<int>());

        // Final values per run, oldest first: score 4, 5, 3; qa_failed 1, 0.
        Assert.Equal("""{"mean":4,"min":3,"max":5,"last":3,"runs":3}""", t["metrics"]!["score"]!.ToJsonString());
        Assert.Equal("""{"mean":0.5,"min":0,"max":1,"last":0,"runs":2}""", t["metrics"]!["qa_failed"]!.ToJsonString());

        var polish = t["nodes"]!["polish"]!;
        // Every reported visit: 2, 4 (run 1), 5 (run 2), 3 (run 3).
        Assert.Equal("""{"count":4,"mean":3.5,"min":2,"max":5,"last":3}""", polish["metrics"]!["score"]!.ToJsonString());
        Assert.Equal(1, polish["visit_limits"]!.GetValue<int>());
        Assert.Equal(0, polish["failures_continued"]!.GetValue<int>());

        var docs = t["nodes"]!["docs"]!;
        Assert.Equal(2, docs["failures_continued"]!.GetValue<int>());
        Assert.Equal(1, docs["failed_stage_landings"]!.GetValue<int>());
        Assert.Empty(docs["metrics"]!.AsObject());

        var series = t["series"]!.AsArray();
        Assert.Equal("""{"score":4,"qa_failed":1}""", series[0]!["metrics"]!.ToJsonString());
        Assert.Equal(["docs"], series[0]!["failed_stages"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(1, series[1]!["failures_continued"]!.GetValue<int>());
        Assert.Null(series[1]!["failed_stages"]);
        Assert.True(series[2]!["budget_exceeded"]!.GetValue<bool>());
        Assert.Null(series[3]!["budget_exceeded"]);
        Assert.Null(series[3]!["metrics"]);
    }

    [Fact]
    public async Task Metric_visits_continue_across_loop_restart_directories()
    {
        await using var server = await ServerHarness.StartAsync();
        var runs = RunsDir(server);
        var run = FakeRun.Create(runs, "20210501-000000-looped", "looped", At("2021-05-01T00:00:00Z"))
            .Started("score").Completed("score", "success", 10).Metrics("score", ("value", 1))
            .Save();
        var restart = Path.Combine(runs, run.Id + "-restart-1");
        Directory.CreateDirectory(restart);
        File.WriteAllText(Path.Combine(restart, "manifest.json"), """{"name":"looped","run_id":"x","started_at":"2021-05-01T00:00:01Z"}""");
        File.WriteAllText(Path.Combine(restart, "events.jsonl"), string.Join('\n',
            """{"type":"StageStarted","node_id":"score","attempt":1,"timestamp":"2021-05-01T00:00:02Z"}""",
            """{"type":"StageCompleted","node_id":"score","status":"success","duration":"00:00:00.0100000","timestamp":"2021-05-01T00:00:03Z"}""",
            """{"type":"MetricsRecorded","node_id":"score","metrics":{"value":7,"nested":{"x":1},"text":"no"},"timestamp":"2021-05-01T00:00:03Z"}""") + "\n");

        var detail = await server.GetJsonAsync($"/studio/api/runs/{run.Id}");
        Assert.Equal("""{"value":7}""", detail["summary"]!["metrics"]!.ToJsonString());
        var score = detail["stages"]!.AsArray().Single(s => s!["node"]!.GetValue<string>() == "score")!;
        Assert.Equal("""[{"visit":1,"metrics":{"value":1}},{"visit":2,"metrics":{"value":7}}]""", score["metrics"]!.ToJsonString());
    }
}

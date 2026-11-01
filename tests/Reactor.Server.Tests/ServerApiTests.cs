using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Server.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reactor-server-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

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

/// <summary>A backend that can block selected nodes until released (or cancelled) and fail on demand.</summary>
internal sealed class ControllableBackend : ICodergenBackend
{
    public HashSet<string> Block { get; } = new(StringComparer.Ordinal);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<Node, CodergenResult>? Respond { get; set; }

    public int Calls;

    public async Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Block.Contains(node.Id))
        {
            await Release.Task.WaitAsync(cancellationToken);
        }

        return Respond?.Invoke(node) ?? CodergenResult.FromText("ok:" + node.Id);
    }
}

internal sealed class ServerHarness : IAsyncDisposable
{
    private ServerHarness(WebApplication app, HttpClient client, TempDir dir)
    {
        App = app;
        Client = client;
        Dir = dir;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public TempDir Dir { get; }

    public PipelineRunManager Manager => App.Services.GetRequiredService<PipelineRunManager>();

    public static async Task<ServerHarness> StartAsync(Func<string, ReactorServerOptions>? options = null)
    {
        var dir = new TempDir();
        var serverOptions = options?.Invoke(dir.Path) ?? new ReactorServerOptions { WorkingDirectory = dir.Path };
        var app = ReactorServer.Build(serverOptions, configure: b => b.WebHost.UseTestServer());
        await app.StartAsync();
        return new ServerHarness(app, app.GetTestClient(), dir);
    }

    public async Task<JsonObject> StartRunAsync(string dot)
    {
        var response = await Client.PostAsync("/pipelines", new StringContent(dot, Encoding.UTF8, "text/vnd.graphviz"));
        var body = await ReadJsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body.ToJsonString());
        return body;
    }

    public async Task<JsonObject> WaitForStatusAsync(string id, params string[] statuses)
    {
        for (var i = 0; i < 400; i++)
        {
            var detail = await GetJsonAsync($"/pipelines/{id}");
            if (statuses.Contains(detail["status"]!.GetValue<string>()))
            {
                return detail;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Run {id} did not reach {string.Join("/", statuses)}.");
    }

    public async Task<JsonArray> WaitForQuestionsAsync(string id)
    {
        for (var i = 0; i < 400; i++)
        {
            var questions = (JsonArray)JsonNode.Parse(await Client.GetStringAsync($"/pipelines/{id}/questions"))!;
            if (questions.Count > 0)
            {
                return questions;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("No question was asked.");
    }

    public async Task<JsonObject> GetJsonAsync(string path)
    {
        var response = await Client.GetAsync(path);
        var body = await ReadJsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {body.ToJsonString()}");
        return body;
    }

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? [];

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Dir.Dispose();
    }
}

public sealed class ServerApiTests
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

    private const string Gate = """
        digraph Gate {
          start [shape=Mdiamond]
          exit [shape=Msquare]
          implement [prompt="Implement"]
          review [shape=hexagon, label="Ship it?"]
          fix [prompt="Fix"]
          start -> implement -> review
          review -> exit [label="[A] Approve"]
          review -> fix [label="[R] Revise"]
          fix -> review
        }
        """;

    [Fact]
    public async Task Submitting_a_pipeline_runs_it_and_exposes_status_checkpoint_and_context()
    {
        await using var server = await ServerHarness.StartAsync();

        var created = await server.StartRunAsync(Simple);
        var id = created["id"]!.GetValue<string>();
        Assert.Equal($"/pipelines/{id}/events", created["links"]!["events"]!.GetValue<string>());

        var detail = await server.WaitForStatusAsync(id, "success");
        Assert.Equal("Simple", detail["name"]!.GetValue<string>());
        Assert.Equal("Say hello", detail["goal"]!.GetValue<string>());
        Assert.Equal(["start", "plan", "implement", "exit"], detail["completed_nodes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("success", detail["node_statuses"]!["implement"]!.GetValue<string>());
        Assert.Equal("success", detail["result"]!["status"]!.GetValue<string>());
        Assert.NotNull(detail["duration_seconds"]);

        var checkpoint = await server.GetJsonAsync($"/pipelines/{id}/checkpoint");
        Assert.Equal("exit", checkpoint["current_node"]!.GetValue<string>());

        var context = await server.GetJsonAsync($"/pipelines/{id}/context");
        Assert.Equal("result", context["source"]!.GetValue<string>());
        Assert.Equal("Say hello", context["context"]!["graph.goal"]!.GetValue<string>());

        var list = JsonNode.Parse(await server.Client.GetStringAsync("/pipelines"))!.AsArray();
        Assert.Contains(list, r => r!["id"]!.GetValue<string>() == id);
    }

    [Fact]
    public async Task Json_submission_carries_initial_context()
    {
        await using var server = await ServerHarness.StartAsync();
        var response = await server.Client.PostAsJsonAsync("/pipelines", new { dot = Simple, context = new { ticket = "ABC-1", priority = 2 } });
        var id = (await ServerHarness.ReadJsonAsync(response))["id"]!.GetValue<string>();

        await server.WaitForStatusAsync(id, "success");
        var context = await server.GetJsonAsync($"/pipelines/{id}/context");
        Assert.Equal("ABC-1", context["context"]!["ticket"]!.GetValue<string>());
        Assert.Equal(2, context["context"]!["priority"]!.GetValue<int>());
    }

    [Fact]
    public async Task Invalid_pipelines_are_rejected_with_diagnostics()
    {
        await using var server = await ServerHarness.StartAsync();

        var parse = await server.Client.PostAsync("/pipelines", new StringContent("digraph { a -> }", Encoding.UTF8, "text/vnd.graphviz"));
        Assert.Equal(HttpStatusCode.BadRequest, parse.StatusCode);
        Assert.Contains("line", (await ServerHarness.ReadJsonAsync(parse))["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        var invalid = await server.Client.PostAsync("/pipelines", new StringContent("digraph G { a -> b }", Encoding.UTF8, "text/vnd.graphviz"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await ServerHarness.ReadJsonAsync(invalid);
        Assert.Contains(body["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "start_node" && d["severity"]!.GetValue<string>() == "error");

        var validate = await server.GetJsonAsync("/healthz");
        Assert.Equal("ok", validate["status"]!.GetValue<string>());

        var check = await server.Client.PostAsync("/validate", new StringContent(Simple, Encoding.UTF8, "text/plain"));
        var result = await ServerHarness.ReadJsonAsync(check);
        Assert.True(result["valid"]!.GetValue<bool>());
        Assert.Equal(4, result["nodes"]!.GetValue<int>());

        var missing = await server.Client.PostAsJsonAsync("/pipelines", new { name = "no dot" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Run_status_and_answer_aliases_work()
    {
        await using var server = await ServerHarness.StartAsync();
        var response = await server.Client.PostAsync("/run", new StringContent(Gate, Encoding.UTF8, "text/vnd.graphviz"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ServerHarness.ReadJsonAsync(response))["id"]!.GetValue<string>();

        await server.WaitForQuestionsAsync(id);
        var status = await server.GetJsonAsync($"/status/{id}");
        Assert.Equal("running", status["status"]!.GetValue<string>());
        Assert.Equal("review", status["pending_questions"]![0]!["stage"]!.GetValue<string>());
        Assert.Equal(id, (await server.GetJsonAsync($"/status?id={id}"))["id"]!.GetValue<string>());

        var answer = await server.Client.PostAsJsonAsync("/answer", new { pipeline_id = id, value = "A" });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        var detail = await server.WaitForStatusAsync(id, "success");
        Assert.DoesNotContain(detail["completed_nodes"]!.AsArray(), n => n!.GetValue<string>() == "fix");
    }

    [Fact]
    public async Task Human_gates_are_answered_through_the_questions_api()
    {
        await using var server = await ServerHarness.StartAsync();
        var id = (await server.StartRunAsync(Gate))["id"]!.GetValue<string>();

        var question = (await server.WaitForQuestionsAsync(id))[0]!;
        Assert.Equal("multiple_choice", question["type"]!.GetValue<string>());
        Assert.Equal("Ship it?", question["text"]!.GetValue<string>());
        Assert.Equal(["A", "R"], question["options"]!.AsArray().Select(o => o!["key"]!.GetValue<string>()));
        var qid = question["id"]!.GetValue<string>();

        var bad = await server.Client.PostAsJsonAsync($"/pipelines/{id}/questions/{qid}/answer", new { value = "Z" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Choose one of", (await ServerHarness.ReadJsonAsync(bad))["error"]!.GetValue<string>());

        var unknown = await server.Client.PostAsJsonAsync($"/pipelines/{id}/questions/nope/answer", new { value = "A" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Revise once (by label), then approve the second review.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.PostAsJsonAsync($"/pipelines/{id}/questions/{qid}/answer", new { value = "Revise" })).StatusCode);
        var second = (await WaitForNewQuestionAsync(server, id, qid))["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await server.Client.PostAsJsonAsync($"/pipelines/{id}/questions/{second}/answer", new { value = "a" })).StatusCode);

        var detail = await server.WaitForStatusAsync(id, "success");
        Assert.Contains(detail["completed_nodes"]!.AsArray(), n => n!.GetValue<string>() == "fix");
        Assert.Empty(JsonNode.Parse(await server.Client.GetStringAsync($"/pipelines/{id}/questions"))!.AsArray());
    }

    [Fact]
    public async Task Event_stream_replays_history_and_supports_last_event_id()
    {
        await using var server = await ServerHarness.StartAsync();
        var id = (await server.StartRunAsync(Simple))["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(id, "success");

        var frames = ParseSse(await server.Client.GetStringAsync($"/pipelines/{id}/events"));
        Assert.Equal("PipelineStarted", frames[0].Event);
        Assert.Equal("end", frames[^1].Event);
        Assert.Contains(frames, f => f.Event == "StageStarted" && f.Data["node_id"]!.GetValue<string>() == "plan");
        Assert.Contains(frames, f => f.Event == "PipelineCompleted");
        var ids = frames.Where(f => f.Id is not null).Select(f => long.Parse(f.Id!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(ids.OrderBy(i => i), ids);
        Assert.Equal(Enumerable.Range(1, ids.Count).Select(i => (long)i), ids);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/pipelines/{id}/events");
        request.Headers.Add("Last-Event-ID", (ids.Count - 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var tail = ParseSse(await (await server.Client.SendAsync(request)).Content.ReadAsStringAsync());
        Assert.Equal(3, tail.Count);
        Assert.Equal("end", tail[^1].Event);
    }

    [Fact]
    public async Task Live_event_stream_delivers_questions_and_completion()
    {
        await using var server = await ServerHarness.StartAsync();
        var id = (await server.StartRunAsync(Gate))["id"]!.GetValue<string>();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/pipelines/{id}/events");
        using var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        var seen = new List<string>();
        string? line;
        var answered = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null)
        {
            if (!line.StartsWith("event: ", StringComparison.Ordinal))
            {
                continue;
            }

            var type = line["event: ".Length..];
            seen.Add(type);
            if (type == "InterviewStarted" && !answered)
            {
                answered = true;
                var question = (await server.WaitForQuestionsAsync(id))[0]!;
                await server.Client.PostAsJsonAsync($"/pipelines/{id}/questions/{question["id"]!.GetValue<string>()}/answer", new { value = "A" });
            }

            if (type == "end")
            {
                break;
            }
        }

        Assert.True(answered);
        Assert.Contains("InterviewCompleted", seen);
        Assert.Equal("end", seen[^1]);
    }

    [Fact]
    public async Task Cancel_stops_a_running_pipeline()
    {
        var backend = new ControllableBackend { Block = { "implement" } };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });
        var id = (await server.StartRunAsync(Simple))["id"]!.GetValue<string>();
        await WaitUntilAsync(async () => (await server.GetJsonAsync($"/pipelines/{id}"))["current_node"]?.GetValue<string>() == "implement");

        var cancel = await server.Client.PostAsync($"/pipelines/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        await server.WaitForStatusAsync(id, "cancelled");
        Assert.Equal(HttpStatusCode.Conflict, (await server.Client.PostAsync($"/pipelines/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.PostAsync("/pipelines/missing/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Runs_beyond_the_concurrency_limit_queue()
    {
        var backend = new ControllableBackend { Block = { "implement" } };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend, MaxConcurrentRuns = 1 });
        var first = (await server.StartRunAsync(Simple))["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(first, "running");
        var second = (await server.StartRunAsync(Simple))["id"]!.GetValue<string>();

        await Task.Delay(200);
        Assert.Equal("queued", (await server.GetJsonAsync($"/pipelines/{second}"))["status"]!.GetValue<string>());

        backend.Release.SetResult();
        await server.WaitForStatusAsync(first, "success");
        await server.WaitForStatusAsync(second, "success");
    }

    [Fact]
    public async Task Failed_runs_can_be_resumed_from_their_checkpoint()
    {
        var failing = true;
        var backend = new ControllableBackend
        {
            Respond = node => node.Id == "implement" && failing ? CodergenResult.FromOutcome(Outcome.Fail("compile error")) : CodergenResult.FromText("ok"),
        };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });
        var id = (await server.StartRunAsync("""
            digraph Resumable {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan"]
              implement [prompt="Implement"]
              start -> plan -> implement
              implement -> exit [condition="outcome=success"]
            }
            """))["id"]!.GetValue<string>();

        var failed = await server.WaitForStatusAsync(id, "fail", "error");
        Assert.Equal("fail", failed["status"]!.GetValue<string>());

        failing = false;
        var resume = await server.Client.PostAsync($"/pipelines/{id}/resume", null);
        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode);
        var detail = await server.WaitForStatusAsync(id, "success");
        Assert.Equal("success", detail["node_statuses"]!["implement"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await server.Client.PostAsync($"/pipelines/{id}/resume", null)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_resume_requests_start_exactly_one_execution()
    {
        var backend = new ControllableBackend
        {
            Respond = node => node.Id == "implement" ? CodergenResult.FromOutcome(Outcome.Fail("compile error")) : CodergenResult.FromText("ok"),
        };
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = backend });
        var id = (await server.StartRunAsync("""
            digraph Resumable {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan"]
              implement [prompt="Implement"]
              start -> plan -> implement
              implement -> exit [condition="outcome=success"]
            }
            """))["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(id, "fail", "error");

        // Hold the resumed execution at `implement` so every request sees the same state.
        backend.Block.Add("implement");
        backend.Respond = _ => CodergenResult.FromText("ok");
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => server.Client.PostAsync($"/pipelines/{id}/resume", null)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        backend.Release.SetResult();
        await server.WaitForStatusAsync(id, "success");
    }

    [Fact]
    public async Task Graph_is_rendered_with_stage_status_colors()
    {
        await using var server = await ServerHarness.StartAsync();
        var id = (await server.StartRunAsync(Simple))["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(id, "success");

        var dot = await server.Client.GetStringAsync($"/pipelines/{id}/graph?format=dot");
        Assert.Contains("fillcolor=\"#c8e6c9\"", dot);
        Assert.DoesNotContain("prompt", dot);

        var svg = await server.Client.GetAsync($"/pipelines/{id}/graph");
        if (svg.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Assert.Contains("Graphviz", (await ServerHarness.ReadJsonAsync(svg))["error"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
            Assert.Contains("<svg", await svg.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync($"/pipelines/{id}/graph?format=exe")).StatusCode);
    }

    [Fact]
    public async Task Stage_artifacts_are_listed_and_readable_without_path_traversal()
    {
        await using var server = await ServerHarness.StartAsync();
        var id = (await server.StartRunAsync(Simple))["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(id, "success");

        var stage = await server.GetJsonAsync($"/pipelines/{id}/stages/plan");
        Assert.Contains(stage["files"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "prompt.md");
        Assert.Equal("success", stage["outcome"]!["outcome"]!.GetValue<string>());

        var prompt = await server.Client.GetStringAsync($"/pipelines/{id}/stages/plan/files/prompt.md");
        Assert.Contains("Plan: Say hello", prompt);

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/pipelines/{id}/stages/plan/files/..%2Fcheckpoint.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/pipelines/{id}/stages/nope")).StatusCode);
    }

    [Fact]
    public async Task Bearer_token_protects_the_api_but_not_health_or_console()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, AuthToken = "s3cret" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync("/pipelines")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/healthz")).StatusCode);
        var console = await server.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, console.StatusCode);
        Assert.Contains("Reactor", await console.Content.ReadAsStringAsync());

        server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync("/pipelines")).StatusCode);

        server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");
        var id = (await server.StartRunAsync(Simple))["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(id, "success");

        server.Client.DefaultRequestHeaders.Authorization = null;
        var events = await server.Client.GetAsync($"/pipelines/{id}/events?access_token=s3cret");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_cross_origin_writes_are_refused()
    {
        await using var server = await ServerHarness.StartAsync();

        async Task<HttpStatusCode> PostAsync(string? origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/pipelines") { Content = new StringContent(Simple, Encoding.UTF8, "text/plain") };
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            return (await server.Client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("http://localhost:3000"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("null"));
        Assert.Empty(server.Manager.Runs);

        Assert.Equal(HttpStatusCode.Created, await PostAsync("http://localhost"));
        Assert.Equal(HttpStatusCode.Created, await PostAsync(null));
    }

    [Fact]
    public async Task Without_a_token_only_local_or_ip_host_names_are_served()
    {
        await using var server = await ServerHarness.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("http://attacker.example:5180/pipelines")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("http://attacker.example:5180/")).StatusCode);
        foreach (var host in new[] { "localhost:5180", "studio.localhost:5180", "127.0.0.1:5180", "[::1]:5180", "192.168.1.20:5180" })
        {
            Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync($"http://{host}/pipelines")).StatusCode);
        }
    }

    [Fact]
    public async Task With_a_token_any_host_and_origin_may_call_the_api()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, AuthToken = "s3cret" });
        server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://reactor.example/pipelines") { Content = new StringContent(Simple, Encoding.UTF8, "text/vnd.graphviz") };
        request.Headers.Add("Origin", "https://reactor.example");
        Assert.Equal(HttpStatusCode.Created, (await server.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Client_working_directories_require_opt_in()
    {
        await using var server = await ServerHarness.StartAsync();
        var response = await server.Client.PostAsJsonAsync("/pipelines", new { dot = Simple, working_directory = Path.GetTempPath() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("working directory", (await ServerHarness.ReadJsonAsync(response))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_runs_return_404()
    {
        await using var server = await ServerHarness.StartAsync();
        foreach (var path in new[] { "/pipelines/x", "/pipelines/x/questions", "/pipelines/x/checkpoint", "/pipelines/x/context", "/pipelines/x/graph", "/pipelines/x/events", "/status/x" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public void Answers_are_interpreted_per_question_type()
    {
        var yesNo = new Human.Question { Text = "OK?", Type = Human.QuestionType.YesNo };
        Assert.True(WebInterviewer.Interpret(yesNo, "y", null, out _)!.IsYes);
        Assert.Equal(Human.AnswerValue.No, WebInterviewer.Interpret(yesNo, "false", null, out _)!.Special);
        Assert.Null(WebInterviewer.Interpret(yesNo, "maybe", null, out var error));
        Assert.Contains("yes or no", error);

        var freeform = new Human.Question { Text = "Why?", Type = Human.QuestionType.Freeform };
        Assert.Equal("because", WebInterviewer.Interpret(freeform, null, "because", out _)!.Text);
        Assert.Null(WebInterviewer.Interpret(freeform, null, "  ", out _));

        var choice = new Human.Question { Text = "Pick", Options = [new Human.Option("A", "[A] Approve"), new Human.Option("R", "[R] Revise")] };
        Assert.Equal("R", WebInterviewer.Interpret(choice, "revise", null, out _)!.SelectedOption!.Key);
        Assert.Equal("A", WebInterviewer.Interpret(choice, "a", null, out _)!.SelectedOption!.Key);
        Assert.True(WebInterviewer.Interpret(choice, "skip", null, out _)!.IsSkipped);
    }

    private static async Task<JsonNode> WaitForNewQuestionAsync(ServerHarness server, string id, string previous)
    {
        for (var i = 0; i < 400; i++)
        {
            var questions = JsonNode.Parse(await server.Client.GetStringAsync($"/pipelines/{id}/questions"))!.AsArray();
            if (questions.FirstOrDefault(q => q!["id"]!.GetValue<string>() != previous) is { } fresh)
            {
                return fresh;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("No new question.");
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

    private static List<(string? Id, string Event, JsonObject Data)> ParseSse(string text)
    {
        var frames = new List<(string?, string, JsonObject)>();
        foreach (var block in text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? id = null, type = null, data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line[4..];
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    type = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
            }

            if (type is not null && data is not null)
            {
                frames.Add((id, type, (JsonObject)JsonNode.Parse(data)!));
            }
        }

        return frames;
    }
}

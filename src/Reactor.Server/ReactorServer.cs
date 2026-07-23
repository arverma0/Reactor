using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;
using Reactor.Rendering;
using Reactor.Server.Studio;
using Reactor.Validation;

namespace Reactor.Server;

/// <summary>
/// HTTP server mode (spec 9.5): a REST API, server-sent events, and an embedded web console for running pipelines and
/// answering human gates.
/// </summary>
public static class ReactorServer
{
    private static readonly JsonSerializerOptions EventJson = new() { WriteIndented = false };

    /// <summary>Builds a ready-to-run web application hosting the Reactor API.</summary>
    public static WebApplication Build(ReactorServerOptions options, string[]? args = null, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(args ?? []);

        // Request logs are noisy and would record ?access_token= query strings; keep startup/lifetime messages only.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddReactorServer(options);
        configure?.Invoke(builder);
        var app = builder.Build();
        app.MapReactorServer();
        return app;
    }

    /// <summary>Registers the run manager and JSON conventions.</summary>
    public static IServiceCollection AddReactorServer(this IServiceCollection services, ReactorServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<PipelineRunManager>();
        services.AddSingleton(new Studio.StudioWorkspace(options.WorkingDirectory, options.MaxDotBytes));
        services.AddSingleton<Studio.StudioFeatures>();
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        return services;
    }

    /// <summary>Maps the API, aliases, and web console onto <paramref name="app"/>.</summary>
    public static WebApplication MapReactorServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<ReactorServerOptions>();
        app.Use(async (http, next) =>
        {
            if (RequiresAuth(http.Request.Path) && !Authorized(http, options.AuthToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                http.Response.Headers.WWWAuthenticate = "Bearer";
                await http.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            await next(http).ConfigureAwait(false);
        });

        app.MapStudioAssets();
        app.MapGet("/console", () => Asset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapStudioApi();

        app.MapPost("/pipelines", StartAsync);
        app.MapPost("/run", StartAsync);
        app.MapPost("/validate", ValidateAsync);
        app.MapGet("/pipelines", (PipelineRunManager manager) => Results.Ok(manager.Runs.Select(Summary)));
        app.MapGet("/pipelines/{id}", (string id, PipelineRunManager manager) => WithRun(manager, id, run => Results.Ok(Detail(run))));
        app.MapGet("/status", (string? id, PipelineRunManager manager) =>
            id is null ? Results.Ok(manager.Runs.Select(Summary)) : WithRun(manager, id, run => Results.Ok(Detail(run))));
        app.MapGet("/status/{id}", (string id, PipelineRunManager manager) => WithRun(manager, id, run => Results.Ok(Detail(run))));
        app.MapGet("/pipelines/{id}/events", StreamEventsAsync);
        app.MapPost("/pipelines/{id}/cancel", (string id, PipelineRunManager manager) =>
            manager.Get(id) is null ? NotFound(id)
            : manager.Cancel(id) ? Results.Accepted($"/pipelines/{id}", new { id, status = "cancelling" })
            : Results.Conflict(new { error = "The run has already finished." }));
        app.MapPost("/pipelines/{id}/resume", (string id, PipelineRunManager manager) =>
        {
            var result = manager.Resume(id);
            return result.Succeeded ? Results.Accepted($"/pipelines/{id}", Detail(result.Run!)) : manager.Get(id) is null ? NotFound(id) : Results.Conflict(new { error = result.Error });
        });
        app.MapGet("/pipelines/{id}/graph", RenderGraphAsync);
        app.MapGet("/pipelines/{id}/questions", (string id, PipelineRunManager manager) =>
            WithRun(manager, id, run => Results.Ok(run.Interviewer.Pending.Select(QuestionDto.From))));
        app.MapPost("/pipelines/{id}/questions/{qid}/answer", (string id, string qid, AnswerRequest? body, PipelineRunManager manager) =>
            WithRun(manager, id, run => SubmitAnswer(run, qid, body)));
        app.MapPost("/answer", (AnswerRequest? body, PipelineRunManager manager) =>
        {
            if (body?.PipelineId is not { Length: > 0 } pipelineId)
            {
                return Results.BadRequest(new { error = "pipeline_id is required." });
            }

            return WithRun(manager, pipelineId, run =>
            {
                var questionId = body.QuestionId;
                if (string.IsNullOrEmpty(questionId))
                {
                    var pending = run.Interviewer.Pending;
                    if (pending.Count != 1)
                    {
                        return Results.BadRequest(new { error = $"question_id is required ({pending.Count} questions are pending)." });
                    }

                    questionId = pending[0].Question.Id;
                }

                return SubmitAnswer(run, questionId, body);
            });
        });
        app.MapGet("/pipelines/{id}/checkpoint", (string id, PipelineRunManager manager) => WithRun(manager, id, run =>
        {
            var path = Path.Combine(run.CurrentLogsRoot, "checkpoint.json");
            return File.Exists(path)
                ? Results.Text(ReadShared(path), "application/json")
                : Results.NotFound(new { error = "No checkpoint has been saved yet." });
        }));
        app.MapGet("/pipelines/{id}/context", (string id, PipelineRunManager manager) => WithRun(manager, id, ContextOf));
        app.MapGet("/pipelines/{id}/stages/{node}", (string id, string node, PipelineRunManager manager) => WithRun(manager, id, run => StageFiles(run, node)));
        app.MapGet("/pipelines/{id}/stages/{node}/files/{name}", (string id, string node, string name, PipelineRunManager manager) =>
            WithRun(manager, id, run => StageFile(run, node, name)));
        return app;
    }

    private static async Task<IResult> StartAsync(HttpContext http, PipelineRunManager manager)
    {
        var (request, error) = await ReadStartRequestAsync(http, manager.Options).ConfigureAwait(false);
        if (request is null)
        {
            return Results.BadRequest(new { error });
        }

        var result = manager.Start(request);
        if (!result.Succeeded)
        {
            return Results.BadRequest(new { error = result.Error, diagnostics = result.Diagnostics.Select(DiagnosticDto.From) });
        }

        var run = result.Run!;
        return Results.Created($"/pipelines/{run.Id}", new
        {
            id = run.Id,
            status = run.Status,
            name = run.Name,
            logs_root = run.LogsRoot,
            diagnostics = result.Diagnostics.Select(DiagnosticDto.From),
            links = Links(run),
        });
    }

    private static async Task<IResult> ValidateAsync(HttpContext http, PipelineRunManager manager)
    {
        var (request, error) = await ReadStartRequestAsync(http, manager.Options).ConfigureAwait(false);
        if (request is null)
        {
            return Results.BadRequest(new { error });
        }

        try
        {
            var prepared = manager.Runner.Prepare(DotParser.Parse(request.Dot));
            return Results.Ok(new
            {
                valid = !prepared.HasErrors,
                name = prepared.Graph.Name,
                goal = prepared.Graph.Goal,
                nodes = prepared.Graph.Nodes.Count,
                edges = prepared.Graph.Edges.Count,
                diagnostics = prepared.Diagnostics.Select(DiagnosticDto.From),
            });
        }
        catch (DotParseException ex)
        {
            return Results.Ok(new { valid = false, error = ex.Message, line = ex.Line, column = ex.Column });
        }
    }

    private static async Task StreamEventsAsync(string id, HttpContext http, PipelineRunManager manager)
    {
        var run = manager.Get(id);
        if (run is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = $"Unknown run '{id}'." }).ConfigureAwait(false);
            return;
        }

        var after = 0L;
        if (long.TryParse(http.Request.Headers["Last-Event-ID"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var lastId) ||
            long.TryParse(http.Request.Query["since"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out lastId))
        {
            after = lastId;
        }

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var aborted = http.RequestAborted;
        await http.Response.WriteAsync("retry: 3000\n\n", aborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(aborted).ConfigureAwait(false);

        var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token).ConfigureAwait(false);
                    await writeLock.WaitAsync(heartbeatCts.Token).ConfigureAwait(false);
                    try
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", heartbeatCts.Token).ConfigureAwait(false);
                        await http.Response.Body.FlushAsync(heartbeatCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var evt in run.ReadEventsAsync(after, aborted).ConfigureAwait(false))
            {
                var payload = evt.Event.ToJson();
                payload["seq"] = evt.Sequence;
                var frame = $"id: {evt.Sequence}\nevent: {evt.Event.Type}\ndata: {payload.ToJsonString(EventJson)}\n\n";
                await writeLock.WaitAsync(aborted).ConfigureAwait(false);
                try
                {
                    await http.Response.WriteAsync(frame, aborted).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(aborted).ConfigureAwait(false);
                }
                finally
                {
                    writeLock.Release();
                }
            }

            await writeLock.WaitAsync(aborted).ConfigureAwait(false);
            try
            {
                var end = new JsonObject { ["id"] = run.Id, ["status"] = run.Status };
                await http.Response.WriteAsync($"event: end\ndata: {end.ToJsonString(EventJson)}\n\n", aborted).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(aborted).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
        }
        finally
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    private static async Task<IResult> RenderGraphAsync(string id, string? format, PipelineRunManager manager, CancellationToken cancellationToken)
    {
        var run = manager.Get(id);
        if (run is null)
        {
            return NotFound(id);
        }

        var dot = GraphRenderer.ToDisplayDot(run.Graph, run.NodeStatuses, run.CurrentNode);
        format = string.IsNullOrWhiteSpace(format) ? "svg" : format.ToLowerInvariant();
        if (format == "dot")
        {
            return Results.Text(dot, "text/vnd.graphviz; charset=utf-8");
        }

        if (format is not ("svg" or "png"))
        {
            return Results.BadRequest(new { error = "format must be svg, png, or dot." });
        }

        try
        {
            var bytes = await GraphRenderer.RenderAsync(dot, format, manager.Options.GraphvizPath, cancellationToken).ConfigureAwait(false);
            return Results.Bytes(bytes, format == "svg" ? "image/svg+xml" : "image/png");
        }
        catch (GraphvizException ex)
        {
            return Results.Json(new { error = ex.Message, dot }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult SubmitAnswer(ManagedRun run, string questionId, AnswerRequest? body)
    {
        var submission = run.Interviewer.Submit(questionId, body?.Value, body?.Text, out var error);
        return submission switch
        {
            AnswerSubmission.Accepted => Results.Ok(new { status = "accepted", question_id = questionId }),
            AnswerSubmission.NotFound => Results.NotFound(new { error }),
            _ => Results.BadRequest(new { error }),
        };
    }

    private static IResult ContextOf(ManagedRun run)
    {
        if (run.Result is { } result && RunStatus.IsTerminal(run.Status))
        {
            return Results.Ok(new { source = "result", context = ContextValues.ToJson(ContextValues.Normalize(result.Context)) });
        }

        var path = Path.Combine(run.CurrentLogsRoot, "checkpoint.json");
        if (File.Exists(path))
        {
            var checkpoint = JsonNode.Parse(ReadShared(path));
            return Results.Ok(new { source = "checkpoint", context = checkpoint?["context"]?.DeepClone() ?? new JsonObject() });
        }

        return Results.Ok(new { source = "initial", context = ContextValues.ToJson(ContextValues.Normalize(run.InitialContext ?? new Dictionary<string, object?>())) });
    }

    private static IResult StageFiles(ManagedRun run, string node)
    {
        if (!run.Graph.Nodes.ContainsKey(node))
        {
            return Results.NotFound(new { error = $"Unknown node '{node}'." });
        }

        var dir = Path.Combine(run.CurrentLogsRoot, node);
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTimeUtc }).ToList<object>()
            : [];
        var status = Path.Combine(dir, "status.json");
        return Results.Ok(new
        {
            node,
            status = run.NodeStatuses.GetValueOrDefault(node),
            outcome = File.Exists(status) ? JsonNode.Parse(ReadShared(status)) : null,
            files,
        });
    }

    private static IResult StageFile(ManagedRun run, string node, string name)
    {
        if (!run.Graph.Nodes.ContainsKey(node) || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or ".." || name.Contains("..", StringComparison.Ordinal))
        {
            return Results.NotFound(new { error = "Not found." });
        }

        var path = Path.Combine(run.CurrentLogsRoot, node, name);
        if (!File.Exists(path))
        {
            return Results.NotFound(new { error = "Not found." });
        }

        var contentType = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".jsonl" => "application/x-ndjson",
            ".md" => "text/markdown; charset=utf-8",
            _ => "text/plain; charset=utf-8",
        };
        return Results.Text(ReadShared(path), contentType);
    }

    private static async Task<(StartRequest? Request, string? Error)> ReadStartRequestAsync(HttpContext http, ReactorServerOptions options)
    {
        if (http.Request.ContentLength is { } length && length > options.MaxDotBytes)
        {
            return (null, $"The request body exceeds {options.MaxDotBytes} bytes.");
        }

        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
        {
            var content = new StringBuilder();
            var buffer = new char[16 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                content.Append(buffer, 0, read);
                if (content.Length > options.MaxDotBytes)
                {
                    return (null, $"The request body exceeds {options.MaxDotBytes} bytes.");
                }
            }

            body = content.ToString();
        }

        var contentType = http.Request.ContentType ?? "";
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(body) ? (null, "The request body must contain DOT source.") : (new StartRequest { Dot = body }, null);
        }

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid JSON: {ex.Message}");
        }

        var dot = json?["dot"] is JsonValue dotValue && dotValue.TryGetValue<string>(out var text) ? text : null;
        if (string.IsNullOrWhiteSpace(dot))
        {
            return (null, "The 'dot' field (DOT source) is required.");
        }

        IReadOnlyDictionary<string, object?>? context = null;
        if (json!["context"] is JsonObject contextObject && ContextValues.FromJson(contextObject) is IReadOnlyDictionary<string, object?> values)
        {
            context = values;
        }

        var workingDirectory = json["working_directory"] is JsonValue wd && wd.TryGetValue<string>(out var dir) ? dir : null;
        return (new StartRequest
        {
            Dot = dot,
            Context = context,
            WorkingDirectory = workingDirectory,
            Simulation = json["simulation"] as JsonObject,
            Studio = json["studio"] as JsonObject,
        }, null);
    }

    private static IResult WithRun(PipelineRunManager manager, string id, Func<ManagedRun, IResult> action) =>
        manager.Get(id) is { } run ? action(run) : NotFound(id);

    private static IResult NotFound(string id) => Results.NotFound(new { error = $"Unknown run '{id}'." });

    private static object Summary(ManagedRun run) => new
    {
        id = run.Id,
        name = run.Name,
        goal = run.Goal,
        status = run.Status,
        current_node = run.CurrentNode,
        created_at = run.CreatedAt,
        started_at = run.StartedAt,
        finished_at = run.FinishedAt,
        pending_questions = run.Interviewer.Pending.Count,
    };

    private static object Detail(ManagedRun run)
    {
        var duration = run.StartedAt is { } started ? ((run.FinishedAt ?? DateTimeOffset.UtcNow) - started).TotalSeconds : (double?)null;
        return new
        {
            id = run.Id,
            name = run.Name,
            goal = run.Goal,
            status = run.Status,
            current_node = run.CurrentNode,
            completed_nodes = run.CompletedNodes,
            node_statuses = run.NodeStatuses,
            created_at = run.CreatedAt,
            started_at = run.StartedAt,
            finished_at = run.FinishedAt,
            duration_seconds = duration,
            logs_root = run.CurrentLogsRoot,
            working_directory = run.WorkingDirectory,
            pending_questions = run.Interviewer.Pending.Select(QuestionDto.From),
            event_count = run.EventCount,
            error = run.Error,
            result = run.Result is { } result
                ? new
                {
                    status = result.Status.ToWire(),
                    cancelled = result.Cancelled,
                    notes = result.FinalOutcome.Notes,
                    failure_reason = result.FinalOutcome.FailureReason,
                    loop_restarts = result.LoopRestarts,
                    completed_nodes = result.CompletedNodes,
                }
                : null,
            links = Links(run),
        };
    }

    private static object Links(ManagedRun run) => new
    {
        self = $"/pipelines/{run.Id}",
        events = $"/pipelines/{run.Id}/events",
        graph = $"/pipelines/{run.Id}/graph",
        questions = $"/pipelines/{run.Id}/questions",
        checkpoint = $"/pipelines/{run.Id}/checkpoint",
        context = $"/pipelines/{run.Id}/context",
        cancel = $"/pipelines/{run.Id}/cancel",
    };

    private static bool RequiresAuth(PathString path) =>
        !(path == "/console" || path == "/healthz" || StudioAssets.IsPublicPath(path));

    private static bool Authorized(HttpContext http, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return true;
        }

        var header = http.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : http.Request.Query["access_token"].ToString();
        var expected = Encoding.UTF8.GetBytes(token);
        var actual = Encoding.UTF8.GetBytes(supplied);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static IResult Asset(string name, string contentType)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Reactor.Server.wwwroot." + name);
        if (stream is null)
        {
            return Results.NotFound();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Results.Text(reader.ReadToEnd(), contentType);
    }
}

/// <summary>Body of answer endpoints.</summary>
public sealed record AnswerRequest
{
    /// <summary>Option key/label, yes/no, or <c>skip</c>.</summary>
    public string? Value { get; init; }

    /// <summary>Free-text answer (freeform questions).</summary>
    public string? Text { get; init; }

    /// <summary>For <c>POST /answer</c>: the run id.</summary>
    public string? PipelineId { get; init; }

    /// <summary>For <c>POST /answer</c>: the question id (optional when exactly one question is pending).</summary>
    public string? QuestionId { get; init; }
}

/// <summary>A pending question as returned by the API.</summary>
public sealed record QuestionDto(
    string Id,
    string Text,
    string Type,
    IReadOnlyList<OptionDto> Options,
    string Stage,
    double? TimeoutSeconds,
    string? Default,
    DateTimeOffset AskedAt)
{
    public static QuestionDto From(PendingQuestion pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var question = pending.Question;
        return new QuestionDto(
            question.Id,
            question.Text,
            question.Type switch
            {
                QuestionType.YesNo => "yes_no",
                QuestionType.Confirmation => "confirmation",
                QuestionType.Freeform => "freeform",
                _ => "multiple_choice",
            },
            question.Options.Select(o => new OptionDto(o.Key, o.Label)).ToList(),
            question.Stage,
            question.TimeoutSeconds,
            question.Default?.Value,
            pending.AskedAt);
    }
}

public sealed record OptionDto(string Key, string Label);

/// <summary>A validation diagnostic as returned by the API.</summary>
public sealed record DiagnosticDto(string Rule, string Severity, string Message, string? NodeId, string? Edge, string? Fix)
{
    public static DiagnosticDto From(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new DiagnosticDto(
            diagnostic.Rule,
            diagnostic.Severity.ToString().ToLowerInvariant(),
            diagnostic.Message,
            diagnostic.NodeId,
            diagnostic.Edge is { } edge ? $"{edge.From} -> {edge.To}" : null,
            diagnostic.Fix);
    }
}

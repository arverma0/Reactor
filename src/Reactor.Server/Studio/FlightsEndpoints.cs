using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Server.Studio;

/// <summary>Body of <c>POST /studio/api/runs/{id}/fork</c>.</summary>
public sealed record ForkRequest
{
    /// <summary>The stage snapshot to continue from, e.g. <c>0004-plan</c> (see the run's <c>checkpoints</c>).</summary>
    public string? Checkpoint { get; init; }

    /// <summary>The pipeline to run instead of the source run's <c>graph.dot</c> (e.g. with another model for a stage).</summary>
    public string? Dot { get; init; }

    /// <summary>Context values to set before continuing.</summary>
    public JsonObject? Context { get; init; }

    /// <summary>Working directory for the fork (only with <see cref="ReactorServerOptions.AllowClientWorkingDirectory"/>).</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>Body of <c>POST /studio/api/pipelines/{id}/steer</c>.</summary>
public sealed record SteerRequest
{
    /// <summary>The steering message for the running agent.</summary>
    public string? Message { get; init; }

    /// <summary>The stage to steer (default: the stage that is running).</summary>
    public string? Node { get; init; }
}

/// <summary>
/// The Studio "flights" API: the run history (disk-backed, including CLI runs and runs of earlier server processes),
/// event replay, stage files, telemetry aggregated across runs, forking a run from a stage snapshot, and steering a
/// running stage.
/// </summary>
public static partial class FlightsEndpoints
{
    /// <summary>Events returned per page by default.</summary>
    public const int DefaultEventLimit = 20_000;

    /// <summary>Largest page of events.</summary>
    public const int MaxEventLimit = 50_000;

    /// <summary>Largest steering message.</summary>
    public const int MaxSteeringBytes = 16 * 1024;

    /// <summary>Stage types that never consume steering (only agent stages do).</summary>
    private static readonly HashSet<string> Unsteerable = new(StringComparer.Ordinal)
    {
        HandlerTypes.Start, HandlerTypes.Exit, HandlerTypes.WaitHuman, HandlerTypes.Conditional, HandlerTypes.Parallel,
        HandlerTypes.FanIn, HandlerTypes.Tool, HandlerTypes.ManagerLoop,
    };

    /// <summary>Maps the flights endpoints under <c>/studio/api</c> and advertises their features.</summary>
    public static WebApplication MapFlightsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Services.GetRequiredService<StudioFeatures>().Add("history", "telemetry", "fork", "steering");
        var api = app.MapGroup("/studio/api");
        api.MapGet("/runs", ListRuns);
        api.MapGet("/runs/{id}", RunDetail);
        api.MapGet("/runs/{id}/events", RunEvents);
        api.MapGet("/runs/{id}/stages/{node}/files/{name}", StageFile);
        api.MapPost("/runs/{id}/fork", Fork);
        api.MapGet("/telemetry", Telemetry);
        api.MapPost("/pipelines/{id}/steer", SteerAsync);
        return app;
    }

    private static IResult ListRuns(string? name, int? limit, RunHistory history)
    {
        var max = Math.Clamp(limit ?? 100, 1, 5000);
        var runs = history.LoadAll()
            .Where(r => string.IsNullOrEmpty(name) || string.Equals(r.Name, name, StringComparison.Ordinal))
            .Take(max)
            .Select(r => (JsonNode?)Summary(r))
            .ToArray();
        return Results.Ok(new JsonObject { ["runs"] = new JsonArray(runs) });
    }

    private static IResult RunDetail(string id, RunHistory history)
    {
        if (history.Load(id) is not { OnDisk: true } record)
        {
            return UnknownRun(id);
        }

        var first = record.Segments[0].Path;
        var last = record.Segments[^1].Path;
        var graph = Path.Combine(first, RunHistory.GraphFile);
        return Results.Ok(new JsonObject
        {
            ["summary"] = Summary(record),
            ["manifest"] = RunHistory.ReadJsonObject(Path.Combine(first, RunHistory.ManifestFile)),
            ["result"] = RunHistory.ReadJsonObject(Path.Combine(last, RunHistory.ResultFile)),
            ["checkpoint"] = CheckpointSummary(RunHistory.ReadJsonObject(Path.Combine(last, Checkpoint.FileName))),
            ["graph_dot"] = File.Exists(graph) ? ReadTextOrNull(graph) : null,
            ["studio"] = RunHistory.ReadJsonObject(Path.Combine(first, PipelineRunManager.StudioFileName)),
            ["stages"] = Stages(record),
            ["checkpoints"] = new JsonArray(Snapshots(record).Select(s => (JsonNode?)s.Name).ToArray()),
        });
    }

    private static IResult RunEvents(string id, long? since, int? limit, RunHistory history)
    {
        if (history.Load(id) is not { OnDisk: true } record)
        {
            return UnknownRun(id);
        }

        var page = history.ReadEvents(record, Math.Max(0, since ?? 0), Math.Clamp(limit ?? DefaultEventLimit, 1, MaxEventLimit));
        return Results.Ok(new { events = page.Events, next = page.Next, done = page.Done });
    }

    private static IResult StageFile(string id, string node, string name, int? visit, RunHistory history)
    {
        var directory = history.ResolveDirectory(id);
        if (directory is null || !RunHistory.NodeIdPattern().IsMatch(node) || !RunHistory.IsPlainName(name))
        {
            return Results.NotFound(new { error = "Not found." });
        }

        var stageDirectory = Path.Combine(directory, node);
        var folder = stageDirectory;
        if (visit is { } number)
        {
            var archived = Path.Combine(stageDirectory, "history", $"visit-{number}");
            if (number >= 1 && Directory.Exists(archived))
            {
                folder = archived;
            }
            else if (number < 1 || number != StageFiles.HistoryVisits(stageDirectory).Count + 1)
            {
                // The latest visit is the stage directory itself; other numbers name history/visit-N.
                return Results.NotFound(new { error = "Not found." });
            }
        }

        var path = Path.Combine(folder, name);
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
        return new SharedFileResult(path, contentType);
    }

    private static IResult Telemetry(string? name, int? limit, RunHistory history)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "The 'name' query parameter (the pipeline's graph name) is required." });
        }

        // Forks are partial re-flights (experiments from a snapshot), so they would skew reach and success rates.
        var runs = history.LoadAll()
            .Where(r => r.OnDisk && r.IsFinished && !r.Simulated && r.ForkedFromJson is null && string.Equals(r.Name, name, StringComparison.Ordinal))
            .Take(Math.Clamp(limit ?? 50, 1, 1000))
            .ToList();
        return Results.Ok(RunTelemetry.Compute(name, runs));
    }

    private static IResult Fork(string id, ForkRequest? body, RunHistory history, PipelineRunManager manager)
    {
        var directory = history.ResolveDirectory(id);
        if (directory is null || history.Load(id) is not { OnDisk: true } record)
        {
            return UnknownRun(id);
        }

        if (string.IsNullOrWhiteSpace(body?.Checkpoint))
        {
            return Results.BadRequest(new { error = "The 'checkpoint' field (a stage snapshot such as \"0004-plan\") is required." });
        }

        var match = SnapshotReference().Match(body.Checkpoint);
        if (!match.Success)
        {
            return Results.BadRequest(new { error = $"'{body.Checkpoint}' is not a checkpoint name (expected e.g. \"0004-plan\")." });
        }

        // "restart-N/<name>" selects a loop-restart directory of the run; a bare name refers to the directory {id}.
        var segment = match.Groups["restart"].Success
            ? record.Segments.FirstOrDefault(s => s.Restart == int.Parse(match.Groups["restart"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : record.Segments.FirstOrDefault(s => RunHistory.PathComparer.Equals(s.Path, directory));
        var snapshot = match.Groups["name"].Value;
        var file = segment is null ? null : Path.Combine(segment.Path, Checkpoint.SnapshotDirectoryName, snapshot + ".json");
        if (segment is null || !File.Exists(file))
        {
            return Results.NotFound(new { error = $"Unknown checkpoint '{body.Checkpoint}'." });
        }

        Checkpoint checkpoint;
        try
        {
            checkpoint = Checkpoint.FromJson(JsonNode.Parse(RunHistory.ReadText(file)) ?? throw new JsonException("empty"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return Results.Json(new { error = $"Checkpoint '{body.Checkpoint}' could not be read: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        var dot = body.Dot;
        if (string.IsNullOrWhiteSpace(dot))
        {
            var graph = Path.Combine(segment.Path, RunHistory.GraphFile);
            dot = File.Exists(graph) ? ReadTextOrNull(graph) : null;
            if (dot is null)
            {
                return Results.Conflict(new { error = "The run has no graph.dot; supply the pipeline as 'dot'." });
            }
        }

        if (Encoding.UTF8.GetByteCount(dot) > manager.Options.MaxDotBytes)
        {
            return Results.BadRequest(new { error = $"The pipeline exceeds {manager.Options.MaxDotBytes} bytes." });
        }

        if (body.Context is { } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                checkpoint.ContextValues[key] = ContextValues.FromJson(value);
            }
        }

        var forkedFrom = new JsonObject
        {
            ["id"] = record.Id,
            ["checkpoint"] = segment.Restart == 0 ? snapshot : $"restart-{segment.Restart}/{snapshot}",
            ["node"] = checkpoint.CurrentNode,
            ["run_id"] = record.RunId,
        };
        var studio = new JsonObject { ["forked_from"] = forkedFrom.DeepClone() };
        if (RunHistory.ReadJsonObject(Path.Combine(record.Segments[0].Path, PipelineRunManager.StudioFileName)) is { } source)
        {
            foreach (var (key, value) in source)
            {
                if (key is not ("forked_from" or "simulated" or "simulation"))
                {
                    studio[key] = value?.DeepClone();
                }
            }
        }

        var started = manager.StartFromCheckpoint(new CheckpointStartRequest
        {
            Dot = dot,
            Checkpoint = checkpoint,
            Studio = studio,
            WorkingDirectory = body.WorkingDirectory,
        });
        if (!started.Succeeded)
        {
            return Results.BadRequest(new { error = started.Error, diagnostics = started.Diagnostics.Select(DiagnosticDto.From) });
        }

        var run = started.Run!;
        return Results.Created($"/pipelines/{run.Id}", new
        {
            id = run.Id,
            status = run.Status,
            name = run.Name,
            logs_root = run.LogsRoot,
            forked_from = forkedFrom,
            diagnostics = started.Diagnostics.Select(DiagnosticDto.From),
            links = new { self = $"/pipelines/{run.Id}", events = $"/pipelines/{run.Id}/events", history = $"/studio/api/runs/{Path.GetFileName(run.LogsRoot)}" },
        });
    }

    private static async Task<IResult> SteerAsync(string id, SteerRequest? body, PipelineRunManager manager, CancellationToken cancellationToken)
    {
        if (manager.Get(id) is not { } run)
        {
            return Results.NotFound(new { error = $"Unknown run '{id}'." });
        }

        var message = body?.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return Results.BadRequest(new { error = "The 'message' field is required." });
        }

        if (Encoding.UTF8.GetByteCount(message) > MaxSteeringBytes)
        {
            return Results.BadRequest(new { error = $"The message exceeds {MaxSteeringBytes} bytes." });
        }

        var requested = string.IsNullOrEmpty(body!.Node) ? null : body.Node;
        if (requested is not null && !run.Graph.ContainsNode(requested))
        {
            return Results.BadRequest(new { error = $"Unknown node '{requested}'." });
        }

        var active = run.ActiveNodes;
        if (run.Status != RunStatus.Running || active.Count == 0)
        {
            return Results.Conflict(new { error = "The run is not running a stage." });
        }

        string node;
        if (requested is not null)
        {
            if (!active.Contains(requested))
            {
                return Results.Conflict(new { error = $"Stage '{requested}' is not running (running: {string.Join(", ", active)})." });
            }

            node = requested;
        }
        else
        {
            node = run.CurrentNode is { } current && active.Contains(current) ? current : active[^1];
        }

        var type = HandlerTypes.Resolve(run.Graph.Nodes[node]);
        if (Unsteerable.Contains(type))
        {
            return Results.Conflict(new { error = $"Stage '{node}' is a {type} stage; only agent (codergen) stages take steering." });
        }

        string path;
        try
        {
            path = await AppendSteeringAsync(Path.Combine(run.CurrentLogsRoot, node), message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Json(new { error = $"Could not queue the steering message: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        run.TryRecordNote(new StageLog(node, "steering queued by user: " + Clip(message), "info") { RunId = run.Id });
        return Results.Accepted(null, new { status = "queued", node, path });
    }

    /// <summary>
    /// Appends a message to the stage's <c>steering.md</c> (UTF-8). The file is held without delete sharing while it is
    /// written, so a backend taking it (by renaming it) never sees half a message; messages queued before the backend
    /// takes the file are delivered together.
    /// </summary>
    private static async Task<string> AppendSteeringAsync(string stageDirectory, string message, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stageDirectory);
        var path = Path.Combine(stageDirectory, "steering.md");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var text = (stream.Length > 0 ? "\n" : "") + message + (message.EndsWith('\n') ? "" : "\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
                return path;
            }
            catch (IOException) when (attempt < 10)
            {
                // Another writer (a supervisor) has the file open; try again shortly.
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The history summary of a run.</summary>
    internal static JsonObject Summary(RunRecord run) => new()
    {
        ["id"] = run.Id,
        ["run_id"] = run.RunId,
        ["name"] = run.Name,
        ["goal"] = run.Goal,
        ["started_at"] = HistoryJson.Iso(run.StartedAt),
        ["finished_at"] = HistoryJson.Iso(run.FinishedAt),
        ["status"] = run.Status,
        ["duration_ms"] = run.DurationMs,
        ["node_count"] = run.NodeCount,
        ["stage_executions"] = run.StageExecutions,
        ["failure_reason"] = run.FailureReason,
        ["live_id"] = run.Live?.Id,
        ["restarts"] = new JsonArray(run.Segments.Skip(1).Select(s => (JsonNode?)s.Name).ToArray()),
        ["source_path"] = run.SourcePath,
        ["simulated"] = run.Simulated,
        ["forked_from"] = run.ForkedFrom,
        ["cost"] = run.Cost.ToJson(),
    };

    private static JsonArray Stages(RunRecord record)
    {
        var stages = new JsonArray();
        foreach (var (node, stats) in record.Nodes)
        {
            stages.Add(Stage(record, node, stats));
        }

        // Stage directories without events (for example when events.jsonl was lost).
        var extra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var segment in record.Segments)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(segment.Path))
                {
                    var node = Path.GetFileName(dir);
                    if (!record.Nodes.ContainsKey(node) && node != Checkpoint.SnapshotDirectoryName && RunHistory.NodeIdPattern().IsMatch(node) &&
                        (StageFiles.Names(dir).Count > 0 || StageFiles.HistoryVisits(dir).Count > 0))
                    {
                        extra.Add(node);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var node in extra)
        {
            stages.Add(Stage(record, node, null));
        }

        return stages;
    }

    private static JsonObject Stage(RunRecord record, string node, NodeStats? stats)
    {
        var segment = record.Segments.LastOrDefault(s => Directory.Exists(Path.Combine(s.Path, node)));
        var directory = segment is null ? null : Path.Combine(segment.Path, node);
        var files = directory is null ? [] : StageFiles.Names(directory);
        var historyVisits = directory is null ? 0 : StageFiles.HistoryVisits(directory).Count;
        string? status;
        if (stats is { Open: true })
        {
            status = record.IsActive ? RunStatus.Running : "interrupted";
        }
        else if (stats is { Statuses.Count: > 0 })
        {
            status = stats.Statuses[^1];
        }
        else
        {
            status = directory is null ? null : HistoryJson.Str(RunHistory.ReadJsonObject(Path.Combine(directory, "status.json"))?["outcome"]);
        }

        return new JsonObject
        {
            ["node"] = node,
            ["visits"] = stats?.Visits ?? historyVisits + (files.Count > 0 ? 1 : 0),
            ["status"] = status,
            ["duration_ms"] = stats is { DurationsMs.Count: > 0 } ? (long)Math.Round(stats.DurationsMs.Sum()) : null,
            ["backend"] = directory is null ? null : RunHistory.ReadJsonObject(Path.Combine(directory, "backend.json")),
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)f).ToArray()),
            ["history_visits"] = historyVisits,
            ["segment"] = segment?.Name,
        };
    }

    /// <summary>The stage snapshots of a run, in order; names in loop-restart directories are prefixed <c>restart-N/</c>.</summary>
    internal static IReadOnlyList<(string Name, RunSegment Segment)> Snapshots(RunRecord record)
    {
        var snapshots = new List<(string, RunSegment)>();
        foreach (var segment in record.Segments)
        {
            var directory = Path.Combine(segment.Path, Checkpoint.SnapshotDirectoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var names = Directory.EnumerateFiles(directory, "*.json")
                    .Select(f => SnapshotFile().Match(Path.GetFileName(f)))
                    .Where(m => m.Success)
                    .OrderBy(m => long.Parse(m.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture))
                    .ThenBy(m => m.Value, StringComparer.Ordinal)
                    .Select(m => m.Groups["name"].Value);
                snapshots.AddRange(names.Select(n => (segment.Restart == 0 ? n : $"restart-{segment.Restart}/{n}", segment)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return snapshots;
    }

    private static JsonObject? CheckpointSummary(JsonObject? checkpoint)
    {
        if (checkpoint is null)
        {
            return null;
        }

        var outcomes = new JsonObject();
        if (checkpoint["node_outcomes"] is JsonObject nodeOutcomes)
        {
            foreach (var (node, outcome) in nodeOutcomes)
            {
                outcomes[node] = HistoryJson.Str(outcome?["outcome"]);
            }
        }

        return new JsonObject
        {
            ["current_node"] = HistoryJson.Str(checkpoint["current_node"]),
            ["completed_nodes"] = checkpoint["completed_nodes"] is JsonArray completed ? completed.DeepClone() : new JsonArray(),
            ["node_outcomes"] = outcomes,
            ["final_status"] = HistoryJson.Str(checkpoint["final_status"]),
        };
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return RunHistory.ReadText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IResult UnknownRun(string id) => Results.NotFound(new { error = $"Unknown run '{id}'." });

    private static string Clip(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    [GeneratedRegex(@"^(?:restart-(?<restart>\d{1,6})/)?(?<name>\d{4,9}-[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotReference();

    [GeneratedRegex(@"^(?<name>(?<index>\d{4,9})-[A-Za-z_][A-Za-z0-9_]*)\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotFile();

    /// <summary>
    /// Streams a file that a running stage may still be writing: opened with full sharing and copied to its current end
    /// without a fixed <c>Content-Length</c>, so a file that grows or is rewritten meanwhile cannot break the response.
    /// </summary>
    private sealed class SharedFileResult(string path, string contentType) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            FileStream stream;
            try
            {
                stream = RunHistory.OpenShared(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Not found." }).ConfigureAwait(false);
                return;
            }

            await using (stream.ConfigureAwait(false))
            {
                httpContext.Response.ContentType = contentType;
                await stream.CopyToAsync(httpContext.Response.Body, 81920, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }
}

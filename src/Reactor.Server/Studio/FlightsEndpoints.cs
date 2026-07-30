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
using Reactor.Workspaces;

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

    /// <summary>
    /// Working directory for the fork instead of the source run's (relative paths resolve against the server's working
    /// directory). Other than the server's own directories it needs <see cref="ReactorServerOptions.AllowClientWorkingDirectory"/>.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Confirms forking into a directory other than the source run's, which may not have the files the run worked on.</summary>
    public bool? ConfirmWorkingDirectory { get; init; }

    /// <summary>
    /// Rewinds the fork's working directory to how it was right after the checkpoint's stage (the git snapshot recorded
    /// with the checkpoint) before the fork starts. The directory's current state is snapshotted first (the undo commit in
    /// the response). Git work trees only; default false.
    /// </summary>
    public bool? RestoreWorkspace { get; init; }
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

    /// <summary>The run's workspace record (snapshot mode, repository, refs) in its first run directory.</summary>
    private const string WorkspaceRecordFile = "workspace.json";

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
        api.MapGet("/runs/{id}/stages/{node}/visits", StageVisits);
        api.MapGet("/runs/{id}/stages/{node}/files/{name}", StageFile);
        api.MapPost("/runs/{id}/fork", ForkAsync);
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
        var snapshots = Snapshots(record);
        return Results.Ok(new JsonObject
        {
            ["summary"] = Summary(record),
            ["manifest"] = RunHistory.ReadJsonObject(Path.Combine(first, RunHistory.ManifestFile)),
            ["result"] = RunHistory.ReadJsonObject(Path.Combine(last, RunHistory.ResultFile)),
            ["checkpoint"] = CheckpointSummary(RunHistory.ReadJsonObject(Path.Combine(last, Checkpoint.FileName))),
            ["graph_dot"] = File.Exists(graph) ? ReadTextOrNull(graph) : null,
            ["studio"] = RunHistory.ReadJsonObject(Path.Combine(first, PipelineRunManager.StudioFileName)),
            ["stages"] = Stages(record),
            ["checkpoints"] = new JsonArray(snapshots.Select(s => (JsonNode?)s.Name).ToArray()),
            ["workspace"] = RunHistory.ReadJsonObject(Path.Combine(first, WorkspaceRecordFile)),
            ["workspace_checkpoints"] = new JsonArray(WorkspaceCheckpoints(snapshots).Select(s => (JsonNode?)s).ToArray()),
        });
    }

    /// <summary>The snapshots saved with a workspace snapshot (<c>checkpoints/&lt;name&gt;.workspace.json</c>), in order.</summary>
    private static IEnumerable<string> WorkspaceCheckpoints(IReadOnlyList<(string Name, RunSegment Segment)> snapshots)
    {
        var recorded = new Dictionary<string, HashSet<string>>(RunHistory.PathComparer);
        foreach (var (name, segment) in snapshots)
        {
            if (!recorded.TryGetValue(segment.Path, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var directory = Path.Combine(segment.Path, Checkpoint.SnapshotDirectoryName);
                    names.UnionWith(Directory.EnumerateFiles(directory, "*" + CheckpointWorkspace.FileSuffix)
                        .Select(f => Path.GetFileName(f)[..^CheckpointWorkspace.FileSuffix.Length]));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                recorded[segment.Path] = names;
            }

            if (names.Contains(BaseName(name)))
            {
                yield return name;
            }
        }
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

    /// <summary>
    /// The files of every visit of a stage in run directory <paramref name="id"/> (<c>history/visit-N</c>, then the stage
    /// directory itself as the latest visit), each with its retried attempts (<c>attempts/attempt-N</c>): how each attempt
    /// ended and what it cost, from its <c>backend.json</c> and <c>status.json</c>.
    /// </summary>
    private static IResult StageVisits(string id, string node, RunHistory history)
    {
        var directory = history.ResolveDirectory(id);
        var stageDirectory = directory is null || !RunHistory.NodeIdPattern().IsMatch(node) ? null : Path.Combine(directory, node);
        if (stageDirectory is null || !Directory.Exists(stageDirectory))
        {
            return Results.NotFound(new { error = "Not found." });
        }

        var visits = new JsonArray();
        var archived = StageFiles.HistoryVisits(stageDirectory);
        foreach (var (number, path) in archived)
        {
            visits.Add(VisitArchive(number, path, latest: false));
        }

        visits.Add(VisitArchive(archived.Count + 1, stageDirectory, latest: true));
        return Results.Ok(new JsonObject { ["node"] = node, ["run"] = Path.GetFileName(directory), ["visits"] = visits });
    }

    private static JsonObject VisitArchive(int visit, string directory, bool latest)
    {
        var attempts = new JsonArray();
        foreach (var (number, path) in StageFiles.Attempts(directory))
        {
            var attempt = AttemptSummary(path);
            attempt["attempt"] = number;
            attempts.Add(attempt);
        }

        var json = AttemptSummary(directory);
        json["visit"] = visit;
        json["latest"] = latest;
        json["attempts"] = attempts;
        return json;
    }

    /// <summary>
    /// What a directory of stage files says about how its attempt ended: <c>outcome</c> (<c>timeout</c>, <c>cancelled</c>,
    /// or <c>error</c> from <c>backend.json</c>; else the <c>status.json</c> outcome), <c>error</c>, the attempt's
    /// <c>backend.json</c>, and its files.
    /// </summary>
    private static JsonObject AttemptSummary(string directory)
    {
        var backend = RunHistory.ReadJsonObject(Path.Combine(directory, "backend.json"));
        var status = RunHistory.ReadJsonObject(Path.Combine(directory, "status.json"));
        return new JsonObject
        {
            ["outcome"] = HistoryJson.NonEmpty(HistoryJson.Str(backend?["outcome"])) ?? HistoryJson.NonEmpty(HistoryJson.Str(status?["outcome"])),
            ["error"] = HistoryJson.NonEmpty(HistoryJson.Str(backend?["error"])) ?? HistoryJson.NonEmpty(HistoryJson.Str(status?["failure_reason"])),
            ["files"] = new JsonArray(StageFiles.Names(directory).Select(f => (JsonNode?)f).ToArray()),
            ["backend"] = backend,
        };
    }

    private static IResult StageFile(string id, string node, string name, int? visit, int? attempt, RunHistory history)
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

        if (attempt is { } attemptNumber)
        {
            // An attempt the engine retried, archived in the visit's attempts/attempt-N.
            folder = Path.Combine(folder, StageAttemptArchive.DirectoryName, $"attempt-{attemptNumber}");
            if (attemptNumber < 1 || !Directory.Exists(folder))
            {
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

    private static async Task<IResult> ForkAsync(string id, ForkRequest? body, RunHistory history, PipelineRunManager manager)
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
                if (key is not ("forked_from" or "simulated" or "simulation" or "workspace_restore"))
                {
                    studio[key] = value?.DeepClone();
                }
            }
        }

        CheckpointWorkspace? restore = null;
        if (body.RestoreWorkspace == true)
        {
            restore = CheckpointWorkspace.Load(CheckpointWorkspace.PathFor(Path.Combine(segment.Path, Checkpoint.SnapshotDirectoryName), snapshot));
            if (restore is null)
            {
                var walk = RunHistory.ReadJsonObject(Path.Combine(record.Segments[0].Path, WorkspaceRecordFile)) is { } workspace &&
                           string.Equals(HistoryJson.Str(workspace["mode"]), "walk", StringComparison.Ordinal);
                return Refusal(
                    walk ? WorkspaceRestoreErrors.Unavailable : WorkspaceRestoreErrors.SnapshotMissing,
                    walk
                        ? "Rewind is unavailable: the run's working directory is not in a git work tree, so its workspace was recorded with a file walk, which keeps no file contents."
                        : $"No workspace snapshot was recorded with checkpoint '{body.Checkpoint}' (workspace tracking was off, the directory is not in a git work tree, or the run predates workspace snapshots).",
                    record.WorkingDirectory);
            }
        }

        var request = new CheckpointStartRequest
        {
            Dot = dot,
            Checkpoint = checkpoint,
            Studio = studio,
            WorkingDirectory = body.WorkingDirectory,
            SourceWorkingDirectory = record.WorkingDirectory,
            ConfirmWorkingDirectory = body.ConfirmWorkingDirectory == true,
            RestoreWorkspace = restore,
            SourceRunDirectory = record.Segments[0].Path,
        };

        // A rewind must not stop half-way because the client went away, so it does not observe the request's abort.
        var started = await manager.StartFromCheckpointAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (!started.Succeeded)
        {
            if (started.ErrorCode is { } code)
            {
                return Refusal(code, started.Error, started.WorkingDirectory);
            }

            return Results.BadRequest(new { error = started.Error, diagnostics = started.Diagnostics.Select(DiagnosticDto.From) });
        }

        var run = started.Run!;
        var origin = !string.IsNullOrWhiteSpace(body.WorkingDirectory) ? "request" : record.WorkingDirectory is null ? "server" : "source_run";
        return Results.Created($"/pipelines/{run.Id}", new
        {
            id = run.Id,
            status = run.Status,
            name = run.Name,
            logs_root = run.LogsRoot,
            working_directory = run.WorkingDirectory,
            working_directory_source = origin,
            source_working_directory = record.WorkingDirectory,
            working_directory_note = record.WorkingDirectory is null
                ? $"The source run did not record its working directory, so the fork runs in {(origin == "server" ? "the server's working directory, " : "")}'{run.WorkingDirectory}'. Check that it has the files the run worked on."
                : null,
            forked_from = forkedFrom,
            workspace_restore = started.Restored?.ToJson(),
            diagnostics = started.Diagnostics.Select(DiagnosticDto.From),
            links = new { self = $"/pipelines/{run.Id}", events = $"/pipelines/{run.Id}/events", history = $"/studio/api/runs/{Path.GetFileName(run.LogsRoot)}" },
        });

        IResult Refusal(string code, string? error, string? workingDirectory) => Results.Json(
            new
            {
                error,
                code,
                working_directory = HistoryJson.NonEmpty(workingDirectory),
                source_working_directory = record.WorkingDirectory,
                server_working_directory = Path.GetFullPath(manager.Options.WorkingDirectory),
            },
            statusCode: code == WorkingDirectoryErrors.NotAllowed ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);
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
        ["working_directory"] = run.WorkingDirectory,
        ["simulated"] = run.Simulated,
        ["forked_from"] = run.ForkedFrom,
        ["cost"] = run.Cost.ToJson(),
        ["metrics"] = Numbers(run.Metrics),
        ["failures_continued"] = run.FailuresContinued,
        ["failed_stages"] = new JsonArray(run.FailedStages.Select(s => (JsonNode?)s).ToArray()),
        ["visit_limits"] = run.Nodes.Values.Sum(n => n.VisitLimits),
        ["budget"] = run.Budget is { } budget
            ? new JsonObject
            {
                ["spent_usd"] = CostTotals.Number(budget.SpentUsd),
                ["limit_usd"] = CostTotals.Number(budget.LimitUsd),
                ["next_node"] = budget.NextNode,
                ["at"] = HistoryJson.Iso(budget.At),
            }
            : null,
    };

    /// <summary>A name → number object (numbers as <see cref="CostTotals.Number"/> writes them).</summary>
    internal static JsonObject Numbers(IEnumerable<KeyValuePair<string, double>> values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = CostTotals.Number(value);
        }

        return json;
    }

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
        var visitDirectories = directory is null ? [] : StageFiles.HistoryVisits(directory);
        var historyVisits = visitDirectories.Count;
        var archivedAttempts = directory is null ? 0 : visitDirectories.Sum(v => StageFiles.Attempts(v.Path).Count) + StageFiles.Attempts(directory).Count;
        var cost = new CostTotals();
        foreach (var usage in stats?.Usage ?? [])
        {
            cost.Add(usage.Values);
        }
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
            ["changes"] = directory is null ? null : ChangesSummary(RunHistory.ReadJsonObject(Path.Combine(directory, "changes.json"))),
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)f).ToArray()),
            ["history_visits"] = historyVisits,
            ["segment"] = segment?.Name,
            ["attempts"] = archivedAttempts,
            ["cost"] = cost.ToJson(),
            ["metrics"] = new JsonArray((stats?.Metrics ?? []).Select(m => (JsonNode?)new JsonObject { ["visit"] = m.Visit, ["metrics"] = Numbers(m.Values) }).ToArray()),
            ["failures_continued"] = stats?.FailuresContinued ?? 0,
            ["visit_limits"] = stats?.VisitLimits ?? 0,
        };
    }

    /// <summary>
    /// The counts of a stage's latest <c>changes.json</c> (what the visit changed in its working directory): added,
    /// modified, deleted, out_of_scope, plus mode, concurrent, and truncated. Null without one.
    /// </summary>
    internal static JsonObject? ChangesSummary(JsonObject? changes)
    {
        if (changes is null)
        {
            return null;
        }

        static int Count(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;
        var summary = changes["summary"];
        return new JsonObject
        {
            ["added"] = Count(summary?["added"]),
            ["modified"] = Count(summary?["modified"]),
            ["deleted"] = Count(summary?["deleted"]),
            ["out_of_scope"] = changes["write_scope"]?["violations"] is JsonArray violations ? violations.Count : 0,
            ["mode"] = HistoryJson.Str(changes["mode"]),
            ["concurrent"] = changes["concurrent"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            ["truncated"] = changes["truncated"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
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

    /// <summary>A snapshot name without its <c>restart-N/</c> prefix.</summary>
    private static string BaseName(string snapshot) => snapshot[(snapshot.LastIndexOf('/') + 1)..];

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

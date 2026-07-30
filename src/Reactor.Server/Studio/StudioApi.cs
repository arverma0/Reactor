using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Server.Studio;

/// <summary>Feature names advertised by <c>GET /studio/api/info</c> (endpoint groups register theirs when mapped).</summary>
public sealed class StudioFeatures
{
    private readonly HashSet<string> _features = new(StringComparer.Ordinal);

    public void Add(params string[] features)
    {
        lock (_features)
        {
            _features.UnionWith(features);
        }
    }

    public IReadOnlyList<string> List()
    {
        lock (_features)
        {
            return _features.Order(StringComparer.Ordinal).ToList();
        }
    }
}

/// <summary>Body of <c>PUT /studio/api/workspace/file</c>.</summary>
public sealed record SaveFileRequest
{
    public string? Path { get; init; }

    public string? Dot { get; init; }

    /// <summary>The layout sidecar; omitted or null leaves an existing sidecar unchanged.</summary>
    public JsonNode? Layout { get; init; }

    /// <summary>The file's modification time when it was opened; the save fails with 409 if it changed since.</summary>
    public DateTimeOffset? BaseModified { get; init; }
}

/// <summary>
/// The Studio API (<c>/studio/api/...</c>): server info, graph parsing with editor metadata, prompt previews, model lists,
/// and workspace files. Run, validation, and question endpoints are the regular API.
/// </summary>
public static class StudioApi
{
    /// <summary>Maps the Studio API.</summary>
    public static WebApplication MapStudioApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Services.GetRequiredService<StudioFeatures>().Add("workspace", "parse", "models", "prompt_preview", "artifacts");
        var api = app.MapGroup("/studio/api");
        api.MapGet("/info", Info);
        api.MapPost("/graph/parse", ParseAsync);
        api.MapPost("/graph/prompt-preview", PromptPreviewAsync);
        api.MapGet("/models", ModelsAsync);
        api.MapGet("/workspace", (StudioWorkspace workspace) => Results.Ok(new { root = workspace.Root, files = workspace.List() }));
        api.MapGet("/workspace/file", (string? path, StudioWorkspace workspace) =>
        {
            var result = workspace.Read(path);
            return result.Succeeded
                ? Results.Ok(new { path = result.Value!.Path, dot = result.Value.Dot, layout = result.Value.Layout, modified = result.Value.Modified })
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });
        api.MapPut("/workspace/file", (SaveFileRequest? body, StudioWorkspace workspace) =>
        {
            var result = workspace.Write(body?.Path, body?.Dot, body?.Layout, body?.BaseModified);
            return result.Succeeded
                ? Results.Ok(new { path = result.Value!.Path, modified = result.Value.Modified })
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });
        api.MapGet("/pipelines/{id}/source", (string id, PipelineRunManager manager) =>
        {
            if (manager.Get(id) is not { } run)
            {
                return Results.NotFound(new { error = $"Unknown run '{id}'." });
            }

            JsonNode? studio = null;
            var studioPath = Path.Combine(run.CurrentLogsRoot, "studio.json");
            if (File.Exists(studioPath))
            {
                try
                {
                    studio = JsonNode.Parse(StudioWorkspace.ReadShared(studioPath));
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    studio = null;
                }
            }

            return Results.Ok(new { id = run.Id, name = run.Name, dot = run.Source, logs_root = run.CurrentLogsRoot, studio });
        });
        return app;
    }

    private static IResult Info(PipelineRunManager manager, StudioFeatures features)
    {
        var options = manager.Options;
        return Results.Ok(new
        {
            name = "Reactor",
            version = PipelineRunner.Version,
            working_directory = Path.GetFullPath(options.WorkingDirectory),
            allow_client_working_directory = options.AllowClientWorkingDirectory,
            runs_directory = manager.Runner.Options.LogsBaseDirectory,
            backend = new
            {
                kind = options.BackendKind ?? (options.Backend is null ? "simulate" : "custom"),
                description = options.BackendDescription ?? (options.Backend is null ? "simulation (no LLM calls)" : options.Backend.GetType().Name),
                routes = options.BackendRoutes,
                status_file = options.Backend?.SupportsStatusFile ?? false,
            },
            handler_types = manager.Runner.Handlers.Types.Order(StringComparer.Ordinal),
            fidelity_modes = FidelityModes.All,
            auth = !string.IsNullOrEmpty(options.AuthToken),
            features = features.List(),
        });
    }

    private static async Task<IResult> ParseAsync(HttpContext http, PipelineRunManager manager)
    {
        var (body, error) = await ReadBodyAsync(http, manager.Options.MaxDotBytes).ConfigureAwait(false);
        if (body is null)
        {
            return Results.BadRequest(new { error });
        }

        Graph graph;
        try
        {
            graph = DotParser.Parse(body.Dot);
        }
        catch (DotParseException ex)
        {
            return Results.Ok(new { ok = false, error = ex.Reason, message = ex.Message, line = ex.Line, column = ex.Column });
        }

        var prepared = manager.Runner.Prepare(graph);
        var json = new JsonObject
        {
            ["ok"] = true,
            ["valid"] = !prepared.HasErrors,
            ["graph"] = StudioGraphJson.Describe(graph),
            ["diagnostics"] = StudioGraphJson.Diagnostics(prepared),
        };
        return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
    }

    private static async Task<IResult> PromptPreviewAsync(HttpContext http, PipelineRunManager manager)
    {
        var (body, error) = await ReadBodyAsync(http, manager.Options.MaxDotBytes).ConfigureAwait(false);
        if (body is null)
        {
            return Results.BadRequest(new { error });
        }

        PreparedPipeline prepared;
        try
        {
            prepared = manager.Runner.Prepare(DotParser.Parse(body.Dot));
        }
        catch (DotParseException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var graph = prepared.Graph;
        var nodeId = body.Json?["node"]?.GetValue<string>();
        if (nodeId is null || graph.GetNode(nodeId) is not { } node)
        {
            return Results.BadRequest(new { error = $"Unknown node '{nodeId}'." });
        }

        var history = new List<StageRecord>();
        if (body.Json?["history"] is JsonArray completed)
        {
            foreach (var id in completed.Select(n => n?.GetValue<string>()).OfType<string>())
            {
                var label = graph.GetNode(id)?.Label ?? id;
                history.Add(new StageRecord(id, label, StageStatus.Success, $"Stage completed: {id}", "", DateTimeOffset.UtcNow, 1));
            }
        }

        var fidelity = body.Json?["fidelity"]?.GetValue<string>() is { Length: > 0 } requested ? requested
            : node.Fidelity.Length > 0 ? node.Fidelity
            : graph.DefaultFidelity.Length > 0 ? graph.DefaultFidelity
            : FidelityModes.Default;
        var context = new PipelineContext();
        foreach (var (key, value) in graph.Attributes)
        {
            if (key != AttrKeys.ModelStylesheet)
            {
                context.Set("graph." + key, value);
            }
        }

        if (history.Count > 0)
        {
            context.Set("last_stage", history[^1].NodeId);
            context.Set("last_response", $"(the response of '{history[^1].NodeId}')");
        }

        if (body.Json?["context"] is JsonObject seeded && ContextValues.FromJson(seeded) is IReadOnlyDictionary<string, object?> values)
        {
            context.ApplyUpdates(values);
        }

        context.Set("internal.fidelity", fidelity);
        const string runId = "<run-id>";
        context.Set("internal.run_id", runId);
        var stageDir = Path.Combine(manager.Runner.Options.LogsBaseDirectory, "<run>", node.Id);
        var backend = manager.Options.Backend;
        var prompt = CodergenHandler.BuildPrompt(node, context, graph, history, runId, backend, stageDir, includeStatusProtocol: true);
        return Results.Ok(new
        {
            node = node.Id,
            handler = HandlerTypes.Resolve(node),
            fidelity,
            status_protocol = backend is not null && backend.SupportsStatusFileFor(node),
            prompt,
        });
    }

    private static async Task<IResult> ModelsAsync(PipelineRunManager manager, CancellationToken cancellationToken)
    {
        if (manager.Options.ListModels is not { } list)
        {
            return Results.Ok(new { models = Array.Empty<ModelDescriptor>(), error = "This server does not provide a model list." });
        }

        try
        {
            var models = await list(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { models });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Ok(new { models = Array.Empty<ModelDescriptor>(), error = ex.Message });
        }
    }

    internal sealed record Body(string Dot, JsonObject? Json);

    /// <summary>Reads a request whose body is DOT text, or JSON with a <c>dot</c> field (other fields are kept).</summary>
    internal static async Task<(Body? Body, string? Error)> ReadBodyAsync(HttpContext http, int maxBytes)
    {
        if (http.Request.ContentLength is { } length && length > maxBytes)
        {
            return (null, $"The request body exceeds {maxBytes} bytes.");
        }

        string text;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
        {
            var content = new StringBuilder();
            var buffer = new char[16 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                content.Append(buffer, 0, read);
                if (content.Length > maxBytes)
                {
                    return (null, $"The request body exceeds {maxBytes} bytes.");
                }
            }

            text = content.ToString();
        }

        if (!(http.Request.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(text) ? (null, "The request body must contain DOT source.") : (new Body(text, null), null);
        }

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid JSON: {ex.Message}");
        }

        return json?["dot"] is JsonValue value && value.TryGetValue<string>(out var dot) && !string.IsNullOrWhiteSpace(dot)
            ? (new Body(dot, json), null)
            : (null, "The 'dot' field (DOT source) is required.");
    }
}

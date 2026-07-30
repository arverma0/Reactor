using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Simulation;
using Reactor.Validation;

namespace Reactor.Server.Studio;

/// <summary>
/// The Studio's wind tunnel: <c>POST /studio/api/simulate</c> runs a Monte Carlo simulation and returns the
/// <see cref="SimulationReport"/>; <c>POST /studio/api/simulate/trace</c> replays one sample with its full event list;
/// <c>GET /studio/api/simulate/presets</c> lists the scenario presets (<see cref="SimulationPreset"/>).
/// Both POSTs take JSON with the DOT source (<c>dot</c>) and an optional <c>scenario</c> (an object, or a preset name) and
/// <c>context</c> (or a raw DOT body, which simulates the empty scenario). Animated or step-by-step simulated runs go
/// through <c>POST /pipelines</c> with a <c>simulation</c> object (see <see cref="RunSimulation"/>).
/// </summary>
public static class SimulationEndpoints
{
    /// <summary>Maps the simulation endpoints and advertises the <c>simulation</c> Studio feature.</summary>
    public static WebApplication MapSimulationApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Services.GetRequiredService<StudioFeatures>().Add("simulation");
        var api = app.MapGroup("/studio/api");
        api.MapPost("/simulate", SimulateAsync);
        api.MapPost("/simulate/trace", TraceAsync);
        api.MapGet("/simulate/presets", () => JsonResult(SimulationPreset.CatalogJson()));
        return app;
    }

    /// <summary>
    /// The report as the simulate API returns it: <see cref="SimulationReport.ToJson"/> plus the pipeline's lint
    /// <c>diagnostics</c> (shared with <c>reactor simulate --json</c>).
    /// </summary>
    public static JsonObject ReportJson(SimulationReport report, PreparedPipeline prepared)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(prepared);
        var json = report.ToJson();
        json["diagnostics"] = StudioGraphJson.Diagnostics(prepared);
        return json;
    }

    /// <summary>
    /// Resolves the <c>scenario</c> of a simulate request (see <see cref="SimulationScenario.TryResolve"/>): null (the empty
    /// scenario), an object (optionally starting from a <c>"preset"</c>), or a preset name. On failure
    /// <paramref name="error"/> and <paramref name="diagnostics"/> say why.
    /// </summary>
    public static bool TryResolveScenario(JsonNode? json, Graph graph, out SimulationScenario scenario, out string? error, out IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(graph);
        error = null;
        diagnostics = [];
        if (json is JsonArray || (json is JsonValue value && value.GetValueKind() != System.Text.Json.JsonValueKind.String))
        {
            scenario = SimulationScenario.Empty;
            error = "'scenario' must be an object or a preset name (" + string.Join(", ", SimulationPreset.All.Select(p => p.Name)) + ").";
            return false;
        }

        if (SimulationScenario.TryResolve(json, graph, out scenario, out var errors))
        {
            return true;
        }

        error = json is JsonValue ? errors[0].Message[0..1].ToUpperInvariant() + errors[0].Message[1..] + "." : "The simulation scenario is invalid.";
        diagnostics = json is JsonValue ? [] : RunSimulation.ToDiagnostics(errors);
        return false;
    }

    private static async Task<IResult> SimulateAsync(HttpContext http, PipelineRunManager manager)
    {
        var (request, failure) = await ReadAsync(http, manager).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (!TryReadInteger(request.Json, "runs", 500, 1, MonteCarloOptions.MaxRuns, out var runs, out var error) ||
            !TryReadInteger(request.Json, "max_steps", 200, 1, MonteCarloOptions.MaxStepsCap, out var maxSteps, out error) ||
            !TryReadSeed(request.Json, out var seed, out error))
        {
            return Results.BadRequest(new { error });
        }

        var report = await MonteCarloSimulator.RunAsync(request.Prepared.Graph, request.Scenario, new MonteCarloOptions
        {
            Runs = (int)runs,
            MaxSteps = (int)maxSteps,
            Seed = seed,
            InitialContext = request.Context,
            CustomHandlerTypes = manager.CustomHandlerTypes(),
        }, http.RequestAborted).ConfigureAwait(false);
        return JsonResult(ReportJson(report, request.Prepared));
    }

    private static async Task<IResult> TraceAsync(HttpContext http, PipelineRunManager manager)
    {
        var (request, failure) = await ReadAsync(http, manager).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (!TryReadInteger(request.Json, "run_index", 0, 0, int.MaxValue, out var runIndex, out var error) ||
            !TryReadInteger(request.Json, "max_steps", 200, 1, MonteCarloOptions.MaxStepsCap, out var maxSteps, out error) ||
            !TryReadSeed(request.Json, out var seed, out error))
        {
            return Results.BadRequest(new { error });
        }

        var trace = await SimulationTrace.RunAsync(
            request.Prepared.Graph,
            request.Scenario,
            seed ?? request.Scenario.Seed ?? SimulationSample.NewSeed(),
            (int)runIndex,
            new MonteCarloOptions { MaxSteps = (int)maxSteps, InitialContext = request.Context, CustomHandlerTypes = manager.CustomHandlerTypes() },
            http.RequestAborted).ConfigureAwait(false);
        return JsonResult(trace.ToJson());
    }

    private sealed record SimulationRequest(PreparedPipeline Prepared, SimulationScenario Scenario, JsonObject? Json, IReadOnlyDictionary<string, object?>? Context);

    /// <summary>Reads and validates the DOT source (with the server runner's transforms and lint rules) and the scenario.</summary>
    private static async Task<(SimulationRequest? Request, IResult? Failure)> ReadAsync(HttpContext http, PipelineRunManager manager)
    {
        var (body, error) = await StudioApi.ReadBodyAsync(http, manager.Options.MaxDotBytes).ConfigureAwait(false);
        if (body is null)
        {
            return (null, Results.BadRequest(new { error }));
        }

        PreparedPipeline prepared;
        try
        {
            prepared = manager.Runner.Prepare(DotParser.Parse(body.Dot));
        }
        catch (DotParseException ex)
        {
            return (null, Invalid(ex.Message, []));
        }

        if (prepared.HasErrors)
        {
            return (null, Invalid("The pipeline has validation errors.", prepared.Diagnostics));
        }

        if (!TryResolveScenario(body.Json?["scenario"], prepared.Graph, out var scenario, out var scenarioError, out var scenarioDiagnostics))
        {
            return (null, Invalid(scenarioError!, scenarioDiagnostics));
        }

        IReadOnlyDictionary<string, object?>? context = null;
        if (body.Json?["context"] is JsonObject contextJson && ContextValues.FromJson(contextJson) is IReadOnlyDictionary<string, object?> values)
        {
            context = values;
        }

        return (new SimulationRequest(prepared, scenario, body.Json, context), null);
    }

    private static bool TryReadInteger(JsonObject? json, string name, long fallback, long min, long max, out long value, out string? error)
    {
        error = null;
        value = fallback;
        if (json?[name] is not { } node)
        {
            return true;
        }

        if (RunSimulation.TryReadLong(node, out var parsed) && parsed >= min && parsed <= max)
        {
            value = parsed;
            return true;
        }

        error = $"'{name}' must be an integer between {min} and {max}.";
        return false;
    }

    private static bool TryReadSeed(JsonObject? json, out long? seed, out string? error)
    {
        seed = null;
        error = null;
        if (json?["seed"] is not { } node)
        {
            return true;
        }

        if (RunSimulation.TryReadLong(node, out var parsed))
        {
            seed = parsed;
            return true;
        }

        error = "'seed' must be an integer.";
        return false;
    }

    private static IResult Invalid(string error, IEnumerable<Diagnostic> diagnostics) =>
        Results.BadRequest(new { error, diagnostics = diagnostics.Select(DiagnosticDto.From) });

    private static IResult JsonResult(JsonObject json) => Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
}

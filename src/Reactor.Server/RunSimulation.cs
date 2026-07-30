using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Simulation;
using Reactor.Validation;

namespace Reactor.Server;

/// <summary>
/// Simulation settings of a managed run, from the <c>"simulation"</c> object of <c>POST /pipelines</c>:
/// <c>{"mode": "stochastic" | "interactive", "scenario": {...}, "seed": 123}</c>. The run goes through the real engine with
/// simulated stages (see <see cref="SimulationSample"/>): in <c>stochastic</c> mode outcomes and human gates are drawn from
/// the scenario and each stage takes <c>step_delay_ms</c> (default <see cref="DefaultStochasticStepDelayMs"/>) so clients
/// can animate it; in <c>interactive</c> mode every simulated stage asks for its outcome through the run's questions, and
/// human gates are asked for real. Explicit <c>duration_ms</c> values are waited out; the scenario's
/// <c>duration_priors</c> (forecasting guesses) are ignored.
/// </summary>
public sealed class RunSimulation
{
    /// <summary>Step delay of stochastic runs whose scenario sets none.</summary>
    public const int DefaultStochasticStepDelayMs = 600;

    /// <summary>The diagnostic rule reported for invalid simulation settings.</summary>
    public const string DiagnosticRule = "simulation_scenario";

    private RunSimulation(SimulationMode mode, SimulationScenario scenario, long seed)
    {
        Mode = mode;
        Scenario = scenario;
        Seed = seed;
        Sample = new SimulationSample(scenario, seed, 0, mode)
        {
            RealTime = true,
            DefaultStepDelayMs = mode == SimulationMode.Stochastic ? DefaultStochasticStepDelayMs : 0,
        };
    }

    public SimulationMode Mode { get; }

    /// <summary><c>stochastic</c> or <c>interactive</c>.</summary>
    public string ModeName => Mode == SimulationMode.Interactive ? "interactive" : "stochastic";

    public SimulationScenario Scenario { get; }

    /// <summary>The seed in use (replay the run's draws with <c>POST /studio/api/simulate/trace</c>, run index 0).</summary>
    public long Seed { get; }

    /// <summary>The run's draws and per-node execution counts; kept across resumes so scripts continue where they stopped.</summary>
    internal SimulationSample Sample { get; }

    /// <summary>Parses the settings; on failure <paramref name="errors"/> holds API diagnostics.</summary>
    public static bool TryParse(JsonObject json, out RunSimulation? simulation, out IReadOnlyList<Diagnostic> errors)
    {
        ArgumentNullException.ThrowIfNull(json);
        simulation = null;
        var found = new List<Diagnostic>();
        var mode = SimulationMode.Stochastic;
        switch (json["mode"])
        {
            case null:
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim().ToLowerInvariant() is "stochastic" or "random":
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim().ToLowerInvariant() is "interactive" or "walkthrough":
                mode = SimulationMode.Interactive;
                break;
            default:
                found.Add(Error($"simulation.mode must be \"stochastic\" or \"interactive\" (got {json["mode"]!.ToJsonString()})."));
                break;
        }

        var scenario = SimulationScenario.Empty;
        switch (json["scenario"])
        {
            case null:
                break;
            case JsonObject scenarioJson:
                if (!SimulationScenario.TryFromJson(scenarioJson, out scenario, out var scenarioErrors))
                {
                    found.AddRange(ToDiagnostics(scenarioErrors));
                }

                break;
            default:
                found.Add(Error("simulation.scenario must be an object."));
                break;
        }

        long? seed = null;
        if (json["seed"] is { } seedNode)
        {
            if (TryReadLong(seedNode, out var parsed))
            {
                seed = parsed;
            }
            else
            {
                found.Add(Error("simulation.seed must be an integer."));
            }
        }

        errors = found;
        if (found.Count > 0)
        {
            return false;
        }

        // Duration priors are forecasting guesses (minutes per stage); managed runs animate, so they are not waited out.
        if (scenario.DurationPriors.Count > 0)
        {
            scenario = scenario with { DurationPriors = new Dictionary<string, int>(StringComparer.Ordinal) };
        }

        simulation = new RunSimulation(mode, scenario, seed ?? scenario.Seed ?? SimulationSample.NewSeed());
        return true;
    }

    /// <summary>Scenario errors as API diagnostics (rule <see cref="DiagnosticRule"/>).</summary>
    public static IReadOnlyList<Diagnostic> ToDiagnostics(IEnumerable<SimulationScenarioError> errors) =>
        (errors ?? throw new ArgumentNullException(nameof(errors)))
            .Select(e => new Diagnostic(DiagnosticRule, Severity.Error, e.ToString()) { NodeId = e.NodeId })
            .ToList();

    /// <summary>Reads an integer from a JSON number or numeric string.</summary>
    internal static bool TryReadLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v)
        {
            return false;
        }

        var text = v.GetValueKind() switch
        {
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.String => v.GetValue<string>().Trim(),
            _ => null,
        };
        return text is not null && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Run options for the simulated execution: the simulated interviewer (stochastic mode) and no retry backoff.</summary>
    internal RunOptions Apply(RunOptions options, Graph graph) => options with
    {
        Interviewer = Mode == SimulationMode.Interactive ? options.Interviewer : Sample.CreateInterviewer(graph),
        Backoff = BackoffConfig.Immediate,
    };

    private static Diagnostic Error(string message) => new(DiagnosticRule, Severity.Error, message);
}

using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Simulation;

/// <summary>
/// A named simulation scenario: how often agent stages (codergen, supervisors, custom handlers) succeed or ask for a retry,
/// how often tool commands pass, and typical stage durations (<see cref="DurationPriors"/>). The presets are the single
/// definition shared by <c>reactor simulate --scenario</c>, the simulate API (<c>"scenario": "realistic"</c>), the
/// Architect's checks, and the Studio's wind tunnel (which bundles <see cref="CatalogJson"/>; a test keeps it in sync).
/// </summary>
/// <param name="Name">Identifier (<c>happy</c>, <c>realistic</c>, <c>stress</c>, <c>chaos</c>).</param>
/// <param name="Label">Display name.</param>
/// <param name="Blurb">One-line description.</param>
/// <param name="AgentSuccess">Share of agent executions that succeed.</param>
/// <param name="AgentRetry">Share of agent executions that ask for a retry (the rest fail).</param>
/// <param name="ToolSuccess">Share of tool commands that pass (the rest fail).</param>
public sealed record SimulationPreset(string Name, string Label, string Blurb, double AgentSuccess, double AgentRetry, double ToolSuccess)
{
    public const string Happy = "happy";
    public const string Realistic = "realistic";
    public const string Stress = "stress";
    public const string Chaos = "chaos";

    /// <summary>
    /// Rough typical durations by stage kind, so a forecast without calibration says something useful about time: agent
    /// stages 10 minutes, tool commands 1 minute, supervisors (manager loops) 30 minutes. Human gates are not modeled
    /// (forecasts are machine time). Calibrating to a flight record replaces them with each stage's real median; a prior
    /// never exceeds a stage's own timeout (see <see cref="SimulationScenario.DurationPriors"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, int> DurationPriors { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [HandlerTypes.Codergen] = 600_000,
        [HandlerTypes.Tool] = 60_000,
        [HandlerTypes.ManagerLoop] = 1_800_000,
    };

    /// <summary>Every preset, mildest first.</summary>
    public static IReadOnlyList<SimulationPreset> All { get; } =
    [
        new(Happy, "Happy path", "Every stage succeeds. Checks the main route reaches the exit.", 1, 0, 1),
        new(Realistic, "Realistic", "Agents succeed 9 times in 10; a quarter of tool runs fail.", 0.9, 0.03, 0.75),
        new(Stress, "Stress", "A bad day: agents miss one time in four, tests fail half the time.", 0.75, 0.05, 0.5),
        new(Chaos, "Chaos", "Hostile conditions. Finds loops that never converge.", 0.55, 0.15, 0.35),
    ];

    /// <summary>Share of agent executions that fail.</summary>
    public double AgentFail => Math.Max(0, 1 - AgentSuccess - AgentRetry);

    /// <summary>The preset named <paramref name="name"/> (case-insensitive), or null.</summary>
    public static SimulationPreset? Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The preset's scenario: outcome weights per handler type and <see cref="DurationPriors"/>. With a
    /// <paramref name="graph"/>, agent and tool stages whose outgoing edges carry route labels choose among them evenly
    /// (see <see cref="SimulationScenario.WithEvenRouteLabels"/>), as in the Studio.
    /// </summary>
    public SimulationScenario ToScenario(Graph? graph = null)
    {
        var scenario = new SimulationScenario
        {
            DurationPriors = DurationPriors,
            Defaults = new Dictionary<string, SimulationBehavior>(StringComparer.Ordinal)
            {
                [HandlerTypes.Codergen] = Weights((StageStatus.Success, AgentSuccess), (StageStatus.Retry, AgentRetry), (StageStatus.Fail, AgentFail)),
                [HandlerTypes.Tool] = Weights((StageStatus.Success, ToolSuccess), (StageStatus.Fail, 1 - ToolSuccess)),
                [HandlerTypes.ManagerLoop] = Weights((StageStatus.Success, AgentSuccess), (StageStatus.Fail, 1 - AgentSuccess)),
            },
        };
        return graph is null ? scenario : scenario.WithEvenRouteLabels(graph);
    }

    /// <summary>JSON: <c>{"name", "label", "blurb", "agent_success", "agent_retry", "tool_success", "scenario"}</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["label"] = Label,
        ["blurb"] = Blurb,
        ["agent_success"] = AgentSuccess,
        ["agent_retry"] = AgentRetry,
        ["tool_success"] = ToolSuccess,
        ["scenario"] = ToScenario().ToJson(),
    };

    /// <summary>
    /// Every preset and the duration priors: <c>{"duration_priors": {...}, "presets": [...]}</c> (served by
    /// <c>GET /studio/api/simulate/presets</c> and bundled with the Studio).
    /// </summary>
    public static JsonObject CatalogJson() => new()
    {
        ["duration_priors"] = new JsonObject(DurationPriors.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["presets"] = new JsonArray(All.Select(p => (JsonNode?)p.ToJson()).ToArray()),
    };

    private static SimulationBehavior Weights(params (StageStatus Status, double Weight)[] weights) => new()
    {
        Outcomes = weights.ToDictionary(w => w.Status, w => SimulationScenario.RoundWeight(w.Weight)),
    };
}

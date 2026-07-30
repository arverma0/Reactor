using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;

namespace Reactor.Simulation;

/// <summary>A scenario validation error: the JSON path of the offending value and what is wrong with it.</summary>
public sealed record SimulationScenarioError(string Path, string Message)
{
    /// <summary>The node the error refers to (errors under <c>nodes.&lt;id&gt;</c>).</summary>
    public string? NodeId { get; init; }

    public override string ToString() => Path.Length > 0 ? $"{Path}: {Message}" : Message;
}

/// <summary>Thrown by <see cref="SimulationScenario.FromJson"/> when a scenario is invalid.</summary>
public sealed class SimulationScenarioException : FormatException
{
    public SimulationScenarioException(IReadOnlyList<SimulationScenarioError> errors)
        : base("Invalid simulation scenario: " + string.Join("; ", errors ?? throw new ArgumentNullException(nameof(errors))))
    {
        Errors = errors;
    }

    public IReadOnlyList<SimulationScenarioError> Errors { get; }
}

/// <summary>A scripted outcome: a status and an optional preferred label (<c>"fail"</c>, <c>"success:Approve"</c>).</summary>
public sealed record SimulationScriptStep(StageStatus Status, string PreferredLabel = "")
{
    /// <summary>Parses <c>status</c> or <c>status:label</c> (status names as in status files, e.g. <c>partial_success</c>).</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SimulationScriptStep? step)
    {
        step = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        var statusText = colon < 0 ? trimmed : trimmed[..colon];
        if (!StageStatusExtensions.TryParse(statusText, out var status))
        {
            return false;
        }

        step = new SimulationScriptStep(status, colon < 0 ? "" : trimmed[(colon + 1)..].Trim());
        return true;
    }

    public override string ToString() => PreferredLabel.Length > 0 ? $"{Status.ToWire()}:{PreferredLabel}" : Status.ToWire();
}

/// <summary>
/// How a stage behaves in simulation. Every field is optional: an unset field falls back to the scenario's
/// <see cref="SimulationScenario.Defaults"/> entry for the node's handler type (custom handler types also fall back to the
/// <c>codergen</c> entry), and finally to the built-in default: always succeed, no preferred label, no delay.
/// </summary>
public sealed record SimulationBehavior
{
    /// <summary>Relative outcome weights (normalized when drawing).</summary>
    public IReadOnlyDictionary<StageStatus, double>? Outcomes { get; init; }

    /// <summary>
    /// Outcomes for successive executions of the node (every attempt, including engine retries, takes the next entry);
    /// once exhausted, <see cref="Outcomes"/> applies.
    /// </summary>
    public IReadOnlyList<SimulationScriptStep>? Script { get; init; }

    /// <summary>Preferred-label weights, drawn on success and partial success (routes over labeled edges).</summary>
    public IReadOnlyDictionary<string, double>? Labels { get; init; }

    /// <summary>Human-gate option weights, matched against an option's label, accelerator key, or target node id.</summary>
    public IReadOnlyDictionary<string, double>? Choices { get; init; }

    /// <summary>Context updates per resulting status (wire name, or <c>*</c> for every status).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? ContextUpdates { get; init; }

    /// <summary>
    /// Simulated latency per execution, in milliseconds (at most <see cref="SimulationScenario.MaxDurationMs"/>). A duration
    /// beyond the stage's <c>timeout</c> times the execution out, as in a real run.
    /// </summary>
    public int? DurationMs { get; init; }

    /// <summary>
    /// Outcome metrics reported by every execution, drawn per execution (see <see cref="SimulationMetric"/>); the engine
    /// records them as <c>metrics.&lt;name&gt;</c>, so edge conditions can route on them. Not applicable to human gates.
    /// </summary>
    public IReadOnlyDictionary<string, SimulationMetric>? Metrics { get; init; }

    /// <summary>Simulated cost of every execution (attempts and timed-out executions included), in USD.</summary>
    public double? CostUsd { get; init; }
}

/// <summary>
/// A simulation scenario for the Studio's "wind tunnel": per-node and per-handler-type stage behaviors (outcome weights,
/// scripts, preferred labels, human-gate choices, context updates, outcome metrics, latency, cost) plus typical durations
/// per handler type and the random seed. JSON uses snake_case:
/// <code>
/// {"seed": 123, "step_delay_ms": 0, "duration_priors": {"codergen": "10m", "tool": "1m"},
///  "defaults": {"tool": {"outcomes": {"success": 0.7, "fail": 0.3}}},
///  "nodes": {"test": {"script": ["fail", "fail", "success"], "context_updates": {"success": {"tests_passed": true}}},
///            "review": {"metrics": {"score": {"values": {"3": 0.2, "4": 0.5, "5": 0.3}}}, "cost": 0.4}}}
/// </code>
/// </summary>
public sealed record SimulationScenario
{
    /// <summary>
    /// Upper bound for <c>duration_ms</c>, <c>step_delay_ms</c>, and <c>duration_priors</c> (24 hours, beyond the longest
    /// stage any backend supports). A node's own <c>timeout</c> still applies: a longer simulated duration times it out.
    /// </summary>
    public const int MaxDurationMs = 86_400_000;

    /// <summary>Upper bound for <c>cost</c> (USD per execution).</summary>
    public const double MaxCostUsd = 10_000;

    /// <summary>The context-update key that applies to every status.</summary>
    public const string AnyStatus = "*";

    private static readonly IReadOnlyDictionary<StageStatus, double> AlwaysSucceed = new Dictionary<StageStatus, double> { [StageStatus.Success] = 1 };

    /// <summary>The empty scenario: every simulated stage succeeds, human gates choose uniformly.</summary>
    public static SimulationScenario Empty { get; } = new();

    /// <summary>Random seed; null lets the simulator choose one (and report it).</summary>
    public long? Seed { get; init; }

    /// <summary>Default latency of every simulated execution, in milliseconds (overridden by <c>duration_ms</c>).</summary>
    public int? StepDelayMs { get; init; }

    /// <summary>
    /// Typical latency per handler type (custom types fall back to the <c>codergen</c> entry), used for executions whose
    /// node and handler type set no <c>duration_ms</c> when the scenario sets no <c>step_delay_ms</c>. A prior is a guess,
    /// so it never times a stage out: it is capped at the stage's timeout (for tools, the default tool timeout; for human
    /// gates, the question's timeout).
    /// </summary>
    public IReadOnlyDictionary<string, int> DurationPriors { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Behaviors per handler type (<c>codergen</c>, <c>tool</c>, <c>stack.manager_loop</c>, <c>wait.human</c>, custom types).</summary>
    public IReadOnlyDictionary<string, SimulationBehavior> Defaults { get; init; } = new Dictionary<string, SimulationBehavior>(StringComparer.Ordinal);

    /// <summary>Behaviors per node id (override <see cref="Defaults"/> field by field).</summary>
    public IReadOnlyDictionary<string, SimulationBehavior> Nodes { get; init; } = new Dictionary<string, SimulationBehavior>(StringComparer.Ordinal);

    /// <summary>Parses a scenario (null or <c>{}</c> is the empty scenario).</summary>
    /// <exception cref="SimulationScenarioException">The scenario is invalid; <see cref="SimulationScenarioException.Errors"/> lists why.</exception>
    public static SimulationScenario FromJson(JsonObject? json) =>
        TryFromJson(json, out var scenario, out var errors) ? scenario : throw new SimulationScenarioException(errors);

    /// <summary>
    /// Resolves a scenario as the simulate API and <c>reactor simulate</c> accept it: null (the empty scenario), a preset
    /// name (<c>"realistic"</c>; see <see cref="SimulationPreset"/>), or a scenario object — which may name a
    /// <c>"preset"</c> to start from. Presets get even route-label weights for <paramref name="graph"/>
    /// (<see cref="WithEvenRouteLabels"/>), as in the Studio.
    /// </summary>
    public static bool TryResolve(JsonNode? json, Graph graph, out SimulationScenario scenario, out IReadOnlyList<SimulationScenarioError> errors)
    {
        ArgumentNullException.ThrowIfNull(graph);
        switch (json)
        {
            case null:
                scenario = Empty;
                errors = [];
                return true;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var name = value.GetValue<string>();
                if (SimulationPreset.Find(name) is { } preset)
                {
                    scenario = preset.ToScenario(graph);
                    errors = [];
                    return true;
                }

                scenario = Empty;
                errors = [new SimulationScenarioError("", UnknownPreset($"'{name}'"))];
                return false;
            case JsonObject obj:
                if (!TryFromJson(obj, out scenario, out errors))
                {
                    return false;
                }

                if (obj.Any(kv => Canonical(kv.Key) == "preset"))
                {
                    scenario = scenario.WithEvenRouteLabels(graph);
                }

                return true;
            default:
                scenario = Empty;
                errors = [new SimulationScenarioError("", "must be a scenario object or a preset name (" + string.Join(", ", SimulationPreset.All.Select(p => p.Name)) + ")")];
                return false;
        }
    }

    /// <summary>
    /// This scenario with <paramref name="overrides"/> laid over it: the seed and step delay when set, duration priors per
    /// handler type, and behaviors per handler type and per node field by field.
    /// </summary>
    public SimulationScenario MergedWith(SimulationScenario overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        static Dictionary<string, SimulationBehavior> Merge(IReadOnlyDictionary<string, SimulationBehavior> under, IReadOnlyDictionary<string, SimulationBehavior> over)
        {
            var merged = new Dictionary<string, SimulationBehavior>(under, StringComparer.Ordinal);
            foreach (var (key, behavior) in over)
            {
                merged[key] = merged.TryGetValue(key, out var below)
                    ? new SimulationBehavior
                    {
                        Outcomes = behavior.Outcomes ?? below.Outcomes,
                        Script = behavior.Script ?? below.Script,
                        Labels = behavior.Labels ?? below.Labels,
                        Choices = behavior.Choices ?? below.Choices,
                        ContextUpdates = behavior.ContextUpdates ?? below.ContextUpdates,
                        DurationMs = behavior.DurationMs ?? below.DurationMs,
                        Metrics = behavior.Metrics ?? below.Metrics,
                        CostUsd = behavior.CostUsd ?? below.CostUsd,
                    }
                    : behavior;
            }

            return merged;
        }

        var priors = new Dictionary<string, int>(DurationPriors, StringComparer.Ordinal);
        foreach (var (type, prior) in overrides.DurationPriors)
        {
            priors[type] = prior;
        }

        return new SimulationScenario
        {
            Seed = overrides.Seed ?? Seed,
            StepDelayMs = overrides.StepDelayMs ?? StepDelayMs,
            DurationPriors = priors,
            Defaults = Merge(Defaults, overrides.Defaults),
            Nodes = Merge(Nodes, overrides.Nodes),
        };
    }

    /// <summary>
    /// Parses a scenario leniently: keys may be snake_case or camelCase, numbers may be strings, durations may carry units
    /// (<c>"1.5s"</c>), <c>outcomes</c> may be a single status, and <c>script</c> a single entry. A <c>"preset"</c> key
    /// starts from that preset's scenario (<see cref="SimulationPreset.ToScenario"/>), overridden by the other fields.
    /// Unknown top-level keys are ignored (room for metadata such as a name); unknown statuses or behavior fields are errors.
    /// </summary>
    public static bool TryFromJson(JsonObject? json, out SimulationScenario scenario, out IReadOnlyList<SimulationScenarioError> errors)
    {
        var found = new List<SimulationScenarioError>();
        long? seed = null;
        int? stepDelay = null;
        SimulationPreset? preset = null;
        var priors = new Dictionary<string, int>(StringComparer.Ordinal);
        var defaults = new Dictionary<string, SimulationBehavior>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, SimulationBehavior>(StringComparer.Ordinal);

        foreach (var (key, value) in json ?? [])
        {
            if (value is null)
            {
                continue;
            }

            switch (Canonical(key))
            {
                case "seed":
                    if (TryInteger(value, out var parsedSeed))
                    {
                        seed = parsedSeed;
                    }
                    else
                    {
                        found.Add(new SimulationScenarioError("seed", "must be an integer"));
                    }

                    break;
                case "stepdelayms":
                    stepDelay = ParseDuration(value, "step_delay_ms", null, found);
                    break;
                case "durationpriors":
                    ParseDurationPriors(value, "duration_priors", priors, found);
                    break;
                case "preset":
                    preset = value is JsonValue presetName && presetName.GetValueKind() == JsonValueKind.String ? SimulationPreset.Find(presetName.GetValue<string>()) : null;
                    if (preset is null)
                    {
                        found.Add(new SimulationScenarioError("preset", UnknownPreset(value.ToJsonString())));
                    }

                    break;
                case "defaults":
                    ParseBehaviors(value, "defaults", isNodes: false, defaults, found);
                    break;
                case "nodes":
                    ParseBehaviors(value, "nodes", isNodes: true, nodes, found);
                    break;
            }
        }

        scenario = new SimulationScenario { Seed = seed, StepDelayMs = stepDelay, DurationPriors = priors, Defaults = defaults, Nodes = nodes };
        if (preset is not null)
        {
            scenario = preset.ToScenario().MergedWith(scenario);
        }

        errors = found;
        return found.Count == 0;
    }

    private static string UnknownPreset(string name) =>
        $"unknown preset {name} (expected {string.Join(", ", SimulationPreset.All.Select(p => p.Name))})";

    /// <summary>The canonical JSON form (snake_case, wire status names); only fields that are set are written.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Seed is { } seed)
        {
            json["seed"] = seed;
        }

        if (StepDelayMs is { } delay)
        {
            json["step_delay_ms"] = delay;
        }

        if (DurationPriors.Count > 0)
        {
            json["duration_priors"] = new JsonObject(DurationPriors.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        }

        if (Defaults.Count > 0)
        {
            json["defaults"] = new JsonObject(Defaults.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)BehaviorJson(kv.Value))));
        }

        if (Nodes.Count > 0)
        {
            json["nodes"] = new JsonObject(Nodes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)BehaviorJson(kv.Value))));
        }

        return json;
    }

    /// <summary>
    /// Cross-checks the scenario against a pipeline and returns warnings: unknown node ids or handler types, behaviors on
    /// nodes that always run their real handler (start, exit, conditional, parallel, fan-in), labels that match no
    /// outgoing edge, choices that match no human-gate option, metrics on human gates, and durations longer than a
    /// node's own <c>timeout</c> (every execution would time out).
    /// </summary>
    public IReadOnlyList<string> Check(Graph graph, IEnumerable<string>? customHandlerTypes = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var warnings = new List<string>();
        var known = new HashSet<string>(HandlerTypes.BuiltIn, StringComparer.Ordinal);
        known.UnionWith(customHandlerTypes ?? []);
        foreach (var type in DurationPriors.Keys)
        {
            if (!known.Contains(type))
            {
                warnings.Add($"duration_priors.{type}: unknown handler type (known: {string.Join(", ", known.Order(StringComparer.Ordinal))}).");
            }
            else if (!IsSimulatedType(type))
            {
                warnings.Add($"duration_priors.{type}: '{type}' nodes always run their real handler; this entry has no effect.");
            }
        }

        foreach (var (type, behavior) in Defaults)
        {
            if (!known.Contains(type))
            {
                warnings.Add($"defaults.{type}: unknown handler type (known: {string.Join(", ", known.Order(StringComparer.Ordinal))}).");
            }
            else if (!IsSimulatedType(type))
            {
                warnings.Add($"defaults.{type}: '{type}' nodes always run their real handler; this entry has no effect.");
            }
            else if (behavior.Choices is not null && type != HandlerTypes.WaitHuman)
            {
                warnings.Add($"defaults.{type}.choices: choices only apply to human gates (wait.human).");
            }
            else if (behavior.Metrics is not null && type == HandlerTypes.WaitHuman)
            {
                warnings.Add($"defaults.{type}.metrics: human gates report no metrics; this entry has no effect.");
            }
        }

        foreach (var (id, behavior) in Nodes)
        {
            var path = "nodes." + id;
            if (graph.GetNode(id) is not { } node)
            {
                warnings.Add($"{path}: the pipeline has no node '{id}'.");
                continue;
            }

            var type = HandlerTypes.Resolve(node);
            if (!IsSimulatedType(type))
            {
                warnings.Add($"{path}: '{id}' is a {type} node, which always runs its real handler; this entry has no effect.");
                continue;
            }

            var edges = graph.OutgoingEdges(id);
            if (type == HandlerTypes.WaitHuman)
            {
                var options = edges.Select(e => (Label: e.Label.Length > 0 ? e.Label : e.To, e.To)).ToList();
                bool MatchesOption(string value) => options.Any(o =>
                    string.Equals(Accelerators.ParseKey(o.Label), value.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o.To, value.Trim(), StringComparison.Ordinal) ||
                    Accelerators.NormalizeLabel(o.Label) == Accelerators.NormalizeLabel(value));
                if (behavior.Labels is not null)
                {
                    warnings.Add($"{path}.labels: labels do not apply to human gates; use choices.");
                }

                if (behavior.Metrics is not null)
                {
                    warnings.Add($"{path}.metrics: human gates report no metrics; this entry has no effect.");
                }

                foreach (var choice in (behavior.Choices?.Keys ?? []).Where(c => !MatchesOption(c)))
                {
                    warnings.Add($"{path}.choices: '{choice}' matches no option of the gate (options: {string.Join(", ", options.Select(o => o.Label))}).");
                }

                foreach (var step in (behavior.Script ?? []).Where(s => s.PreferredLabel.Length > 0 && !MatchesOption(s.PreferredLabel)))
                {
                    warnings.Add($"{path}.script: '{step}' matches no option of the gate.");
                }

                continue;
            }

            if (behavior.Choices is not null)
            {
                warnings.Add($"{path}.choices: choices only apply to human gates (wait.human).");
            }

            var labels = edges.Select(e => Accelerators.NormalizeLabel(e.Label)).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var label in (behavior.Labels?.Keys ?? []).Where(l => !labels.Contains(Accelerators.NormalizeLabel(l))))
            {
                warnings.Add($"{path}.labels: '{label}' matches no outgoing edge label of '{id}'.");
            }

            foreach (var step in (behavior.Script ?? []).Where(s => s.PreferredLabel.Length > 0 && !labels.Contains(Accelerators.NormalizeLabel(s.PreferredLabel))))
            {
                warnings.Add($"{path}.script: '{step}' matches no outgoing edge label of '{id}'.");
            }
        }

        foreach (var node in graph.Nodes.Values)
        {
            var type = HandlerTypes.Resolve(node);
            if (!IsSimulatedType(type) || type == HandlerTypes.WaitHuman || node.Timeout is not { } timeout || timeout <= TimeSpan.Zero)
            {
                continue;
            }

            var behavior = Resolve(node.Id, type);
            var duration = behavior.DurationMs ?? StepDelayMs;
            if (duration is { } ms && ms > timeout.TotalMilliseconds)
            {
                var source = Nodes.GetValueOrDefault(node.Id)?.DurationMs is not null ? $"nodes.{node.Id}.duration_ms"
                    : behavior.DurationMs is not null ? $"defaults.{(Defaults.GetValueOrDefault(type)?.DurationMs is not null ? type : HandlerTypes.Codergen)}.duration_ms"
                    : "step_delay_ms";
                warnings.Add(
                    $"{source}: {AttributeValues.FormatDuration(TimeSpan.FromMilliseconds(ms))} exceeds the timeout of '{node.Id}' " +
                    $"({AttributeValues.FormatDuration(timeout)}), so every execution of '{node.Id}' times out.");
            }
        }

        return warnings;
    }

    /// <summary>Merges the node's behavior with the defaults for its handler type, field by field.</summary>
    internal ResolvedBehavior Resolve(string nodeId, string handlerType)
    {
        Nodes.TryGetValue(nodeId, out var node);
        Defaults.TryGetValue(handlerType, out var byType);
        var codergen = HandlerTypes.BuiltIn.Contains(handlerType) ? null : Defaults.GetValueOrDefault(HandlerTypes.Codergen);
        return new ResolvedBehavior(
            node?.Outcomes ?? byType?.Outcomes ?? codergen?.Outcomes ?? AlwaysSucceed,
            node?.Script ?? byType?.Script ?? codergen?.Script ?? [],
            node?.Labels ?? byType?.Labels ?? codergen?.Labels,
            node?.Choices ?? byType?.Choices ?? codergen?.Choices,
            node?.ContextUpdates ?? byType?.ContextUpdates ?? codergen?.ContextUpdates,
            node?.DurationMs ?? byType?.DurationMs ?? codergen?.DurationMs,
            node?.Metrics ?? byType?.Metrics ?? codergen?.Metrics,
            node?.CostUsd ?? byType?.CostUsd ?? codergen?.CostUsd);
    }

    /// <summary>The duration prior for a handler type (custom types fall back to the <c>codergen</c> prior).</summary>
    internal int? PriorFor(string handlerType) =>
        DurationPriors.TryGetValue(handlerType, out var prior) ? prior
        : !HandlerTypes.BuiltIn.Contains(handlerType) && DurationPriors.TryGetValue(HandlerTypes.Codergen, out var codergen) ? codergen
        : null;

    /// <summary>
    /// The scenario with even preferred-label weights for every agent (codergen) or tool stage of <paramref name="graph"/>
    /// whose unconditional outgoing edges carry labels and whose behavior (node or handler type) sets no <c>labels</c>,
    /// so labeled routes (Approve / Rework) are all exercised. The Studio builds its scenarios the same way.
    /// </summary>
    public SimulationScenario WithEvenRouteLabels(Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var nodes = new Dictionary<string, SimulationBehavior>(Nodes, StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Values)
        {
            var type = HandlerTypes.Resolve(node);
            if (type is not (HandlerTypes.Codergen or HandlerTypes.Tool) || Nodes.GetValueOrDefault(node.Id)?.Labels is not null || Defaults.GetValueOrDefault(type)?.Labels is not null)
            {
                continue;
            }

            var labels = graph.OutgoingEdges(node.Id)
                .Where(e => e.Condition.Trim().Length == 0 && e.Label.Length > 0)
                .Select(e => e.Label)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (labels.Count > 0)
            {
                var weight = RoundWeight(1.0 / labels.Count);
                nodes[node.Id] = (Nodes.GetValueOrDefault(node.Id) ?? new SimulationBehavior()) with { Labels = labels.ToDictionary(l => l, _ => weight, StringComparer.Ordinal) };
            }
        }

        return this with { Nodes = nodes };
    }

    /// <summary>Rounds a weight to three decimals, as the Studio does.</summary>
    internal static double RoundWeight(double weight) => Math.Round(weight, 3, MidpointRounding.AwayFromZero);

    /// <summary>True for handler types whose behavior the simulation replaces (or, for human gates, answers).</summary>
    internal static bool IsSimulatedType(string type) =>
        type is not (HandlerTypes.Start or HandlerTypes.Exit or HandlerTypes.Conditional or HandlerTypes.Parallel or HandlerTypes.FanIn);

    private static void ParseBehaviors(JsonNode value, string path, bool isNodes, Dictionary<string, SimulationBehavior> into, List<SimulationScenarioError> errors)
    {
        if (value is not JsonObject map)
        {
            errors.Add(new SimulationScenarioError(path, isNodes ? "must be an object keyed by node id" : "must be an object keyed by handler type"));
            return;
        }

        foreach (var (key, entry) in map)
        {
            var id = key.Trim();
            if (id.Length == 0)
            {
                errors.Add(new SimulationScenarioError(path, "keys must not be empty"));
                continue;
            }

            if (entry is not null)
            {
                into[id] = ParseBehavior(entry, $"{path}.{id}", isNodes ? id : null, errors);
            }
        }
    }

    private static SimulationBehavior ParseBehavior(JsonNode node, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        if (node is not JsonObject obj)
        {
            errors.Add(new SimulationScenarioError(path, "must be an object") { NodeId = nodeId });
            return new SimulationBehavior();
        }

        var behavior = new SimulationBehavior();
        foreach (var (key, value) in obj)
        {
            if (value is null)
            {
                continue;
            }

            var fieldPath = $"{path}.{key}";
            behavior = Canonical(key) switch
            {
                "outcomes" => behavior with { Outcomes = ParseOutcomes(value, fieldPath, nodeId, errors) },
                "script" => behavior with { Script = ParseScript(value, fieldPath, nodeId, errors) },
                "labels" => behavior with { Labels = ParseWeights(value, fieldPath, nodeId, "labels", errors) },
                "choices" => behavior with { Choices = ParseWeights(value, fieldPath, nodeId, "choices", errors) },
                "contextupdates" => behavior with { ContextUpdates = ParseContextUpdates(value, fieldPath, nodeId, errors) },
                "durationms" => behavior with { DurationMs = ParseDuration(value, fieldPath, nodeId, errors) },
                "metrics" => behavior with { Metrics = SimulationMetric.ParseAll(value, fieldPath, nodeId, errors) },
                "cost" or "costusd" => behavior with { CostUsd = ParseCost(value, fieldPath, nodeId, errors) },
                _ => Unknown(behavior, fieldPath, nodeId, errors),
            };
        }

        return behavior;
    }

    private static SimulationBehavior Unknown(SimulationBehavior behavior, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        errors.Add(new SimulationScenarioError(path, "unknown field (expected outcomes, script, labels, choices, context_updates, duration_ms, metrics, or cost)") { NodeId = nodeId });
        return behavior;
    }

    private static void ParseDurationPriors(JsonNode value, string path, Dictionary<string, int> into, List<SimulationScenarioError> errors)
    {
        if (value is not JsonObject map)
        {
            errors.Add(new SimulationScenarioError(path, "must be an object keyed by handler type, e.g. {\"codergen\": \"10m\", \"tool\": \"1m\"}"));
            return;
        }

        foreach (var (key, entry) in map)
        {
            var type = key.Trim();
            if (type.Length == 0)
            {
                errors.Add(new SimulationScenarioError(path, "keys must not be empty"));
                continue;
            }

            if (entry is not null && ParseDuration(entry, $"{path}.{type}", null, errors) is { } duration)
            {
                into[type] = duration;
            }
        }
    }

    private static double? ParseCost(JsonNode value, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        var node = value is JsonValue text && text.GetValueKind() == JsonValueKind.String && text.GetValue<string>().Trim().StartsWith('$')
            ? JsonValue.Create(text.GetValue<string>().Trim()[1..])
            : value;
        if (TryNumber(node, out var cost) && double.IsFinite(cost) && cost >= 0 && cost <= MaxCostUsd)
        {
            return cost;
        }

        errors.Add(new SimulationScenarioError(path, string.Create(CultureInfo.InvariantCulture, $"must be a cost in USD per execution between 0 and {MaxCostUsd}")) { NodeId = nodeId });
        return null;
    }

    private static IReadOnlyDictionary<StageStatus, double>? ParseOutcomes(JsonNode value, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        if (value is JsonValue single && single.GetValueKind() == JsonValueKind.String)
        {
            if (StageStatusExtensions.TryParse(single.GetValue<string>(), out var only))
            {
                return new Dictionary<StageStatus, double> { [only] = 1 };
            }

            errors.Add(new SimulationScenarioError(path, UnknownStatus(single.GetValue<string>())) { NodeId = nodeId });
            return null;
        }

        if (value is not JsonObject obj)
        {
            errors.Add(new SimulationScenarioError(path, "must be an object of status weights, e.g. {\"success\": 0.8, \"fail\": 0.2}") { NodeId = nodeId });
            return null;
        }

        var weights = new Dictionary<StageStatus, double>();
        var valid = true;
        foreach (var (key, weight) in obj)
        {
            if (!StageStatusExtensions.TryParse(key, out var status))
            {
                errors.Add(new SimulationScenarioError($"{path}.{key}", UnknownStatus(key)) { NodeId = nodeId });
                valid = false;
                continue;
            }

            if (!TryWeight(weight, $"{path}.{key}", nodeId, errors, out var w))
            {
                valid = false;
                continue;
            }

            weights[status] = weights.GetValueOrDefault(status) + w;
        }

        if (!valid)
        {
            return null;
        }

        if (weights.Count > 0 && weights.Values.Sum() <= 0)
        {
            errors.Add(new SimulationScenarioError(path, "weights must not all be zero") { NodeId = nodeId });
            return null;
        }

        return weights.Count == 0 ? null : weights;
    }

    private static IReadOnlyDictionary<string, double>? ParseWeights(JsonNode value, string path, string? nodeId, string what, List<SimulationScenarioError> errors)
    {
        if (value is not JsonObject obj)
        {
            errors.Add(new SimulationScenarioError(path, $"must be an object of {what} to weights, e.g. {{\"Approve\": 0.8, \"Fix\": 0.2}}") { NodeId = nodeId });
            return null;
        }

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var valid = true;
        foreach (var (key, weight) in obj)
        {
            var name = key.Trim();
            if (name.Length == 0)
            {
                errors.Add(new SimulationScenarioError(path, "keys must not be empty") { NodeId = nodeId });
                valid = false;
                continue;
            }

            if (!TryWeight(weight, $"{path}.{key}", nodeId, errors, out var w))
            {
                valid = false;
                continue;
            }

            weights[name] = weights.GetValueOrDefault(name) + w;
        }

        if (!valid)
        {
            return null;
        }

        if (weights.Count > 0 && weights.Values.Sum() <= 0)
        {
            errors.Add(new SimulationScenarioError(path, "weights must not all be zero") { NodeId = nodeId });
            return null;
        }

        return weights.Count == 0 ? null : weights;
    }

    private static IReadOnlyList<SimulationScriptStep>? ParseScript(JsonNode value, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        List<JsonNode?> entries = value is JsonArray array ? [.. array] : [value];
        var steps = new List<SimulationScriptStep>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entryPath = value is JsonArray ? $"{path}[{i}]" : path;
            SimulationScriptStep? step = null;
            switch (entries[i])
            {
                case JsonValue text when text.GetValueKind() == JsonValueKind.String:
                    SimulationScriptStep.TryParse(text.GetValue<string>(), out step);
                    break;
                case JsonObject entry:
                    var status = StringField(entry, "outcome") ?? StringField(entry, "status");
                    if (status is not null && StageStatusExtensions.TryParse(status, out var parsed))
                    {
                        step = new SimulationScriptStep(parsed, (StringField(entry, "preferred_label") ?? StringField(entry, "label") ?? "").Trim());
                    }

                    break;
            }

            if (step is null)
            {
                errors.Add(new SimulationScenarioError(entryPath,
                    $"invalid script entry {entries[i]?.ToJsonString() ?? "null"} (expected a status such as \"fail\" or \"success:Approve\")") { NodeId = nodeId });
                continue;
            }

            steps.Add(step);
        }

        return steps.Count == 0 ? null : steps;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? ParseContextUpdates(JsonNode value, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        if (value is not JsonObject obj)
        {
            errors.Add(new SimulationScenarioError(path, "must be an object keyed by status, e.g. {\"success\": {\"tests_passed\": true}}") { NodeId = nodeId });
            return null;
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var (key, updates) in obj)
        {
            string statusKey;
            if (key.Trim() is AnyStatus or "any" or "all")
            {
                statusKey = AnyStatus;
            }
            else if (StageStatusExtensions.TryParse(key, out var status))
            {
                statusKey = status.ToWire();
            }
            else
            {
                errors.Add(new SimulationScenarioError($"{path}.{key}", UnknownStatus(key) + " (or \"*\" for every status)") { NodeId = nodeId });
                continue;
            }

            if (updates is not JsonObject values)
            {
                errors.Add(new SimulationScenarioError($"{path}.{key}", "must be an object of context keys and values") { NodeId = nodeId });
                continue;
            }

            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (contextKey, contextValue) in values)
            {
                map[contextKey] = ContextValues.FromJson(contextValue);
            }

            result[statusKey] = map;
        }

        return result.Count == 0 ? null : result;
    }

    private static int? ParseDuration(JsonNode value, string path, string? nodeId, List<SimulationScenarioError> errors)
    {
        double? milliseconds = null;
        if (TryNumber(value, out var number))
        {
            milliseconds = number;
        }
        else if (value is JsonValue text && text.GetValueKind() == JsonValueKind.String && AttributeValues.ParseDuration(text.GetValue<string>()) is { } duration)
        {
            milliseconds = duration.TotalMilliseconds;
        }

        if (milliseconds is not { } ms || double.IsNaN(ms) || ms < 0 || ms > MaxDurationMs)
        {
            errors.Add(new SimulationScenarioError(path, $"must be a duration in milliseconds between 0 and {MaxDurationMs} (24h), or a string such as \"1.5s\" or \"2h\"") { NodeId = nodeId });
            return null;
        }

        return (int)Math.Round(ms);
    }

    private static bool TryWeight(JsonNode? value, string path, string? nodeId, List<SimulationScenarioError> errors, out double weight)
    {
        if (TryNumber(value, out weight) && double.IsFinite(weight) && weight >= 0)
        {
            return true;
        }

        errors.Add(new SimulationScenarioError(path, "weight must be a non-negative number") { NodeId = nodeId });
        return false;
    }

    private static bool TryNumber(JsonNode? node, out double value)
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
        return text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryInteger(JsonNode? node, out long value)
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
        if (text is null)
        {
            return false;
        }

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == Math.Floor(number) && Math.Abs(number) < 9e15)
        {
            value = (long)number;
            return true;
        }

        return false;
    }

    private static string? StringField(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string UnknownStatus(string value) =>
        $"unknown status '{value}' (expected success, partial_success, retry, fail, or skipped)";

    /// <summary>Lowercase letters and digits only, so <c>step_delay_ms</c>, <c>stepDelayMs</c>, and <c>step-delay-ms</c> match.</summary>
    private static string Canonical(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static JsonObject BehaviorJson(SimulationBehavior behavior)
    {
        var json = new JsonObject();
        if (behavior.Outcomes is { } outcomes)
        {
            json["outcomes"] = new JsonObject(outcomes.OrderBy(kv => kv.Key).Select(kv => KeyValuePair.Create(kv.Key.ToWire(), (JsonNode?)kv.Value)));
        }

        if (behavior.Script is { } script)
        {
            json["script"] = new JsonArray(script.Select(step => (JsonNode?)step.ToString()).ToArray());
        }

        if (behavior.Labels is { } labels)
        {
            json["labels"] = new JsonObject(labels.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        }

        if (behavior.Choices is { } choices)
        {
            json["choices"] = new JsonObject(choices.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        }

        if (behavior.ContextUpdates is { } updates)
        {
            json["context_updates"] = new JsonObject(updates.Select(kv => KeyValuePair.Create(kv.Key,
                (JsonNode?)new JsonObject(kv.Value.Select(u => KeyValuePair.Create(u.Key, ContextValues.ToJson(ContextValues.DeepCopy(u.Value))))))));
        }

        if (behavior.DurationMs is { } duration)
        {
            json["duration_ms"] = duration;
        }

        if (behavior.Metrics is { } metrics)
        {
            json["metrics"] = new JsonObject(metrics.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.ToJson())));
        }

        if (behavior.CostUsd is { } cost)
        {
            json["cost"] = cost;
        }

        return json;
    }
}

/// <summary>A node's effective behavior: its own fields merged with the defaults for its handler type.</summary>
internal sealed record ResolvedBehavior(
    IReadOnlyDictionary<StageStatus, double> Outcomes,
    IReadOnlyList<SimulationScriptStep> Script,
    IReadOnlyDictionary<string, double>? Labels,
    IReadOnlyDictionary<string, double>? Choices,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? ContextUpdates,
    int? DurationMs,
    IReadOnlyDictionary<string, SimulationMetric>? Metrics = null,
    double? CostUsd = null)
{
    /// <summary>
    /// The metrics an execution reports for <paramref name="status"/>: one draw per metric (purpose <c>metric:&lt;name&gt;</c>),
    /// so a sample replays exactly.
    /// </summary>
    public IReadOnlyDictionary<string, double> DrawMetrics(SimulationSample sample, string nodeId, int execution, StageStatus status)
    {
        var drawn = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, metric) in Metrics ?? new Dictionary<string, SimulationMetric>())
        {
            if (metric.Draw(status, sample.Draw(nodeId, execution, "metric:" + name)) is { } value)
            {
                drawn[name] = value;
            }
        }

        return drawn;
    }

    /// <summary>The configured context updates for <paramref name="status"/> (the <c>*</c> entry first, then the status entry).</summary>
    public IEnumerable<KeyValuePair<string, object?>> UpdatesFor(StageStatus status)
    {
        if (ContextUpdates is null)
        {
            return [];
        }

        var any = ContextUpdates.GetValueOrDefault(SimulationScenario.AnyStatus) ?? new Dictionary<string, object?>();
        var specific = ContextUpdates.GetValueOrDefault(status.ToWire()) ?? new Dictionary<string, object?>();
        return any.Concat(specific).Select(kv => KeyValuePair.Create(kv.Key, ContextValues.DeepCopy(kv.Value)));
    }
}

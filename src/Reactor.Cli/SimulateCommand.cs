using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Server.Studio;
using Reactor.Simulation;
using Reactor.Validation;

namespace Reactor.Cli;

/// <summary>
/// <c>reactor simulate</c>: the Studio's wind tunnel from the command line. Flies a pipeline many times through the real
/// engine with simulated stage outcomes (zero tokens) and prints the readiness gauge, failure modes with replayable
/// flights, hot spots, and simulated time and cost; <c>--min-success</c> makes it a CI gate. Also hosts the happy-path
/// check of <c>reactor validate --simulate</c>.
/// </summary>
internal static class SimulateCommand
{
    /// <summary>Runs of the happy-path check of <c>reactor validate --simulate</c>.</summary>
    internal const int HappyPathRuns = 200;

    /// <summary>Seed of the happy-path check (fixed, so the check is reproducible in CI).</summary>
    internal const long HappyPathSeed = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static Command Create()
    {
        var presets = string.Join(", ", SimulationPreset.All.Select(p => p.Name));
        var file = new Argument<string>("file") { Description = "DOT pipeline file ('-' reads standard input)" };
        var runs = new Option<int>("--runs", "-n") { Description = $"Simulated flights (1 to {MonteCarloOptions.MaxRuns})", DefaultValueFactory = _ => 1000 };
        var scenario = new Option<string>("--scenario") { Description = $"Scenario: a preset ({presets}) or a scenario JSON file (it may start from a \"preset\")", DefaultValueFactory = _ => SimulationPreset.Realistic };
        var seed = new Option<long?>("--seed") { Description = "Random seed (default: the scenario's, else random; reported so any flight can be replayed)" };
        var maxSteps = new Option<int>("--max-steps") { Description = $"Give a flight up after this many stage executions (1 to {MonteCarloOptions.MaxStepsCap})", DefaultValueFactory = _ => 200 };
        var minSuccess = new Option<string?>("--min-success") { Description = "Exit with 1 when the clean landing rate is below this share (0.95 or 95%)" };
        var trace = new Option<int?>("--trace") { Description = "Replay one flight (its run index, with the report's --seed) event by event instead of flying many" };
        var json = new Option<bool>("--json") { Description = "Print the report (or trace) as JSON, as the server's simulate API returns it" };
        var noColor = new Option<bool>("--no-color") { Description = "Disable colored output" };
        var context = new RunOptionSet();
        var command = new Command("simulate", "Fly a pipeline in the wind tunnel: many simulated runs, zero tokens") { file, runs, scenario, seed, maxSteps, minSuccess, trace, json, noColor, context.Set };

        runs.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > MonteCarloOptions.MaxRuns)
            {
                r.AddError($"--runs must be between 1 and {MonteCarloOptions.MaxRuns}.");
            }
        });
        maxSteps.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > MonteCarloOptions.MaxStepsCap)
            {
                r.AddError($"--max-steps must be between 1 and {MonteCarloOptions.MaxStepsCap}.");
            }
        });
        minSuccess.Validators.Add(r =>
        {
            if (!TryParseShare(r.GetValueOrDefault<string?>(), out _))
            {
                r.AddError("--min-success must be a share between 0 and 1 (0.95) or a percentage (95%).");
            }
        });
        trace.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 0)
            {
                r.AddError("--trace must be a run index (0 or more).");
            }
        });

        command.SetAction(async (result, cancellationToken) =>
        {
            var asJson = result.GetValue(json);
            var color = !result.GetValue(noColor) && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
            var path = result.GetValue(file)!;

            // Input: the pipeline, validated with the CLI runner's rules, then the scenario and the initial context.
            Graph graph;
            PreparedPipeline prepared;
            try
            {
                graph = DotParser.Parse(await PipelineCommands.ReadSourceAsync(path, cancellationToken).ConfigureAwait(false));
                graph.SourcePath = path == "-" ? null : Path.GetFullPath(path);
                prepared = new PipelineRunner(new PipelineRunnerOptions()).Prepare(graph);
            }
            catch (Exception ex) when (ex is IOException or DotParseException)
            {
                return await FailAsync(asJson, ex.Message, []).ConfigureAwait(false);
            }

            if (prepared.HasErrors)
            {
                return await FailAsync(asJson, "The pipeline has validation errors.", prepared.Diagnostics).ConfigureAwait(false);
            }

            var scenarioName = result.GetValue(scenario)!;
            var (resolved, scenarioError) = await ResolveScenarioAsync(scenarioName, prepared.Graph, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return await FailAsync(asJson, scenarioError!, []).ConfigureAwait(false);
            }

            Dictionary<string, object?> initialContext;
            try
            {
                initialContext = context.InitialContext(result);
            }
            catch (FormatException ex)
            {
                return await FailAsync(asJson, ex.Message, []).ConfigureAwait(false);
            }

            var options = new MonteCarloOptions
            {
                Runs = result.GetValue(runs),
                MaxSteps = result.GetValue(maxSteps),
                Seed = result.GetValue(seed),
                InitialContext = initialContext.Count > 0 ? initialContext : null,
            };
            var label = SimulationPreset.Find(scenarioName)?.Name ?? Path.GetFileName(scenarioName);

            try
            {
                if (result.GetValue(trace) is { } runIndex)
                {
                    var traceSeed = options.Seed ?? resolved.Seed ?? SimulationSample.NewSeed();
                    var replay = await SimulationTrace.RunAsync(prepared.Graph, resolved, traceSeed, runIndex, options, cancellationToken).ConfigureAwait(false);
                    Console.Out.Write(asJson ? replay.ToJson().ToJsonString(Indented) + Environment.NewLine : FormatTrace(replay, prepared.Graph.Name, label));
                    return ExitCodes.Success;
                }

                if (!asJson)
                {
                    PrintLintWarnings(prepared.Diagnostics);
                }

                var report = await MonteCarloSimulator.RunAsync(prepared.Graph, resolved, options, cancellationToken).ConfigureAwait(false);
                TryParseShare(result.GetValue(minSuccess), out var min);
                if (asJson)
                {
                    Console.Out.WriteLine(SimulationEndpoints.ReportJson(report, prepared).ToJsonString(Indented));
                }
                else
                {
                    var replayCommand = ReplayCommand(path, scenarioName, report.Seed, options.MaxSteps, result.GetValue(context.Set) ?? []);
                    var text = SimulationReportText.Format(report, new SimulationTextOptions
                    {
                        Pipeline = prepared.Graph.Name,
                        Scenario = label,
                        MinSuccess = min,
                        ReplayCommand = replayCommand,
                    });
                    Console.Out.Write(color ? Colorize(text) : text);
                }

                return min is { } required && report.CleanSuccessRate < required ? ExitCodes.PipelineFailed : ExitCodes.Success;
            }
            catch (ValidationException ex)
            {
                return await FailAsync(asJson, "The pipeline has validation errors.", ex.Diagnostics).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
                return ExitCodes.Cancelled;
            }
        });
        return command;
    }

    /// <summary>
    /// The happy-path check of <c>reactor validate --simulate</c>: <see cref="HappyPathRuns"/> flights under the
    /// <c>happy</c> preset (every stage succeeds; labeled routes and gate options chosen evenly) must all land.
    /// </summary>
    internal static async Task<HappyPathCheck> CheckHappyPathAsync(Graph graph, CancellationToken cancellationToken)
    {
        var scenario = SimulationPreset.Find(SimulationPreset.Happy)!.ToScenario(graph);
        var report = await MonteCarloSimulator.RunAsync(graph, scenario, new MonteCarloOptions { Runs = HappyPathRuns, MaxSteps = 500, Seed = HappyPathSeed }, cancellationToken).ConfigureAwait(false);
        return new HappyPathCheck(report);
    }

    /// <summary>The result of the happy-path check.</summary>
    internal sealed record HappyPathCheck(SimulationReport Report)
    {
        public bool Passed => Report.CleanSuccessRate >= 1;

        public JsonObject ToJson() => new()
        {
            ["scenario"] = SimulationPreset.Happy,
            ["runs"] = Report.Runs,
            ["seed"] = Report.Seed,
            ["max_steps"] = Report.MaxSteps,
            ["passed"] = Passed,
            ["clean_success_rate"] = Math.Round(Report.CleanSuccessRate, 4),
            ["outcomes"] = Report.ToJson()["outcomes"]!.DeepClone(),
            ["failed_stage_landings"] = Report.FailedStageLandings,
            ["failures"] = new JsonArray(Report.Failures.Select(f => (JsonNode?)f.ToJson()).ToArray()),
        };

        /// <summary>One line (two when it fails: what goes wrong and how to replay it).</summary>
        public string Describe(string file)
        {
            var runs = Report.Runs.ToString(CultureInfo.InvariantCulture);
            if (Passed)
            {
                return $"✔ happy path: all {runs} simulated flights land (every stage succeeding)";
            }

            var down = Report.Runs - (Report.Outcomes.GetValueOrDefault(SimulationOutcomeKinds.Success) - Report.FailedStageLandings);
            var line = $"✖ happy path: {down.ToString(CultureInfo.InvariantCulture)} of {runs} simulated flights do not land cleanly even with every stage succeeding";
            if (Report.Failures.FirstOrDefault() is { } failure)
            {
                var where = failure.LastNode is { } node ? $" at {node}" : "";
                line += $"\n    {SimulationReportText.KindLabels.GetValueOrDefault(failure.Kind, failure.Kind)}{where}: {failure.Reason}" +
                        $"\n    replay: reactor simulate {Quote(file)} --scenario happy --seed {HappyPathSeed.ToString(CultureInfo.InvariantCulture)} --max-steps 500 --trace {failure.Sample.RunIndex.ToString(CultureInfo.InvariantCulture)}";
            }

            return line;
        }
    }

    /// <summary>A preset name, else a scenario JSON file (resolved against the graph).</summary>
    private static async Task<(SimulationScenario? Scenario, string? Error)> ResolveScenarioAsync(string name, Graph graph, CancellationToken cancellationToken)
    {
        JsonNode? json;
        if (SimulationPreset.Find(name) is not null)
        {
            json = JsonValue.Create(name);
        }
        else if (File.Exists(name))
        {
            try
            {
                json = JsonNode.Parse(await File.ReadAllTextAsync(name, cancellationToken).ConfigureAwait(false));
            }
            catch (JsonException ex)
            {
                return (null, $"The scenario file {name} is not valid JSON: {ex.Message}");
            }

            if (json is not JsonObject)
            {
                return (null, $"The scenario file {name} must contain a JSON object.");
            }
        }
        else
        {
            return (null, $"Unknown scenario '{name}': not a preset ({string.Join(", ", SimulationPreset.All.Select(p => p.Name))}) or an existing file.");
        }

        if (SimulationScenario.TryResolve(json, graph, out var scenario, out var errors))
        {
            return (scenario, null);
        }

        return (null, $"The scenario {name} is invalid:\n" + string.Join("\n", errors.Select(e => "  " + e)));
    }

    private static async Task<int> FailAsync(bool json, string error, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (json)
        {
            Console.Out.WriteLine(new JsonObject
            {
                ["error"] = error,
                ["diagnostics"] = new JsonArray(diagnostics.Select(d => (JsonNode?)new JsonObject
                {
                    ["rule"] = d.Rule,
                    ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                    ["message"] = d.Message,
                    ["node_id"] = d.NodeId,
                    ["fix"] = d.Fix,
                }).ToArray()),
            }.ToJsonString(Indented));
        }
        else
        {
            await Console.Error.WriteLineAsync("✖ " + error).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics.Where(d => d.Severity == Severity.Error))
            {
                await Console.Error.WriteLineAsync($"  ✖ [{diagnostic.Rule}] {diagnostic.Message}{Location(diagnostic)}").ConfigureAwait(false);
            }
        }

        return ExitCodes.InvalidInput;
    }

    private static void PrintLintWarnings(IReadOnlyList<Diagnostic> diagnostics)
    {
        var warnings = diagnostics.Where(d => d.Severity == Severity.Warning).ToList();
        foreach (var warning in warnings)
        {
            Console.Out.WriteLine($"⚠ warning [{warning.Rule}] {warning.Message}{Location(warning)}");
        }

        if (warnings.Count > 0)
        {
            Console.Out.WriteLine();
        }
    }

    private static string Location(Diagnostic diagnostic) =>
        diagnostic.NodeId is { } node ? $" ({node})" : diagnostic.Edge is { } edge ? $" ({edge.From} -> {edge.To})" : "";

    /// <summary>The flight event by event: decisions, outcomes, routes, failures that flow on, metrics, and how it ended.</summary>
    private static string FormatTrace(SimulationTrace trace, string pipeline, string scenario)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Flight {trace.RunIndex.ToString(CultureInfo.InvariantCulture)} of {pipeline} · {scenario} scenario · seed {trace.Seed.ToString(CultureInfo.InvariantCulture)}");
        text.AppendLine();
        foreach (var e in trace.Events)
        {
            var line = e switch
            {
                StageStarted s => $"▶ {s.NodeId}{(s.Attempt > 1 ? $" (attempt {s.Attempt.ToString(CultureInfo.InvariantCulture)})" : "")}",
                StageLog l when l.Message.StartsWith("[Simulated]", StringComparison.Ordinal) => $"    {l.Message}",
                StageCompleted c => $"    → {c.Status}",
                StageFailed f => $"    ✖ {f.Error}{(f.WillRetry ? " (will retry)" : "")}",
                MetricsRecorded m => "    metrics " + string.Join(", ", m.Metrics.Select(kv => $"{kv.Key}={kv.Value.ToString("0.####", CultureInfo.InvariantCulture)}")),
                FailedStageContinued f => $"  ⚠ {f.NodeId} failed; its failure flows on to {f.To} along an unconditional edge",
                EdgeSelected s => $"  {s.From} → {s.To}{(s.Label.Length > 0 ? $"  (\"{s.Label}\")" : "")}",
                GoalGateUnsatisfied g => $"  ⚠ {g.Description}",
                ParallelStarted or ParallelCompleted => "  " + e.Description,
                _ => null,
            };
            if (line is not null)
            {
                text.AppendLine(line);
            }
        }

        text.AppendLine();
        var verdict = trace.Kind == SimulationOutcomeKinds.Success
            ? trace.FailedStages.Count > 0 ? $"landed with failed stages ({string.Join(", ", trace.FailedStages)})" : "landed clean"
            : $"{SimulationReportText.KindLabels.GetValueOrDefault(trace.Kind, trace.Kind)}: {trace.FailureReason}";
        text.AppendLine($"Result: {verdict} · {trace.Steps.ToString(CultureInfo.InvariantCulture)} stage executions" +
                        (trace.SimulatedDurationMs > 0 ? $" · {SimulationReportText.Duration(trace.SimulatedDurationMs)}" : "") +
                        (trace.SimulatedCostUsd > 0 ? $" · {SimulationReportText.Money(trace.SimulatedCostUsd)}" : ""));
        return text.ToString();
    }

    /// <summary>Colors the verdict and readiness markers.</summary>
    private static string Colorize(string text) => string.Join('\n', text.Split('\n').Select(line =>
        line.StartsWith('✔') ? $"\u001b[32m{line}\u001b[0m"
        : line.StartsWith('✖') ? $"\u001b[31m{line}\u001b[0m"
        : line.StartsWith("  ⚠", StringComparison.Ordinal) || line.StartsWith('⚠') ? $"\u001b[33m{line}\u001b[0m"
        : line));

    /// <summary>Parses <c>0.95</c> or <c>95%</c>; null input is valid (no threshold).</summary>
    internal static bool TryParseShare(string? text, out double? share)
    {
        share = null;
        if (text is null)
        {
            return true;
        }

        var trimmed = text.Trim();
        var percent = trimmed.EndsWith('%');
        if (!double.TryParse(percent ? trimmed[..^1] : trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return false;
        }

        value = percent ? value / 100 : value;
        if (value is < 0 or > 1)
        {
            return false;
        }

        share = value;
        return true;
    }

    /// <summary>
    /// The command that replays one flight of a report (<c>{run}</c> stands for its run index): the same pipeline, scenario,
    /// seed, step limit, and initial context — a flight whose routes read <c>context.*</c> differs without the <c>--set</c> values.
    /// </summary>
    internal static string ReplayCommand(string path, string scenario, long seed, int maxSteps, IEnumerable<string> sets) =>
        $"reactor simulate {Quote(path)} --scenario {Quote(scenario)} --seed {seed.ToString(CultureInfo.InvariantCulture)}" +
        (maxSteps != 200 ? $" --max-steps {maxSteps.ToString(CultureInfo.InvariantCulture)}" : "") +
        string.Concat(sets.Select(pair => " --set " + Quote(pair))) +
        " --trace {run}";

    private static string Quote(string value) => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '/' or '\\' or '-' or '_' or ':') ? value : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

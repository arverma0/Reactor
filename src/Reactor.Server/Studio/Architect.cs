using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Simulation;
using Reactor.Validation;

namespace Reactor.Server.Studio;

/// <summary>
/// The Studio Architect (<c>POST /studio/api/architect</c>, <c>GET /studio/api/architect/{id}</c>): drafts a pipeline from a
/// brief, revises the current one, or critiques it against evidence (the flight record, wind tunnel results). The
/// Architect is itself a Reactor pipeline (<c>architect.dot</c>): a <c>studio.architect</c> stage asks the server's backend
/// for a design (a rationale and one fenced DOT block), and a <c>studio.check</c> stage checks it with the parser, the
/// validator, and the wind tunnel, sending its findings back for a repair until the design passes or
/// <see cref="MaxDrafts"/> drafts have failed. Architect runs are ordinary server runs (listed, streamed, cancellable)
/// whose working directory is a scratch directory under <c>.reactor/studio/architect</c>. A workspace may override the
/// pipeline and the guide with <c>.reactor/studio/architect.dot</c> and <c>.reactor/studio/architect.md</c>.
/// </summary>
public static partial class ArchitectApi
{
    /// <summary>Handler type of the design stage.</summary>
    public const string DesignType = "studio.architect";

    /// <summary>Handler type of the check stage.</summary>
    public const string CheckType = "studio.check";

    /// <summary>Handler type of the self-review stage (the model critiques a passing draft against the brief).</summary>
    public const string ReviewType = "studio.review";

    /// <summary>Drafts (the first and its repairs) before the Architect gives up.</summary>
    public const int MaxDrafts = 3;

    internal const string TemplateResource = "Reactor.Server.studio-architect.dot";
    internal const string GuideResource = "Reactor.Server.studio-architect.md";
    internal const int MaxBriefChars = 20_000;
    internal const int MaxEvidenceChars = 100_000;

    internal const string Mode = "architect.mode";
    internal const string Brief = "architect.brief";
    internal const string Current = "architect.current";
    internal const string Evidence = "architect.evidence";
    internal const string Models = "architect.models";
    internal const string Proposal = "architect.proposal";
    internal const string Rationale = "architect.rationale";
    internal const string Attempt = "architect.attempt";
    internal const string Valid = "architect.valid";
    internal const string Problems = "architect.problems";
    internal const string Warnings = "architect.warnings";
    internal const string Forecast = "architect.forecast";
    internal const string Feedback = "architect.feedback";
    internal const string GiveUp = "architect.give_up";
    internal const string Review = "architect.review";

    /// <summary>
    /// The draft that passed the checker before the self-review sent it back (<c>architect.checked.proposal</c>, <c>.rationale</c>,
    /// <c>.forecast</c>, <c>.warnings</c>, <c>.attempt</c>, <c>.changes</c>): restored when the revision cannot pass the checker
    /// before the drafts run out, so a review never turns a checked draft into a failed run.
    /// </summary>
    internal const string Checked = "architect.checked";

    /// <summary>Registers the Architect's handlers on the server's runner and maps its endpoints.</summary>
    public static WebApplication MapArchitectApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var manager = app.Services.GetRequiredService<PipelineRunManager>();
        manager.Runner.Handlers.Register(DesignType, new ArchitectDesignHandler(manager));
        manager.Runner.Handlers.Register(CheckType, new ArchitectCheckHandler(manager));
        manager.Runner.Handlers.Register(ReviewType, new ArchitectReviewHandler());
        app.Services.GetRequiredService<StudioFeatures>().Add("architect");
        var api = app.MapGroup("/studio/api");
        api.MapPost("/architect", StartAsync);
        api.MapGet("/architect/{id}", Get);
        return app;
    }

    private static async Task<IResult> StartAsync(HttpContext http, PipelineRunManager manager)
    {
        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = $"The body is not valid JSON: {ex.Message}" });
        }

        if (body is null)
        {
            return Results.BadRequest(new { error = "Expected a JSON object." });
        }

        var mode = Str(body["mode"]) ?? "draft";
        var brief = (Str(body["brief"]) ?? "").Trim();
        var current = Str(body["dot"]) ?? "";
        var evidence = (Str(body["evidence"]) ?? "").Trim();
        var model = Str(body["model"]);
        var effort = Str(body["reasoning_effort"]);
        var models = body["models"] is JsonArray list ? list.Select(Str).OfType<string>().ToList() : [];
        string? error = mode switch
        {
            not ("draft" or "revise" or "critique") => "'mode' must be draft, revise, or critique.",
            _ when mode != "critique" && brief.Length == 0 => "Describe what the pipeline should do ('brief').",
            _ when mode != "draft" && current.Trim().Length == 0 => "Revising or critiquing needs the current pipeline ('dot').",
            _ when brief.Length > MaxBriefChars => $"The brief exceeds {MaxBriefChars} characters.",
            _ when evidence.Length > MaxEvidenceChars => $"The evidence exceeds {MaxEvidenceChars} characters.",
            _ when Encoding.UTF8.GetByteCount(current) > manager.Options.MaxDotBytes => $"The pipeline exceeds {manager.Options.MaxDotBytes} bytes.",
            _ when model is not null && !TokenPattern().IsMatch(model) => $"'{model}' is not a model id.",
            _ when effort is not null && !TokenPattern().IsMatch(effort) => $"'{effort}' is not a reasoning effort.",
            _ when models.Count > 500 || models.Any(m => !TokenPattern().IsMatch(m)) => "'models' must be a list of model ids.",
            _ => null,
        };
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        if (manager.Options.Backend is null)
        {
            return Results.Conflict(new
            {
                error = "The Architect needs an LLM backend, and this server simulates stages. Restart it with a real backend, " +
                        "for example: reactor studio --backend copilot",
            });
        }

        var template = LoadTemplate(manager.Options.WorkingDirectory);
        if (model is not null)
        {
            template = WithModel(template, model, effort);
        }

        var scratch = Path.Combine(
            Path.GetFullPath(manager.Options.WorkingDirectory),
            ".reactor",
            "studio",
            "architect",
            $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}");
        var result = manager.Start(new StartRequest
        {
            Dot = template,
            Context = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [Mode] = mode,
                [Brief] = brief,
                [Current] = current,
                [Evidence] = evidence,
                [Models] = string.Join(", ", models),
                [Attempt] = 0L,
            },
            ServerWorkingDirectory = scratch,
            Studio = new JsonObject { ["architect"] = new JsonObject { ["mode"] = mode, ["brief"] = Clip(brief, 2000), ["model"] = model } },
        });
        if (result.Run is not { } run)
        {
            return Results.BadRequest(new { error = $"The Architect pipeline could not start: {result.Error}" });
        }

        return Results.Json(
            new
            {
                id = run.Id,
                status = run.Status,
                name = run.Name,
                logs_root = run.LogsRoot,
                links = new { self = $"/studio/api/architect/{run.Id}", run = $"/pipelines/{run.Id}", events = $"/pipelines/{run.Id}/events" },
            },
            statusCode: StatusCodes.Status201Created);
    }

    private static IResult Get(string id, PipelineRunManager manager)
    {
        if (manager.Get(id) is not { } run || !run.Graph.Nodes.Values.Any(n => n.Type == DesignType))
        {
            return Results.NotFound(new { error = $"Unknown Architect run '{id}'." });
        }

        var context = ReadContext(run);
        string? Text(string key) => context.TryGetValue(key, out var value) && value is not null ? ContextValues.ToDisplayString(value) : null;
        JsonNode? Json(string key) => context.TryGetValue(key, out var value) && value is not null ? ContextValues.ToJson(value) : null;
        var proposal = Text(Proposal);
        var checkedDraft = context.ContainsKey(Valid);
        var json = new JsonObject
        {
            ["id"] = run.Id,
            ["status"] = run.Status,
            ["current_node"] = run.CurrentNode,
            ["mode"] = Text(Mode),
            ["attempt"] = context.TryGetValue(Attempt, out var attempt) ? ToInt(attempt) : 0,
            ["max_drafts"] = MaxDrafts,
            ["rationale"] = Text(Rationale),
            ["proposal"] = string.IsNullOrWhiteSpace(proposal)
                ? null
                : new JsonObject
                {
                    ["dot"] = proposal,
                    ["checked"] = checkedDraft,
                    ["valid"] = checkedDraft && context.TryGetValue(Valid, out var valid) && valid is true,
                    ["problems"] = Json(Problems) ?? new JsonArray(),
                    ["warnings"] = Json(Warnings) ?? new JsonArray(),
                    ["forecast"] = Json(Forecast),
                },
            ["feedback"] = Text(Feedback),
            ["review"] = Text(Review),
            ["error"] = run.Error ?? (run.Result is { Status: StageStatus.Fail } failed ? failed.FailureReason : null),
            ["links"] = new JsonObject { ["run"] = $"/pipelines/{run.Id}", ["events"] = $"/pipelines/{run.Id}/events" },
        };
        return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
    }

    /// <summary>The run's context: its final context once finished, else as of its latest checkpoint (saved after every stage).</summary>
    private static IReadOnlyDictionary<string, object?> ReadContext(ManagedRun run)
    {
        if (run.Result is { Context.Count: > 0 } result)
        {
            return result.Context;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Checkpoint.TryLoadFromRunDirectory(run.CurrentLogsRoot)?.ContextValues ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                // The checkpoint is being replaced; try again.
                Thread.Sleep(20);
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>The Architect pipeline: the workspace's <c>.reactor/studio/architect.dot</c>, else the built-in one.</summary>
    internal static string LoadTemplate(string workingDirectory) => LoadText(workingDirectory, "architect.dot", TemplateResource);

    /// <summary>The Architect's guide (prepended to every design prompt): the workspace's override, else the built-in one.</summary>
    internal static string LoadGuide(string workingDirectory) => LoadText(workingDirectory, "architect.md", GuideResource);

    private static string LoadText(string workingDirectory, string fileName, string resource)
    {
        var overridePath = Path.Combine(Path.GetFullPath(workingDirectory), ".reactor", "studio", fileName);
        if (File.Exists(overridePath))
        {
            return File.ReadAllText(overridePath);
        }

        using var stream = typeof(ArchitectApi).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resource}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Sets the model (and reasoning effort) of the template's design and review stages by redeclaring them.</summary>
    internal static string WithModel(string template, string model, string? effort)
    {
        var graph = DotParser.Parse(template);
        var lines = new StringBuilder();
        foreach (var node in graph.Nodes.Values.Where(n => n.Type is DesignType or ReviewType))
        {
            lines.Append("    ").Append(node.Id).Append(" [llm_model=\"").Append(model).Append('"');
            if (effort is not null)
            {
                lines.Append(", reasoning_effort=\"").Append(effort).Append('"');
            }

            lines.AppendLine("]");
        }

        var end = template.LastIndexOf('}');
        return end < 0 ? template : template[..end] + lines + template[end..];
    }

    /// <summary>The full prompt of a design stage: the guide, then the request assembled from the context.</summary>
    internal static string ComposePrompt(string task, string guide, PipelineContext context)
    {
        var mode = context.GetString(Mode, "draft");
        var brief = context.GetString(Brief);
        var current = context.GetString(Current);
        var evidence = context.GetString(Evidence);
        var models = context.GetString(Models);
        var attempt = context.TryGet(Attempt, out var count) ? ToInt(count) : 0;
        var previous = context.GetString(Proposal);
        var feedback = context.GetString(Feedback);

        var sb = new StringBuilder();
        sb.AppendLine(guide.Trim()).AppendLine().AppendLine("---").AppendLine().AppendLine("# Request").AppendLine();
        if (task.Trim().Length > 0)
        {
            sb.AppendLine(task.Trim()).AppendLine();
        }

        sb.AppendLine(mode switch
        {
            "revise" => "Revise the current pipeline as the brief asks. Change only what the request needs.",
            "critique" => "Critique the current pipeline using the evidence, then propose a revision that fixes what matters most. " +
                          "Begin your rationale with a \"## Findings\" section: the most important problems, each tied to the evidence " +
                          "(numbers from the flight record or the wind tunnel), most important first. Then a \"## Changes\" section.",
            _ => "Design a new pipeline for the brief.",
        }).AppendLine();

        sb.AppendLine("## Brief").AppendLine(brief.Length > 0 ? brief : "(none: focus on what the evidence shows)").AppendLine();
        if (current.Trim().Length > 0)
        {
            sb.AppendLine("## Current pipeline").AppendLine("```dot").AppendLine(current.Trim()).AppendLine("```");
            sb.AppendLine("Keep its graph name and the ids of stages that keep their role: the run history, the layout, and the flight record are keyed by them.");
            sb.AppendLine();
        }

        if (evidence.Length > 0)
        {
            sb.AppendLine("## Evidence").AppendLine(evidence).AppendLine();
        }

        sb.AppendLine("## Models available").AppendLine(models.Length > 0 ? models : "(not listed: leave models out)").AppendLine();
        if (attempt > 0 && feedback.Length > 0)
        {
            if (previous.Trim().Length > 0)
            {
                sb.AppendLine("## Your previous draft").AppendLine("```dot").AppendLine(previous.Trim()).AppendLine("```").AppendLine();
            }

            sb.AppendLine("## What the checker found").AppendLine(feedback.Trim()).AppendLine();
            sb.AppendLine("Fix every finding. Answer again in the same format, with the complete corrected pipeline.");
        }

        return sb.ToString().TrimEnd().ReplaceLineEndings("\n");
    }

    /// <summary>
    /// The self-review prompt: the model that drafted the pipeline checks it against the brief — the contracts and quality bar
    /// the brief states, its budget, and the design rules a checker cannot judge — and answers PASS or REVISE with changes.
    /// </summary>
    internal static string ComposeReviewPrompt(PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sb = new StringBuilder();
        sb.AppendLine("You are reviewing a Reactor pipeline (an agent factory written in Graphviz DOT) that you designed, before it is");
        sb.AppendLine("handed to the person who asked for it. It already parses, validates, and lands in the wind tunnel; judge whether");
        sb.AppendLine("it does what the brief asks. Check, in this order:");
        sb.AppendLine();
        sb.AppendLine("1. Contracts. Every path, file name, script, command, tool, and format the brief names appears in the prompts and");
        sb.AppendLine("   tool commands exactly as written; no prompt contradicts the brief or another prompt.");
        sb.AppendLine("2. The quality bar. The brief's central requirement (what would make the person call the result excellent) has a");
        sb.AppendLine("   stage that produces it and a critic or check that measures it, with routing on the result.");
        sb.AppendLine("3. Budget. If the brief states a time or cost budget for the run, the forecast below fits it, or the rationale says");
        sb.AppendLine("   why not; a `Forecast:` warning below compares the forecast with a budget read from the brief's wording — decide");
        sb.AppendLine("   whether that wording is the run's budget (not a requirement of the product) before acting on it.");
        sb.AppendLine("4. Loops and defaults. Every rework loop is bounded by max_visits or a visits.<node> condition and escalates");
        sb.AppendLine("   sensibly; a stage choosing between labeled routes defaults (highest weight) to the conservative one.");
        sb.AppendLine("5. Evidence and time. Fixers read the failing report; timeouts fit the work (building takes hours, not minutes).");
        sb.AppendLine("6. Proportion. Human feedback takes a short path (apply, verify, back to the person), not the full review machinery.");
        sb.AppendLine();
        sb.AppendLine("Answer with exactly one line `VERDICT: PASS` when nothing important is wrong, or `VERDICT: REVISE` followed by a");
        sb.AppendLine("numbered list of specific changes (which stage or edge, and what to change). Do not rewrite the pipeline, and do");
        sb.AppendLine("not create, read, or change files.");
        sb.AppendLine().AppendLine("## Brief").AppendLine(context.GetString(Brief) is { Length: > 0 } brief ? brief : "(none)").AppendLine();
        if (context.GetString(Rationale) is { Length: > 0 } rationale)
        {
            sb.AppendLine("## Your rationale").AppendLine(rationale).AppendLine();
        }

        sb.AppendLine("## The pipeline").AppendLine("```dot").AppendLine(context.GetString(Proposal).Trim()).AppendLine("```").AppendLine();
        if (context.Get(Forecast) is { } forecast)
        {
            sb.AppendLine("## Wind tunnel forecast (typical stage durations; real build stages often take longer)")
                .AppendLine("```json").AppendLine(ContextValues.ToJson(forecast)?.ToJsonString()).AppendLine("```").AppendLine();
        }

        if (context.Get(Warnings) is List<object?> { Count: > 0 } warnings)
        {
            sb.AppendLine("## Checker warnings").AppendJoin("\n", warnings.Select(w => "- " + ContextValues.ToDisplayString(w))).AppendLine().AppendLine();
        }

        return sb.ToString().TrimEnd().ReplaceLineEndings("\n");
    }

    /// <summary>The verdict of a self-review answer: REVISE with the requested changes, or PASS (also when no verdict is given).</summary>
    internal static (bool Revise, string Changes) ParseReview(string answer)
    {
        var lines = (answer ?? "").ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Markdown around the verdict is common: **VERDICT:** REVISE, **VERDICT**: REVISE, `VERDICT: REVISE`, ## VERDICT: …
            var match = VerdictPattern().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            if (!string.Equals(match.Groups["v"].Value, "REVISE", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "");
            }

            var changes = string.Join("\n", lines.Skip(i + 1)).Trim();
            return (true, changes.Length > 0 ? changes : "The review asked for a revision without listing changes.");
        }

        return (false, "");
    }

    /// <summary>Splits an answer into the pipeline (the last fenced block holding a digraph) and the rest (the rationale).</summary>
    internal static (string? Dot, string Rationale) SplitAnswer(string answer)
    {
        Match? best = null;
        foreach (Match match in FencePattern().Matches(answer))
        {
            if (DigraphPattern().IsMatch(match.Groups["body"].Value))
            {
                best = match;
            }
        }

        if (best is not null)
        {
            var rationale = (answer[..best.Index] + answer[(best.Index + best.Length)..]).Trim();
            return (best.Groups["body"].Value.Trim(), rationale);
        }

        // No fence: take a bare digraph with balanced braces.
        var start = DigraphPattern().Match(answer);
        if (!start.Success)
        {
            return (null, answer.Trim());
        }

        var depth = 0;
        for (var i = answer.IndexOf('{', start.Index); i >= 0 && i < answer.Length; i++)
        {
            depth += answer[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                var dot = answer[start.Index..(i + 1)];
                return (dot.Trim(), (answer[..start.Index] + answer[(i + 1)..]).Trim());
            }
        }

        return (null, answer.Trim());
    }

    /// <summary>The checker's verdict on a draft.</summary>
    internal sealed record CheckResult(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, Dictionary<string, object?>? Forecast)
    {
        public bool Passed => Problems.Count == 0;

        public string Feedback => string.Join("\n", Problems.Select(p => "- " + p).Concat(Warnings.Select(w => "- (warning) " + w)));
    }

    /// <summary>
    /// Checks a draft: it must parse and validate without errors (warnings are reported), and in the wind tunnel every
    /// simulated flight must land when every stage succeeds (the <c>happy</c> preset). A second pass under the
    /// <c>realistic</c> preset (agents succeed 90% of the time, tools 75%, typical stage durations) produces the forecast —
    /// landing judged on clean landings, plus time — and flags loops that do not converge and failures that flow onward.
    /// </summary>
    internal static async Task<CheckResult> CheckAsync(PipelineRunManager manager, string proposal, string mode, string current, CancellationToken cancellationToken, string brief = "")
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(proposal))
        {
            problems.Add("The answer had no ```dot code block with the complete pipeline.");
            return new CheckResult(problems, warnings, null);
        }

        Graph parsed;
        try
        {
            parsed = DotParser.Parse(proposal);
        }
        catch (DotParseException ex)
        {
            problems.Add($"DOT syntax error: {ex.Message}");
            return new CheckResult(problems, warnings, null);
        }

        var prepared = manager.Runner.Prepare(parsed);
        foreach (var diagnostic in prepared.Diagnostics)
        {
            var text = Describe(diagnostic);
            if (diagnostic.Severity == Severity.Error || DesignRules.Contains(diagnostic.Rule))
            {
                // The Architect's drafts must pass the flow-control rules a person would otherwise have to catch.
                problems.Add(text);
            }
            else if (diagnostic.Severity == Severity.Warning)
            {
                warnings.Add(text);
            }
        }

        warnings.AddRange(ContractFindings(brief, parsed));

        if (mode != "draft" && TryParse(current) is { } original && original.Name != parsed.Name)
        {
            warnings.Add($"The graph is renamed from '{original.Name}' to '{parsed.Name}'; its flight record is keyed by the name.");
        }

        if (prepared.HasErrors)
        {
            return new CheckResult(problems, warnings, null);
        }

        var custom = manager.CustomHandlerTypes();
        var happyScenario = SimulationPreset.Find(SimulationPreset.Happy)!.ToScenario(prepared.Graph);
        var happy = await MonteCarloSimulator.RunAsync(prepared.Graph, happyScenario, new MonteCarloOptions { Runs = 60, MaxSteps = 300, Seed = 1, CustomHandlerTypes = custom }, cancellationToken).ConfigureAwait(false);
        if (happy.CleanSuccessRate < 1)
        {
            var failure = happy.Failures.FirstOrDefault();
            var where = failure?.LastNode is { } last ? $", last stage '{last}'" : "";
            problems.Add(
                $"Wind tunnel: even with every stage succeeding, {Percent(1 - happy.CleanSuccessRate)} of simulated flights do not land " +
                $"({failure?.Kind ?? "fail"}: {failure?.Reason ?? "unknown"}{where}). Every route must lead to the exit.");
        }

        var realisticScenario = SimulationPreset.Find(SimulationPreset.Realistic)!.ToScenario(prepared.Graph);
        var realistic = await MonteCarloSimulator.RunAsync(prepared.Graph, realisticScenario, new MonteCarloOptions { Runs = 400, MaxSteps = 300, Seed = 7, CustomHandlerTypes = custom }, cancellationToken).ConfigureAwait(false);
        var stepLimit = realistic.Outcomes.GetValueOrDefault(SimulationOutcomeKinds.StepLimit) / (double)Math.Max(1, realistic.Runs);
        if (stepLimit > 0.05)
        {
            warnings.Add(
                $"Wind tunnel: with agents succeeding 90% of the time and tools 75%, {Percent(stepLimit)} of flights loop until the step limit. " +
                "Bound the loops with max_retries, goal gates and a retry_target, or an escape route.");
        }

        var dirtyShare = realistic.FailedStageLandings / (double)Math.Max(1, realistic.Runs);
        var failedStages = realistic.Nodes
            .Where(kv => kv.Value.FailedStageLandings > 0)
            .OrderByDescending(kv => kv.Value.FailedStageLandings)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        if (failedStages.Count > 0)
        {
            warnings.Add(
                $"Wind tunnel: {Percent(dirtyShare)} of realistic flights land with a failed stage behind them, because a failure flows on " +
                $"along an unconditional edge ({string.Join(", ", failedStages.Take(5).Select(kv => $"'{kv.Key}' in {Percent(kv.Value.FailedStageLandings / (double)realistic.Runs)}"))}). " +
                "Condition the onward edge on outcome=success and route failures explicitly (condition=\"outcome=fail\").");
        }

        var forecast = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["happy_path"] = Math.Round(happy.CleanSuccessRate, 4),
            ["realistic"] = Math.Round(realistic.CleanSuccessRate, 4),
            ["realistic_landed"] = Math.Round(realistic.SuccessRate, 4),
            ["failed_stage_landings"] = Math.Round(dirtyShare, 4),
            ["failed_stages"] = failedStages.Select(kv => (object?)kv.Key).ToList(),
            ["step_limit"] = Math.Round(stepLimit, 4),
            ["mean_steps"] = Math.Round(realistic.Steps.Mean, 1),
            ["p90_steps"] = (long)realistic.Steps.P90,
            ["mean_duration_ms"] = (long)Math.Round(realistic.SimulatedDuration.Mean),
            ["p90_duration_ms"] = realistic.SimulatedDuration.P90,
            ["stages"] = (long)prepared.Graph.Nodes.Count,
            ["routes"] = (long)prepared.Graph.Edges.Count,
            ["budget_hours"] = BriefBudgetHours(brief) is { } budget ? Math.Round(budget.Hours, 2) : null,
            ["failures"] = realistic.Failures.Take(3)
                .Select(f => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = f.Kind,
                    ["reason"] = f.Reason,
                    ["share"] = Math.Round(f.Count / (double)Math.Max(1, realistic.Runs), 4),
                    ["last_node"] = f.LastNode,
                })
                .ToList(),
        };
        if (BriefBudgetHours(brief) is { } hours)
        {
            // The budget is read from prose, so it may be a product requirement ("tests run in under 5 minutes") rather than
            // the run's: a warning, which the self-review weighs (it asks for a revision when the brief really meant it).
            var mean = TimeSpan.FromMilliseconds(realistic.SimulatedDuration.Mean);
            var p90 = TimeSpan.FromMilliseconds(realistic.SimulatedDuration.P90);
            var limit = TimeSpan.FromHours(hours.Hours);
            var said = $"the time budget the brief seems to state (\"{hours.Text}\")";
            if (mean > limit && !hours.Approximate)
            {
                warnings.Add(
                    $"Forecast: a typical flight takes {Hours(mean)} (p90 {Hours(p90)}) with the wind tunnel's typical stage durations, over {said}. " +
                    "If that is the run's budget, fewer or larger stages, fewer rework rounds (lower max_visits), or cheaper checks would fit it; if it cannot be met, say so in the rationale.");
            }
            else if (p90 > limit)
            {
                warnings.Add($"Forecast: a typical flight takes {Hours(mean)}, but the slowest 10% take {Hours(p90)} or more, over {said}.");
            }
        }

        return new CheckResult(problems, warnings, forecast);
    }

    /// <summary>
    /// Lint rules an Architect draft must pass even though they are warnings (or notes) for hand-written pipelines: failures
    /// that flow on silently, defaults decided by id order, loops without an engine-enforced bound, fixers sent back
    /// without the failing report, and broken visit limits.
    /// </summary>
    internal static readonly IReadOnlySet<string> DesignRules = new HashSet<string>(StringComparer.Ordinal)
    {
        "fail_flows_onward", "default_route", "unbounded_loop", "failure_evidence", "visit_limits",
    };

    /// <summary>
    /// Paths the brief names that the draft contradicts: the draft never mentions the brief's path but uses a sibling with
    /// another name (the brief says <c>qa/shots/</c>, every prompt says <c>qa/screenshots/</c>).
    /// </summary>
    internal static IReadOnlyList<string> ContractFindings(string brief, Graph draft)
    {
        if (string.IsNullOrWhiteSpace(brief))
        {
            return [];
        }

        var briefPaths = PathsIn(brief);
        if (briefPaths.Count == 0)
        {
            return [];
        }

        var uses = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var draftText = new StringBuilder();
        foreach (var node in draft.Nodes.Values)
        {
            foreach (var value in node.Attributes.Values)
            {
                draftText.AppendLine(value);
                foreach (var path in PathsIn(value))
                {
                    if (!uses.TryGetValue(path, out var stages))
                    {
                        uses[path] = stages = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    stages.Add(node.Id);
                }
            }
        }

        var text = Normalize(draftText.ToString());
        var findings = new List<string>();
        foreach (var expected in briefPaths)
        {
            if (text.Contains(expected, StringComparison.OrdinalIgnoreCase) || text.Contains(expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var siblings = uses.Keys
                .Where(used => !briefPaths.Contains(used) && Siblings(expected, used))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (siblings.Count > 0)
            {
                var where = string.Join("; ", siblings.Select(used => $"`{used}` ({string.Join(", ", uses[used])})"));
                findings.Add($"Contract: the brief names `{expected}`, but the draft never uses it and uses {where} instead. Use the brief's paths exactly.");
            }
        }

        return findings;
    }

    /// <summary>Relative paths in a text that look like contracts: a directory (<c>qa/shots/</c>), a file (<c>docs/PRODUCT.md</c>), or a deep path.</summary>
    internal static HashSet<string> PathsIn(string text)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PathPattern().Matches(Normalize(UrlPattern().Replace(text, " "))))
        {
            var path = match.Value.TrimStart('.', '/');
            var segments = path.TrimEnd('/').Split('/');
            var isDirectory = path.EndsWith('/');
            var isFile = segments[^1].Contains('.', StringComparison.Ordinal) && !segments[^1].EndsWith('.');
            if (segments.Length >= 2 && segments.All(s => s.Any(char.IsLetter)) && (isDirectory || isFile || segments.Length >= 3))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static bool Siblings(string expected, string used)
    {
        var a = expected.TrimEnd('/').Split('/');
        var b = used.TrimEnd('/').Split('/');
        if (a.Length != b.Length || string.Equals(a[^1], b[^1], StringComparison.OrdinalIgnoreCase) ||
            !a[..^1].SequenceEqual(b[..^1], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // Same kind: both directories, or files with the same extension.
        return expected.EndsWith('/') == used.EndsWith('/') &&
               string.Equals(Path.GetExtension(a[^1]), Path.GetExtension(b[^1]), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string text) => text.Replace('\\', '/');

    /// <summary>A time budget stated in the brief ("at most 3 hours", "under 90 minutes", "a few hours at most"), or null.</summary>
    internal static (double Hours, string Text, bool Approximate)? BriefBudgetHours(string brief)
    {
        if (string.IsNullOrWhiteSpace(brief))
        {
            return null;
        }

        foreach (var pattern in new[] { BudgetBeforePattern(), BudgetAfterPattern() })
        {
            var match = pattern.Match(brief);
            if (!match.Success)
            {
                continue;
            }

            var amount = match.Groups["n"].Value.ToLowerInvariant();
            var approximate = !double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
            if (approximate)
            {
                number = amount switch
                {
                    "an" or "a" or "one" => 1,
                    "a couple of" or "couple of" or "two" => 2,
                    "a few" or "few" or "three" => 3,
                    "four" => 4,
                    "five" => 5,
                    "six" => 6,
                    "eight" => 8,
                    "ten" => 10,
                    "twelve" => 12,
                    _ => 0,
                };
                approximate = amount.Contains("few", StringComparison.Ordinal) || amount.Contains("couple", StringComparison.Ordinal);
            }

            if (number <= 0)
            {
                continue;
            }

            var unit = match.Groups["u"].Value.ToLowerInvariant();
            var hours = unit.StartsWith('m') ? number / 60 : unit.StartsWith('d') ? number * 24 : number;
            return (hours, match.Value.Trim(), approximate);
        }

        return null;
    }

    private static string Hours(TimeSpan span) => span.TotalHours >= 1
        ? span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " h"
        : Math.Round(span.TotalMinutes).ToString("0", CultureInfo.InvariantCulture) + " min";

    private static string Describe(Diagnostic diagnostic)
    {
        var where = diagnostic.NodeId is { } node ? $"stage '{node}': " : diagnostic.Edge is { } edge ? $"edge {edge.From} -> {edge.To}: " : "";
        var fix = diagnostic.Fix is { Length: > 0 } hint ? $" (fix: {hint})" : "";
        return $"[{diagnostic.Rule}] {where}{diagnostic.Message}{fix}";
    }

    private static Graph? TryParse(string dot)
    {
        if (string.IsNullOrWhiteSpace(dot))
        {
            return null;
        }

        try
        {
            return DotParser.Parse(dot);
        }
        catch (DotParseException)
        {
            return null;
        }
    }

    private static string Percent(double share) => share.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "", StringComparison.Ordinal);

    internal static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        JsonValue json when json.TryGetValue<long>(out var l) => (int)l,
        _ => 0,
    };

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"```[ \t]*(?<lang>[A-Za-z0-9_-]*)[^\n]*\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"\b(strict\s+)?digraph\b", RegexOptions.CultureInvariant)]
    private static partial Regex DigraphPattern();

    [GeneratedRegex(@"(?<![\w.\-/])(?:\.{1,2}/)?[A-Za-z0-9_][A-Za-z0-9_.\-]*(?:/[A-Za-z0-9_][A-Za-z0-9_.\-]*)+/?", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.\-]*://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?:at most|under|within|no more than|less than|max(?:imum)?(?: of)?|budget(?: of| is)?|(?:finish(?:es|ed)?|done|ship(?:s|ped)?|deliver(?:s|ed)?|complete(?:s|d)?)\s+in)\s+(?:about\s+|roughly\s+)?(?<n>\d+(?:\.\d+)?|an?|one|two|three|four|five|six|eight|ten|twelve|a few|a couple of)\s*(?<u>hours?|hrs?|h|minutes?|mins?|days?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BudgetBeforePattern();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?|an?|one|two|three|four|five|six|eight|ten|twelve|a few|a couple of)\s*(?<u>hours?|hrs?|h|minutes?|mins?|days?)\s+(?:at most|max(?:imum)?|or less|tops)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BudgetAfterPattern();

    /// <summary>A self-review verdict line, with or without Markdown around it.</summary>
    [GeneratedRegex(@"^[\s*_`#>-]*VERDICT[\s*_`]*:[\s*_`]*(?<v>PASS|REVISE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerdictPattern();
}

/// <summary>
/// The Architect's design stage: composes the prompt (the guide plus the request from the <c>architect.*</c> context) and
/// sends it to the server's backend as is — unlike a codergen stage, without <c>$goal</c> expansion (the guide teaches the
/// variable) and without the status-file protocol (the answer is text). It writes the usual stage files
/// (<c>prompt.md</c>, <c>response.md</c>, <c>backend.json</c>, <c>status.json</c>), so the stage's cost shows in Flights.
/// The pipeline and the rationale of the answer go to <c>architect.proposal</c> and <c>architect.rationale</c>.
/// </summary>
internal sealed class ArchitectDesignHandler(PipelineRunManager manager) : INodeHandler
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);
        if (context.Run?.Backend is not { } backend)
        {
            return await FinishAsync(stageDir, Outcome.Fail("The Architect needs an LLM backend; this run simulates stages."), cancellationToken).ConfigureAwait(false);
        }

        var prompt = ArchitectApi.ComposePrompt(node.Prompt, ArchitectApi.LoadGuide(manager.Options.WorkingDirectory), context);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);
        var stage = node.Clone();
        stage[AttrKeys.Prompt] = prompt;
        var result = await backend.RunAsync(stage, prompt, context, cancellationToken).ConfigureAwait(false);
        if (result.Metadata.Count > 0)
        {
            var metadata = ContextValues.ToJson(ContextValues.Normalize(result.Metadata));
            await File.WriteAllTextAsync(Path.Combine(stageDir, "backend.json"), metadata?.ToJsonString(Indented) ?? "{}", cancellationToken).ConfigureAwait(false);
        }

        var answer = result.Text ?? "";
        if (answer.Length > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), answer, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome is { IsSuccessful: false } failed)
        {
            return await FinishAsync(stageDir, failed, cancellationToken).ConfigureAwait(false);
        }

        var (dot, rationale) = ArchitectApi.SplitAnswer(answer);
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["last_stage"] = node.Id,
            ["last_response"] = answer.Length <= CodergenHandler.LastResponseLength ? answer : answer[..CodergenHandler.LastResponseLength],
            [ArchitectApi.Proposal] = dot ?? "",
            [ArchitectApi.Rationale] = rationale,
        };
        var outcome = Outcome.Success(dot is null ? "The answer had no pipeline." : $"Drafted {dot.Length} characters of DOT.", updates);
        return await FinishAsync(stageDir, outcome, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Outcome> FinishAsync(string stageDir, Outcome outcome, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(stageDir, "status.json"), outcome.ToStatusJson().ToJsonString(Indented), cancellationToken).ConfigureAwait(false);
        return outcome;
    }
}

/// <summary>
/// The Architect's check stage: checks <c>architect.proposal</c> (see <see cref="ArchitectApi.CheckAsync"/>), records the
/// verdict and the forecast in the context, writes <c>proposal.dot</c> and <c>check.md</c> to its stage directory, and fails
/// with the findings (<c>architect.feedback</c>) so the pipeline routes back to the design stage, setting
/// <c>architect.give_up</c> after the last allowed draft.
/// </summary>
internal sealed class ArchitectCheckHandler(PipelineRunManager manager) : INodeHandler
{
    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        var attempt = (context.TryGet(ArchitectApi.Attempt, out var count) ? ArchitectApi.ToInt(count) : 0) + 1;
        var proposal = context.GetString(ArchitectApi.Proposal);
        var result = await ArchitectApi.CheckAsync(manager, proposal, context.GetString(ArchitectApi.Mode, "draft"), context.GetString(ArchitectApi.Current), cancellationToken, context.GetString(ArchitectApi.Brief)).ConfigureAwait(false);

        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);
        if (proposal.Length > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(stageDir, "proposal.dot"), proposal, cancellationToken).ConfigureAwait(false);
        }

        var report = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"# Draft {attempt}: {(result.Passed ? "passed" : "failed")}").AppendLine();
        if (result.Feedback.Length > 0)
        {
            report.AppendLine(result.Feedback).AppendLine();
        }

        if (result.Forecast is { } forecast)
        {
            report.AppendLine("```json").AppendLine(ContextValues.ToJson(forecast)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).AppendLine("```");
        }

        await File.WriteAllTextAsync(Path.Combine(stageDir, "check.md"), report.ToString(), cancellationToken).ConfigureAwait(false);

        var updates = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ArchitectApi.Attempt] = (long)attempt,
            [ArchitectApi.Valid] = result.Passed,
            [ArchitectApi.Problems] = result.Problems.Cast<object?>().ToList(),
            [ArchitectApi.Warnings] = result.Warnings.Cast<object?>().ToList(),
            [ArchitectApi.Forecast] = result.Forecast,
            [ArchitectApi.Feedback] = result.Feedback,
        };
        if (result.Passed)
        {
            return Outcome.Success(result.Warnings.Count == 0 ? "The draft passed every check." : $"The draft passed with {result.Warnings.Count} warning(s).", updates);
        }

        updates[ArchitectApi.GiveUp] = attempt >= ArchitectApi.MaxDrafts;
        if (attempt >= ArchitectApi.MaxDrafts && context.GetString(ArchitectApi.Checked + ".proposal") is { Length: > 0 } checkedProposal)
        {
            // The self-review sent back a draft that had passed; its revision still fails, and no drafts are left: the
            // checked draft stands, and the review's changes are reported as not applied.
            var kept = context.Get(ArchitectApi.Checked + ".warnings") as List<object?> ?? [];
            var checkedAttempt = ArchitectApi.ToInt(context.Get(ArchitectApi.Checked + ".attempt"));
            return Outcome.Success(
                $"Draft {attempt}, revised as the self-review asked, fails its checks; draft {checkedAttempt}, which passed them, stands.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ArchitectApi.Attempt] = (long)attempt,
                    [ArchitectApi.Proposal] = checkedProposal,
                    [ArchitectApi.Rationale] = context.GetString(ArchitectApi.Checked + ".rationale"),
                    [ArchitectApi.Forecast] = context.Get(ArchitectApi.Checked + ".forecast"),
                    [ArchitectApi.Valid] = true,
                    [ArchitectApi.Problems] = new List<object?>(),
                    [ArchitectApi.Warnings] = kept.Append(
                        $"Self-review (not applied: draft {attempt}, revised as it asked, failed its checks — {result.Problems[0]}; draft {checkedAttempt} stands): " +
                        context.GetString(ArchitectApi.Checked + ".changes")).ToList(),
                    [ArchitectApi.Feedback] = "",
                    [ArchitectApi.GiveUp] = false,
                    [ArchitectApi.Checked + ".restored"] = true,
                });
        }

        var summary = attempt >= ArchitectApi.MaxDrafts
            ? $"Draft {attempt} still fails its checks; giving up after {ArchitectApi.MaxDrafts} drafts: {result.Problems[0]}"
            : $"Draft {attempt} fails {result.Problems.Count} check(s): {result.Problems[0]}";
        return Outcome.Fail(summary, updates);
    }
}

/// <summary>
/// The Architect's self-review stage: once a draft passes the checker, the drafting model reviews it against the brief
/// (contracts, the quality bar, the budget, loop bounds and defaults; see <see cref="ArchitectApi.ComposeReviewPrompt"/>).
/// <c>VERDICT: REVISE</c> fails the stage with the requested changes as <c>architect.feedback</c>, so the pipeline routes back to
/// the design stage — unless the drafts are used up, in which case the checked draft is kept and the review is reported. A
/// review that cannot run (no backend, a backend error) does not block a checked draft.
/// </summary>
internal sealed class ArchitectReviewHandler : INodeHandler
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);
        if (context.Run?.Backend is not { } backend || context.GetString(ArchitectApi.Proposal).Trim().Length == 0)
        {
            return Outcome.Success("No self-review (no backend or no draft).");
        }

        if (context.Get(ArchitectApi.Checked + ".restored") is true &&
            string.Equals(context.GetString(ArchitectApi.Checked + ".proposal").Trim(), context.GetString(ArchitectApi.Proposal).Trim(), StringComparison.Ordinal))
        {
            // The checked draft, restored after its revision failed the checks (the check stage reported it): reviewed already.
            return Outcome.Success("The checked draft was reviewed already; it stands.");
        }

        var prompt = ArchitectApi.ComposeReviewPrompt(context);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);
        var stage = node.Clone();
        stage[AttrKeys.Prompt] = prompt;
        CodergenResult result;
        try
        {
            result = await backend.RunAsync(stage, prompt, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await CodergenHandler.RecordFailedAttemptAsync(stageDir, ex, cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            return Outcome.Success($"The self-review could not run ({ex.Message}); the checked draft stands.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { [ArchitectApi.Review] = $"(not reviewed: {ex.Message})" });
        }

        if (result.Metadata.Count > 0)
        {
            var metadata = ContextValues.ToJson(ContextValues.Normalize(result.Metadata));
            await File.WriteAllTextAsync(Path.Combine(stageDir, "backend.json"), metadata?.ToJsonString(Indented) ?? "{}", cancellationToken).ConfigureAwait(false);
        }

        var answer = result.Text ?? result.Outcome?.Notes ?? "";
        await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), answer, cancellationToken).ConfigureAwait(false);
        var (revise, changes) = ArchitectApi.ParseReview(answer);
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal) { [ArchitectApi.Review] = answer.Trim() };
        if (!revise)
        {
            return Outcome.Success("The self-review passed the draft.", updates);
        }

        var attempt = context.TryGet(ArchitectApi.Attempt, out var count) ? ArchitectApi.ToInt(count) : 0;
        if (attempt >= ArchitectApi.MaxDrafts)
        {
            updates[ArchitectApi.Warnings] = ((context.Get(ArchitectApi.Warnings) as List<object?>) ?? [])
                .Append("Self-review (not applied: no drafts left): " + changes).ToList();
            return Outcome.Success("The self-review asked for changes, but no drafts are left; the checked draft stands.", updates);
        }

        updates[ArchitectApi.Feedback] = "The self-review against the brief asks for these changes:\n" + changes;
        updates[ArchitectApi.Valid] = false;
        updates[ArchitectApi.Checked + ".proposal"] = context.GetString(ArchitectApi.Proposal);
        updates[ArchitectApi.Checked + ".rationale"] = context.GetString(ArchitectApi.Rationale);
        updates[ArchitectApi.Checked + ".forecast"] = context.Get(ArchitectApi.Forecast);
        updates[ArchitectApi.Checked + ".warnings"] = context.Get(ArchitectApi.Warnings);
        updates[ArchitectApi.Checked + ".attempt"] = (long)attempt;
        updates[ArchitectApi.Checked + ".changes"] = changes;
        return Outcome.Fail("The self-review asked for changes: " + LogClip(changes), updates);
    }

    private static string LogClip(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 300 ? line : line[..299] + "…";
    }
}

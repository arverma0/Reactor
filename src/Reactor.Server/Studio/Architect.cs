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

    private static readonly SimulationScenario HappyPath = Scenario("""
        {"defaults": {"codergen": {"outcomes": {"success": 1}}, "tool": {"outcomes": {"success": 1}},
                      "stack.manager_loop": {"outcomes": {"success": 1}}}}
        """);

    private static readonly SimulationScenario Realistic = Scenario("""
        {"defaults": {"codergen": {"outcomes": {"success": 0.9, "retry": 0.03, "fail": 0.07}},
                      "tool": {"outcomes": {"success": 0.75, "fail": 0.25}},
                      "stack.manager_loop": {"outcomes": {"success": 0.9, "fail": 0.1}}}}
        """);

    /// <summary>Registers the Architect's handlers on the server's runner and maps its endpoints.</summary>
    public static WebApplication MapArchitectApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var manager = app.Services.GetRequiredService<PipelineRunManager>();
        manager.Runner.Handlers.Register(DesignType, new ArchitectDesignHandler(manager));
        manager.Runner.Handlers.Register(CheckType, new ArchitectCheckHandler(manager));
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
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
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

    /// <summary>Sets the model (and reasoning effort) of the template's design stages by redeclaring them.</summary>
    internal static string WithModel(string template, string model, string? effort)
    {
        var graph = DotParser.Parse(template);
        var lines = new StringBuilder();
        foreach (var node in graph.Nodes.Values.Where(n => n.Type == DesignType))
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
    /// simulated flight must land when every stage succeeds. A second, realistic pass (agents succeed 90% of the time,
    /// tools 75%) produces the forecast and flags loops that do not converge.
    /// </summary>
    internal static async Task<CheckResult> CheckAsync(PipelineRunManager manager, string proposal, string mode, string current, CancellationToken cancellationToken)
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
            if (diagnostic.Severity == Severity.Error)
            {
                problems.Add(text);
            }
            else if (diagnostic.Severity == Severity.Warning)
            {
                warnings.Add(text);
            }
        }

        if (mode != "draft" && TryParse(current) is { } original && original.Name != parsed.Name)
        {
            warnings.Add($"The graph is renamed from '{original.Name}' to '{parsed.Name}'; its flight record is keyed by the name.");
        }

        if (prepared.HasErrors)
        {
            return new CheckResult(problems, warnings, null);
        }

        var custom = manager.CustomHandlerTypes();
        var happy = await MonteCarloSimulator.RunAsync(prepared.Graph, HappyPath, new MonteCarloOptions { Runs = 60, MaxSteps = 300, Seed = 1, CustomHandlerTypes = custom }, cancellationToken).ConfigureAwait(false);
        if (happy.SuccessRate < 1)
        {
            var failure = happy.Failures.FirstOrDefault();
            var where = failure?.LastNode is { } last ? $", last stage '{last}'" : "";
            problems.Add(
                $"Wind tunnel: even with every stage succeeding, {Percent(1 - happy.SuccessRate)} of simulated flights do not land " +
                $"({failure?.Kind ?? "fail"}: {failure?.Reason ?? "unknown"}{where}). Every route must lead to the exit.");
        }

        var realistic = await MonteCarloSimulator.RunAsync(prepared.Graph, Realistic, new MonteCarloOptions { Runs = 400, MaxSteps = 300, Seed = 7, CustomHandlerTypes = custom }, cancellationToken).ConfigureAwait(false);
        var stepLimit = realistic.Outcomes.GetValueOrDefault(SimulationOutcomeKinds.StepLimit) / (double)Math.Max(1, realistic.Runs);
        if (stepLimit > 0.05)
        {
            warnings.Add(
                $"Wind tunnel: with agents succeeding 90% of the time and tools 75%, {Percent(stepLimit)} of flights loop until the step limit. " +
                "Bound the loops with max_retries, goal gates and a retry_target, or an escape route.");
        }

        var forecast = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["happy_path"] = Math.Round(happy.SuccessRate, 4),
            ["realistic"] = Math.Round(realistic.SuccessRate, 4),
            ["step_limit"] = Math.Round(stepLimit, 4),
            ["mean_steps"] = Math.Round(realistic.Steps.Mean, 1),
            ["p90_steps"] = (long)realistic.Steps.P90,
            ["stages"] = (long)prepared.Graph.Nodes.Count,
            ["routes"] = (long)prepared.Graph.Edges.Count,
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
        return new CheckResult(problems, warnings, forecast);
    }

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

    private static SimulationScenario Scenario(string json) => SimulationScenario.FromJson(JsonNode.Parse(json)!.AsObject());

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
        var result = await ArchitectApi.CheckAsync(manager, proposal, context.GetString(ArchitectApi.Mode, "draft"), context.GetString(ArchitectApi.Current), cancellationToken).ConfigureAwait(false);

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
        var summary = attempt >= ArchitectApi.MaxDrafts
            ? $"Draft {attempt} still fails its checks; giving up after {ArchitectApi.MaxDrafts} drafts: {result.Problems[0]}"
            : $"Draft {attempt} fails {result.Problems.Count} check(s): {result.Problems[0]}";
        return Outcome.Fail(summary, updates);
    }
}

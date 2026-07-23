using System.Globalization;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Human;

namespace Reactor.Simulation;

/// <summary>Which real handler a simulated stage stands in for (decides the context keys it sets).</summary>
internal enum SimulatedStageKind
{
    Codergen,
    Tool,
    ManagerLoop,
}

/// <summary>A simulated outcome decision and a one-line explanation (logged as a <c>StageLog</c> event).</summary>
internal sealed record SimulatedDecision(StageStatus Status, string PreferredLabel, string Explanation);

/// <summary>
/// Stands in for codergen, tool, manager-loop, and custom handlers: draws the outcome from the scenario (or, in interactive
/// mode, asks the run's interviewer), spends the simulated duration, and returns an outcome shaped like the real handler's.
/// Never calls a backend, runs a command, launches a child pipeline, or writes stage files. Self-timed: a node's
/// <c>timeout</c> (for tools, else the runner's default tool timeout) applies to the simulated duration only, never to an
/// interactive question.
/// </summary>
internal sealed class SimulatedStageHandler(SimulationSample sample, string handlerType, SimulatedStageKind kind) : ISelfTimedHandler
{
    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);

        var execution = sample.NextExecution(node.Id);
        var behavior = sample.Scenario.Resolve(node.Id, handlerType);
        SimulatedDecision? decision = null;
        if (sample.Mode == SimulationMode.Interactive && context.Run is { } run)
        {
            decision = await AskAsync(node, graph, run, cancellationToken).ConfigureAwait(false);
        }

        decision ??= SimulatedDecisions.Draw(sample, node.Id, behavior, execution);
        context.Run?.Log(node.Id, decision.Explanation);

        var duration = behavior.DurationMs ?? sample.StepDelayMs;
        var limit = node.Timeout ?? (kind == SimulatedStageKind.Tool ? context.Run?.RunnerOptions.DefaultToolTimeout : null);
        if (!await sample.SpendAsync(duration, limit, cancellationToken).ConfigureAwait(false))
        {
            // Mirrors the engine's own stage timeout (a retryable TimeoutException).
            throw new TimeoutException($"Stage '{node.Id}' timed out after {AttributeValues.FormatDuration(limit!.Value)}.");
        }

        return BuildOutcome(node, decision, behavior, duration);
    }

    /// <summary>Asks the walkthrough question; null (let the scenario decide) when the answer is skipped or unrecognized.</summary>
    private static async Task<SimulatedDecision?> AskAsync(Node node, Graph graph, RunServices run, CancellationToken cancellationToken)
    {
        var labels = graph.OutgoingEdges(node.Id)
            .Select(e => e.Label)
            .Where(l => l.Length > 0)
            .DistinctBy(Accelerators.NormalizeLabel)
            .ToList();
        var options = new List<Option>
        {
            new("S", "[S] Success"),
            new("F", "[F] Fail"),
            new("R", "[R] Retry"),
            new("P", "[P] Partial success"),
        };
        for (var i = 0; i < labels.Count; i++)
        {
            var key = (i + 1).ToString(CultureInfo.InvariantCulture);
            var text = Accelerators.StripPrefix(labels[i]);
            options.Add(new Option(key, $"[{key}] → {(text.Length > 0 ? text : labels[i])}"));
        }

        var question = new Question
        {
            Text = $"Simulate the outcome of '{node.Label}'",
            Type = QuestionType.MultipleChoice,
            Options = options,
            Stage = node.Id,
        };
        var answer = await run.Interviewer.AskAndReportAsync(question, run.Events, cancellationToken).ConfigureAwait(false);
        if (answer.IsSkipped || answer.IsTimeout)
        {
            return null;
        }

        var value = (answer.SelectedOption?.Key ?? answer.Value).Trim();
        var option = options.FirstOrDefault(o => string.Equals(o.Key, value, StringComparison.OrdinalIgnoreCase))
                     ?? options.FirstOrDefault(o => Accelerators.NormalizeLabel(o.Label) == Accelerators.NormalizeLabel(answer.Text.Length > 0 ? answer.Text : value));
        if (option is not null)
        {
            var index = options.IndexOf(option);
            return index switch
            {
                0 => Chosen(StageStatus.Success, ""),
                1 => Chosen(StageStatus.Fail, ""),
                2 => Chosen(StageStatus.Retry, ""),
                3 => Chosen(StageStatus.PartialSuccess, ""),
                _ => Chosen(StageStatus.Success, labels[index - 4]),
            };
        }

        if (StageStatusExtensions.TryParse(value, out var status))
        {
            return Chosen(status, "");
        }

        var label = labels.FirstOrDefault(l => Accelerators.NormalizeLabel(l) == Accelerators.NormalizeLabel(value));
        return label is null ? null : Chosen(StageStatus.Success, label);

        static SimulatedDecision Chosen(StageStatus status, string preferredLabel) =>
            new(status, preferredLabel, SimulatedDecisions.Explain(status, preferredLabel, "chosen in the walkthrough"));
    }

    private Outcome BuildOutcome(Node node, SimulatedDecision decision, ResolvedBehavior behavior, int durationMs)
    {
        var status = decision.Status;
        var ok = status is StageStatus.Success or StageStatus.PartialSuccess or StageStatus.Skipped;
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (kind)
        {
            case SimulatedStageKind.Tool:
                updates["tool.output"] = "[Simulated] " + (node[AttrKeys.ToolCommand] ?? "");
                updates["tool.exit_code"] = ok ? 0 : 1;
                updates["tool.stderr"] = "";
                updates["tool.duration_ms"] = (long)durationMs;
                break;
            case SimulatedStageKind.ManagerLoop:
                updates["stack.child.status"] = ok ? "completed" : "failed";
                updates["stack.child.outcome"] = ok ? "success" : "fail";
                updates["context.stack.child.status"] = ok ? "completed" : "failed";
                updates["context.stack.child.outcome"] = ok ? "success" : "fail";
                break;
            default:
                updates["last_stage"] = node.Id;
                updates["last_response"] = "[Simulated] Response for stage: " + node.Id;
                break;
        }

        foreach (var (key, value) in behavior.UpdatesFor(status))
        {
            updates[key] = value;
        }

        return new Outcome
        {
            Status = status,
            PreferredLabel = decision.PreferredLabel,
            Notes = "[Simulated] " + status.ToWire(),
            FailureReason = status switch
            {
                StageStatus.Fail => "[Simulated] failure",
                StageStatus.Retry => "[Simulated] retry requested",
                _ => "",
            },
            ContextUpdates = updates,
        };
    }
}

/// <summary>
/// The real <see cref="WaitForHumanHandler"/> (asking the run's interviewer, which is simulated in stochastic mode) plus the
/// scenario's <c>context_updates</c> for the resulting status.
/// </summary>
internal sealed class SimulatedHumanGateHandler(SimulationSample sample) : ISelfTimedHandler
{
    private readonly WaitForHumanHandler _inner = new();

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        var outcome = await _inner.ExecuteAsync(node, context, graph, logsRoot, cancellationToken).ConfigureAwait(false);
        if (outcome is null)
        {
            return null;
        }

        var extra = sample.Scenario.Resolve(node.Id, HandlerTypes.WaitHuman).UpdatesFor(outcome.Status).ToList();
        if (extra.Count == 0)
        {
            return outcome;
        }

        var merged = new Dictionary<string, object?>(outcome.ContextUpdates, StringComparer.Ordinal);
        foreach (var (key, value) in extra)
        {
            merged[key] = value;
        }

        return outcome with { ContextUpdates = merged };
    }
}

/// <summary>
/// The real <see cref="ParallelHandler"/>, recording each execution's branch paths (from <c>parallel.results</c>) so a
/// sample's path and edges can be reported in branch order regardless of scheduling.
/// </summary>
internal sealed class RecordingParallelHandler(SimulationSample sample) : INodeHandler
{
    private readonly ParallelHandler _inner = new();

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        var outcome = await _inner.ExecuteAsync(node, context, graph, logsRoot, cancellationToken).ConfigureAwait(false);
        if (outcome is not null && outcome.ContextUpdates.TryGetValue("parallel.results", out var raw) && ContextValues.Normalize(raw) is List<object?> items)
        {
            var branches = new List<ParallelBranchRecord>();
            foreach (var item in items.OfType<Dictionary<string, object?>>())
            {
                var completed = item.GetValueOrDefault("completed_nodes") is List<object?> list
                    ? list.Select(ContextValues.ToDisplayString).ToList()
                    : [];
                var stop = ContextValues.ToDisplayString(item.GetValueOrDefault("stop_node"));
                branches.Add(new ParallelBranchRecord(
                    ContextValues.ToDisplayString(item.GetValueOrDefault("id")),
                    ContextValues.ToDisplayString(item.GetValueOrDefault("status")),
                    stop.Length > 0 ? stop : null,
                    completed));
            }

            sample.RecordParallel(new ParallelRecord(node.Id, branches));
        }

        return outcome;
    }
}

/// <summary>
/// Answers human gates from the scenario: the gate's <c>script</c>/<c>outcomes</c> decide whether the human answers
/// (success), skips (fail), or lets the gate time out (retry); the answer is a scripted label, else drawn from
/// <c>choices</c> (matched against option label, accelerator key, or target node id; uniform by default). Freeform gates
/// get <see cref="FreeformAnswer"/>. The gate's duration is spent before answering; when it exceeds the question's timeout
/// the gate times out.
/// </summary>
internal sealed class SimulatedInterviewer(SimulationSample sample, Graph graph) : IInterviewer
{
    public const string FreeformAnswer = "Simulated input";

    public async Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        var nodeId = question.Stage.Length > 0 ? question.Stage : question.Text;
        var execution = sample.NextExecution(nodeId);
        var behavior = sample.Scenario.Resolve(nodeId, HandlerTypes.WaitHuman);
        var decision = SimulatedDecisions.Draw(sample, nodeId, behavior with { Labels = null }, execution);

        var duration = behavior.DurationMs ?? sample.StepDelayMs;
        var limit = question.TimeoutSeconds is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        if (!await sample.SpendAsync(duration, limit, cancellationToken).ConfigureAwait(false))
        {
            return Answer.Timeout();
        }

        switch (decision.Status)
        {
            case StageStatus.Fail:
                return Answer.Skipped();
            case StageStatus.Retry:
                return Answer.Timeout();
        }

        return question.Type switch
        {
            QuestionType.Freeform => Answer.FromText(FreeformAnswer),
            QuestionType.YesNo or QuestionType.Confirmation => ChooseYesNo(nodeId, behavior, execution, decision.PreferredLabel),
            _ => ChooseOption(question, nodeId, behavior, execution, decision.PreferredLabel),
        };
    }

    private Answer ChooseOption(Question question, string nodeId, ResolvedBehavior behavior, int execution, string scripted)
    {
        var options = question.Options;
        if (options.Count == 0)
        {
            return Answer.Skipped();
        }

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in graph.OutgoingEdges(nodeId))
        {
            targets.TryAdd(edge.Label.Length > 0 ? edge.Label : edge.To, edge.To);
        }

        if (scripted.Length > 0 && options.FirstOrDefault(o => Matches(o, scripted, targets)) is { } scriptedOption)
        {
            return Answer.ForOption(scriptedOption);
        }

        var weights = options
            .Select(o => behavior.Choices is null ? 1.0 : behavior.Choices.Where(c => Matches(o, c.Key, targets)).Sum(c => c.Value))
            .ToList();
        if (weights.Sum() <= 0)
        {
            weights = options.Select(_ => 1.0).ToList();
        }

        var index = SimulatedDecisions.PickIndex(weights, sample.Draw(nodeId, execution, "choice"));
        return Answer.ForOption(options[index]);
    }

    private Answer ChooseYesNo(string nodeId, ResolvedBehavior behavior, int execution, string scripted)
    {
        static bool IsYes(string value) => value.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "approve";
        static bool IsNo(string value) => value.Trim().ToLowerInvariant() is "no" or "n" or "false" or "reject";
        if (scripted.Length > 0 && (IsYes(scripted) || IsNo(scripted)))
        {
            return IsYes(scripted) ? Answer.Yes() : Answer.No();
        }

        var yes = behavior.Choices?.Where(c => IsYes(c.Key)).Sum(c => c.Value) ?? 0;
        var no = behavior.Choices?.Where(c => IsNo(c.Key)).Sum(c => c.Value) ?? 0;
        if (yes + no <= 0)
        {
            return Answer.Yes();
        }

        return SimulatedDecisions.PickIndex([yes, no], sample.Draw(nodeId, execution, "choice")) == 0 ? Answer.Yes() : Answer.No();
    }

    private static bool Matches(Option option, string value, Dictionary<string, string> targets)
    {
        var trimmed = value.Trim();
        return string.Equals(option.Key, trimmed, StringComparison.OrdinalIgnoreCase)
               || string.Equals(option.Label, trimmed, StringComparison.Ordinal)
               || (targets.TryGetValue(option.Label, out var target) && string.Equals(target, trimmed, StringComparison.Ordinal))
               || Accelerators.NormalizeLabel(option.Label) == Accelerators.NormalizeLabel(trimmed);
    }
}

/// <summary>Scenario-driven outcome draws shared by simulated stages and the simulated interviewer.</summary>
internal static class SimulatedDecisions
{
    /// <summary>The next scripted outcome, else a weighted draw; success draws a preferred label from <c>labels</c>.</summary>
    public static SimulatedDecision Draw(SimulationSample sample, string nodeId, ResolvedBehavior behavior, int execution)
    {
        if (execution <= behavior.Script.Count)
        {
            var step = behavior.Script[execution - 1];
            var scriptedLabel = step.PreferredLabel.Length > 0 || !step.Status.IsSuccessful()
                ? step.PreferredLabel
                : DrawLabel(sample, nodeId, behavior, execution);
            return new SimulatedDecision(step.Status, scriptedLabel, Explain(step.Status, scriptedLabel, $"script entry {execution} of {behavior.Script.Count}"));
        }

        var weights = behavior.Outcomes.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).ToList();
        if (weights.Count == 0)
        {
            weights = [KeyValuePair.Create(StageStatus.Success, 1.0)];
        }

        var draw = sample.Draw(nodeId, execution, "outcome");
        var status = weights[PickIndex(weights.Select(kv => kv.Value).ToList(), draw)].Key;
        var label = status.IsSuccessful() ? DrawLabel(sample, nodeId, behavior, execution) : "";
        var total = weights.Sum(kv => kv.Value);
        var odds = string.Join(", ", weights.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key.ToWire()} {kv.Value / total * 100:0.#}%")));
        return new SimulatedDecision(status, label, Explain(status, label, string.Create(CultureInfo.InvariantCulture, $"drew {draw:0.000} against {odds}")));
    }

    /// <summary>Index of the weighted entry the uniform <paramref name="draw"/> falls into (weights need not be normalized).</summary>
    public static int PickIndex(IReadOnlyList<double> weights, double draw)
    {
        var total = weights.Where(w => w > 0).Sum();
        var target = draw * total;
        var cumulative = 0.0;
        var last = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0)
            {
                continue;
            }

            last = i;
            cumulative += weights[i];
            if (target < cumulative)
            {
                return i;
            }
        }

        return last;
    }

    public static string Explain(StageStatus status, string preferredLabel, string reason) =>
        preferredLabel.Length > 0
            ? $"[Simulated] {status.ToWire()} with preferred label '{preferredLabel}' ({reason})"
            : $"[Simulated] {status.ToWire()} ({reason})";

    private static string DrawLabel(SimulationSample sample, string nodeId, ResolvedBehavior behavior, int execution)
    {
        if (behavior.Labels is not { Count: > 0 } labels)
        {
            return "";
        }

        var ordered = labels.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        return ordered.Count == 0
            ? ""
            : ordered[PickIndex(ordered.Select(kv => kv.Value).ToList(), sample.Draw(nodeId, execution, "label"))].Key;
    }
}

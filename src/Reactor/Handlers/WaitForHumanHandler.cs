using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;

namespace Reactor.Handlers;

/// <summary>
/// Human-in-the-loop gate (spec 4.6): presents the outgoing edges as choices through the run's
/// <see cref="IInterviewer"/>, then routes to the selected edge (via <c>suggested_next_ids</c> and <c>preferred_label</c>).
/// The question times out after <c>human.timeout</c> (or the node <c>timeout</c>); on timeout the
/// <c>human.default_choice</c> (target id, key, or label) is used, else the stage requests a retry.
/// With <c>human.input="freeform"</c> the gate collects free text into <c>human.gate.text</c> and routes normally.
/// </summary>
public sealed class WaitForHumanHandler : ISelfTimedHandler
{
    private sealed record Choice(string Key, string Label, string To);

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);

        var interviewer = context.Run?.Interviewer ?? new AutoApproveInterviewer();
        var events = context.Run?.Events;
        var timeout = AttributeValues.ParseDuration(node["human.timeout"]) ?? node.Timeout;
        var text = string.IsNullOrWhiteSpace(node[AttrKeys.Label]) ? "Select an option:" : node.Label;

        if (string.Equals(node["human.input"], "freeform", StringComparison.OrdinalIgnoreCase))
        {
            var freeform = new Question
            {
                Text = node["human.question"] ?? text,
                Type = QuestionType.Freeform,
                Stage = node.Id,
                TimeoutSeconds = timeout?.TotalSeconds,
            };
            var freeAnswer = await interviewer.AskAndReportAsync(freeform, events, cancellationToken).ConfigureAwait(false);
            if (freeAnswer.IsSkipped)
            {
                return Outcome.Fail("human skipped interaction");
            }

            if (freeAnswer.IsTimeout)
            {
                return node[AttrKeys.HumanDefaultChoice] is { Length: > 0 } fallbackText
                    ? Outcome.Success("Human input timed out; default used", new Dictionary<string, object?> { ["human.gate.text"] = fallbackText })
                    : Outcome.Retry("human gate timeout, no default");
            }

            var responseText = freeAnswer.Text.Length > 0 ? freeAnswer.Text : freeAnswer.Value;
            return Outcome.Success("Human input received", new Dictionary<string, object?>
            {
                ["human.gate.text"] = responseText,
                ["human.gate.label"] = responseText,
            });
        }

        var choices = graph.OutgoingEdges(node.Id)
            .Select(e =>
            {
                var label = e.Label.Length > 0 ? e.Label : e.To;
                return new Choice(Accelerators.ParseKey(label), label, e.To);
            })
            .ToList();
        if (choices.Count == 0)
        {
            return Outcome.Fail("No outgoing edges for human gate");
        }

        var question = new Question
        {
            Text = text,
            Type = QuestionType.MultipleChoice,
            Options = choices.Select(c => new Option(c.Key, c.Label)).ToList(),
            Stage = node.Id,
            TimeoutSeconds = timeout?.TotalSeconds,
        };

        var answer = await interviewer.AskAndReportAsync(question, events, cancellationToken).ConfigureAwait(false);
        Choice? selected;
        if (answer.IsTimeout)
        {
            var fallback = node[AttrKeys.HumanDefaultChoice];
            selected = string.IsNullOrWhiteSpace(fallback) ? null : Match(fallback, choices);
            if (selected is null)
            {
                return Outcome.Retry("human gate timeout, no default");
            }
        }
        else if (answer.IsSkipped)
        {
            return Outcome.Fail("human skipped interaction");
        }
        else
        {
            selected = (answer.SelectedOption is { } option ? Match(option.Key, choices) ?? Match(option.Label, choices) : null)
                       ?? Match(answer.Value, choices)
                       ?? (answer.Text.Length > 0 ? Match(answer.Text, choices) : null)
                       ?? (answer.IsYes ? choices[0] : null)
                       ?? choices[0];
        }

        return new Outcome
        {
            Status = StageStatus.Success,
            SuggestedNextIds = [selected.To],
            PreferredLabel = selected.Label,
            Notes = $"Human selected '{selected.Label}'",
            ContextUpdates = new Dictionary<string, object?>
            {
                ["human.gate.selected"] = selected.Key,
                ["human.gate.label"] = selected.Label,
                ["human.gate.target"] = selected.To,
            },
        };
    }

    private static Choice? Match(string value, List<Choice> choices)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return choices.FirstOrDefault(c => string.Equals(c.Key, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? choices.FirstOrDefault(c => string.Equals(c.To, trimmed, StringComparison.Ordinal))
               ?? choices.FirstOrDefault(c => Accelerators.NormalizeLabel(c.Label) == Accelerators.NormalizeLabel(trimmed));
    }
}

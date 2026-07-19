using System.Text.RegularExpressions;

namespace Reactor.Human;

/// <summary>Determines the UI and valid answers for a question.</summary>
public enum QuestionType
{
    YesNo,
    MultipleChoice,
    Freeform,
    Confirmation,
}

/// <summary>A selectable option of a multiple-choice question.</summary>
/// <param name="Key">Accelerator key (e.g. <c>Y</c>, <c>A</c>).</param>
/// <param name="Label">Display text.</param>
public sealed record Option(string Key, string Label);

/// <summary>Special answer values.</summary>
public enum AnswerValue
{
    Yes,
    No,
    Skipped,
    Timeout,
}

/// <summary>A question presented to a human.</summary>
public sealed record Question
{
    /// <summary>Unique id (used by remote frontends to submit answers).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Text { get; init; }

    public QuestionType Type { get; init; } = QuestionType.MultipleChoice;

    public IReadOnlyList<Option> Options { get; init; } = [];

    /// <summary>Answer to use on timeout/skip, if any.</summary>
    public Answer? Default { get; init; }

    /// <summary>Maximum wait; null waits indefinitely.</summary>
    public double? TimeoutSeconds { get; init; }

    /// <summary>Originating stage (node id).</summary>
    public string Stage { get; init; } = "";

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}

/// <summary>A human's answer.</summary>
public sealed record Answer
{
    /// <summary>The selected value: an option key, free text, or a special <see cref="AnswerValue"/> name.</summary>
    public string Value { get; init; } = "";

    /// <summary>Set when <see cref="Value"/> is a special value (YES, NO, SKIPPED, TIMEOUT).</summary>
    public AnswerValue? Special { get; init; }

    /// <summary>The full selected option (multiple choice).</summary>
    public Option? SelectedOption { get; init; }

    /// <summary>Free text response (freeform).</summary>
    public string Text { get; init; } = "";

    public bool IsTimeout => Special == AnswerValue.Timeout;

    public bool IsSkipped => Special == AnswerValue.Skipped;

    public bool IsYes => Special == AnswerValue.Yes;

    public static Answer Yes() => new() { Value = "YES", Special = AnswerValue.Yes };

    public static Answer No() => new() { Value = "NO", Special = AnswerValue.No };

    public static Answer Skipped() => new() { Value = "SKIPPED", Special = AnswerValue.Skipped };

    public static Answer Timeout() => new() { Value = "TIMEOUT", Special = AnswerValue.Timeout };

    public static Answer ForOption(Option option) => new() { Value = option.Key, SelectedOption = option, Text = option.Label };

    public static Answer FromText(string text) => new() { Value = text, Text = text };

    public override string ToString() => Special?.ToString().ToUpperInvariant() ?? (SelectedOption is { } o ? $"[{o.Key}] {o.Label}" : Value);
}

/// <summary>
/// All human interaction goes through an interviewer, so any frontend (CLI, web, chat bot, test queue) can answer.
/// </summary>
public interface IInterviewer
{
    Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<Answer>> AskMultipleAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var answers = new List<Answer>(questions.Count);
        foreach (var question in questions)
        {
            answers.Add(await AskAsync(question, cancellationToken).ConfigureAwait(false));
        }

        return answers;
    }

    /// <summary>Informational message (no answer expected).</summary>
    Task InformAsync(string message, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Accelerator-key parsing and label normalization shared by human gates and edge selection.</summary>
public static partial class Accelerators
{
    /// <summary>
    /// Extracts the accelerator key from a label: <c>[K] Label</c>, <c>K) Label</c>, <c>K - Label</c>, else the first character.
    /// </summary>
    public static string ParseKey(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var trimmed = label.Trim();
        var match = BracketPattern().Match(trimmed);
        if (match.Success)
        {
            return match.Groups["k"].Value;
        }

        match = ParenPattern().Match(trimmed);
        if (match.Success)
        {
            return match.Groups["k"].Value;
        }

        match = DashPattern().Match(trimmed);
        if (match.Success)
        {
            return match.Groups["k"].Value;
        }

        return trimmed.Length > 0 ? trimmed[..1].ToUpperInvariant() : "";
    }

    /// <summary>Normalizes a label for matching: strips accelerator prefixes, trims, lowercases.</summary>
    public static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "";
        }

        var trimmed = label.Trim();
        foreach (var pattern in new[] { BracketPattern(), ParenPattern(), DashPattern() })
        {
            var match = pattern.Match(trimmed);
            if (match.Success)
            {
                trimmed = trimmed[match.Length..];
                break;
            }
        }

        return trimmed.Trim().ToLowerInvariant();
    }

    /// <summary>The label without its accelerator prefix (original casing).</summary>
    public static string StripPrefix(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var trimmed = label.Trim();
        foreach (var pattern in new[] { BracketPattern(), ParenPattern(), DashPattern() })
        {
            var match = pattern.Match(trimmed);
            if (match.Success)
            {
                return trimmed[match.Length..].Trim();
            }
        }

        return trimmed;
    }

    [GeneratedRegex(@"^\[(?<k>[^\]\s]{1,12})\]\s*")]
    private static partial Regex BracketPattern();

    [GeneratedRegex(@"^(?<k>[A-Za-z0-9])\)\s*")]
    private static partial Regex ParenPattern();

    [GeneratedRegex(@"^(?<k>[A-Za-z0-9])\s+-\s+")]
    private static partial Regex DashPattern();
}

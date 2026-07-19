using System.Collections.Concurrent;
using Reactor.Human;

namespace Reactor.Server;

/// <summary>A question waiting for a web (or API) answer.</summary>
public sealed class PendingQuestion
{
    internal PendingQuestion(Question question)
    {
        Question = question;
    }

    public Question Question { get; }

    public DateTimeOffset AskedAt { get; } = DateTimeOffset.UtcNow;

    internal TaskCompletionSource<Answer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The result of submitting an answer.</summary>
public enum AnswerSubmission
{
    Accepted,
    NotFound,
    Invalid,
}

/// <summary>
/// An interviewer for HTTP mode: questions wait in <see cref="Pending"/> until answered through
/// <see cref="Submit"/>. Cancellation (stage timeout, run cancellation) withdraws the question.
/// </summary>
public sealed class WebInterviewer : IInterviewer
{
    private readonly ConcurrentDictionary<string, PendingQuestion> _pending = new(StringComparer.Ordinal);

    /// <summary>Raised when a question becomes pending.</summary>
    public event Action<PendingQuestion>? QuestionAsked;

    /// <summary>Raised for informational messages from stages.</summary>
    public event Action<string, string>? Informed;

    /// <summary>Pending questions, oldest first.</summary>
    public IReadOnlyList<PendingQuestion> Pending => _pending.Values.OrderBy(p => p.AskedAt).ToList();

    public async Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        var pending = new PendingQuestion(question);
        if (!_pending.TryAdd(question.Id, pending))
        {
            throw new InvalidOperationException($"Question '{question.Id}' is already pending.");
        }

        try
        {
            QuestionAsked?.Invoke(pending);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(question.Id, out _);
        }
    }

    public Task InformAsync(string message, string stage, CancellationToken cancellationToken = default)
    {
        Informed?.Invoke(message, stage);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Answers a pending question. <paramref name="value"/> is an option key or label (multiple choice), yes/no
    /// (yes-no/confirmation), or the response text (freeform); <c>skip</c> skips any question.
    /// </summary>
    public AnswerSubmission Submit(string questionId, string? value, string? text, out string? error)
    {
        error = null;
        if (!_pending.TryGetValue(questionId, out var pending))
        {
            error = $"No pending question '{questionId}'.";
            return AnswerSubmission.NotFound;
        }

        var answer = Interpret(pending.Question, value, text, out error);
        if (answer is null)
        {
            return AnswerSubmission.Invalid;
        }

        if (!pending.Completion.TrySetResult(answer))
        {
            error = $"Question '{questionId}' was already answered.";
            return AnswerSubmission.NotFound;
        }

        return AnswerSubmission.Accepted;
    }

    /// <summary>Converts a submitted value into an <see cref="Answer"/> for <paramref name="question"/>, or null with an error.</summary>
    public static Answer? Interpret(Question question, string? value, string? text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(question);
        error = null;
        var raw = (value ?? text ?? "").Trim();
        if (raw.Equals("skip", StringComparison.OrdinalIgnoreCase) || raw.Equals("skipped", StringComparison.OrdinalIgnoreCase))
        {
            return Answer.Skipped();
        }

        switch (question.Type)
        {
            case QuestionType.YesNo or QuestionType.Confirmation:
                if (raw.ToLowerInvariant() is "y" or "yes" or "true" or "1" or "approve" or "ok")
                {
                    return Answer.Yes();
                }

                if (raw.ToLowerInvariant() is "n" or "no" or "false" or "0" or "reject")
                {
                    return Answer.No();
                }

                error = "Answer yes or no.";
                return null;

            case QuestionType.Freeform:
                var freeform = text ?? value;
                if (string.IsNullOrWhiteSpace(freeform))
                {
                    error = "A text answer is required.";
                    return null;
                }

                return Answer.FromText(freeform);

            default:
                var option = question.Options.FirstOrDefault(o => string.Equals(o.Key, raw, StringComparison.OrdinalIgnoreCase))
                    ?? question.Options.FirstOrDefault(o => string.Equals(o.Label, raw, StringComparison.OrdinalIgnoreCase))
                    ?? question.Options.FirstOrDefault(o => Accelerators.NormalizeLabel(o.Label) == Accelerators.NormalizeLabel(raw));
                if (option is not null)
                {
                    return Answer.ForOption(option);
                }

                error = question.Options.Count == 0
                    ? "This question has no options."
                    : $"Choose one of: {string.Join(", ", question.Options.Select(o => $"{o.Key} ({o.Label})"))}.";
                return null;
        }
    }
}

using System.Diagnostics;
using Reactor.Events;

namespace Reactor.Human;

/// <summary>Helpers for handlers that put questions to a human.</summary>
public static class InterviewerExtensions
{
    /// <summary>
    /// Asks <paramref name="question"/> and reports it as an <see cref="InterviewStarted"/> event followed by
    /// <see cref="InterviewCompleted"/> (or <see cref="InterviewTimeout"/>), so event-driven frontends can show it. When the
    /// question has <see cref="Question.TimeoutSeconds"/>, the ask is cancelled after that long and answered with the
    /// question's <see cref="Question.Default"/> or <see cref="Answer.Timeout"/>.
    /// </summary>
    public static async Task<Answer> AskAndReportAsync(this IInterviewer interviewer, Question question, IPipelineEventSink? events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interviewer);
        ArgumentNullException.ThrowIfNull(question);
        events?.Emit(new InterviewStarted(question.Id, question.Text, question.Stage, question.Options, question.Type.ToString()));
        var stopwatch = Stopwatch.StartNew();
        Answer answer;
        if (question.TimeoutSeconds is { } seconds && seconds > 0)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                answer = await interviewer.AskAsync(question, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                answer = question.Default ?? Answer.Timeout();
            }
        }
        else
        {
            answer = await interviewer.AskAsync(question, cancellationToken).ConfigureAwait(false);
        }

        if (answer.IsTimeout)
        {
            events?.Emit(new InterviewTimeout(question.Id, question.Text, question.Stage, stopwatch.Elapsed));
        }
        else
        {
            events?.Emit(new InterviewCompleted(question.Id, question.Text, answer.ToString(), stopwatch.Elapsed));
        }

        return answer;
    }
}

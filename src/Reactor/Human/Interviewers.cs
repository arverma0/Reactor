using System.Collections.Concurrent;
using System.Globalization;

namespace Reactor.Human;

/// <summary>Always approves: YES for yes/no and confirmations, the first option for multiple choice.</summary>
public sealed class AutoApproveInterviewer : IInterviewer
{
    public Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (question.Type is QuestionType.YesNo or QuestionType.Confirmation)
        {
            return Task.FromResult(Answer.Yes());
        }

        if (question.Type == QuestionType.MultipleChoice && question.Options.Count > 0)
        {
            return Task.FromResult(Answer.ForOption(question.Options[0]));
        }

        return Task.FromResult(new Answer { Value = "auto-approved", Text = "auto-approved" });
    }
}

/// <summary>Delegates answering to a callback (Slack, web UI, API, ...).</summary>
public sealed class CallbackInterviewer(Func<Question, CancellationToken, Task<Answer>> callback) : IInterviewer
{
    private readonly Func<Question, CancellationToken, Task<Answer>> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public CallbackInterviewer(Func<Question, Answer> callback)
        : this((q, _) => Task.FromResult(callback(q)))
    {
    }

    public Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default) => _callback(question, cancellationToken);
}

/// <summary>Answers from a pre-filled queue (deterministic tests and replay). An empty queue answers SKIPPED.</summary>
public sealed class QueueInterviewer : IInterviewer
{
    private readonly ConcurrentQueue<Answer> _answers;

    public QueueInterviewer()
        : this(Array.Empty<Answer>())
    {
    }

    public QueueInterviewer(IEnumerable<Answer> answers)
    {
        _answers = new ConcurrentQueue<Answer>(answers ?? throw new ArgumentNullException(nameof(answers)));
    }

    public QueueInterviewer(params Answer[] answers)
        : this((IEnumerable<Answer>)answers)
    {
    }

    public QueueInterviewer(params string[] values)
        : this(values.Select(Answer.FromText))
    {
    }

    public int Remaining => _answers.Count;

    public void Enqueue(Answer answer) => _answers.Enqueue(answer);

    public Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default) =>
        Task.FromResult(_answers.TryDequeue(out var answer) ? answer : Answer.Skipped());
}

/// <summary>Wraps another interviewer and records every question/answer pair (replay, debugging, audit).</summary>
public sealed class RecordingInterviewer(IInterviewer inner) : IInterviewer
{
    private readonly IInterviewer _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ConcurrentQueue<(Question Question, Answer Answer)> _recordings = new();

    public IReadOnlyList<(Question Question, Answer Answer)> Recordings => _recordings.ToList();

    public async Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default)
    {
        var answer = await _inner.AskAsync(question, cancellationToken).ConfigureAwait(false);
        _recordings.Enqueue((question, answer));
        return answer;
    }

    public Task InformAsync(string message, string stage, CancellationToken cancellationToken = default) =>
        _inner.InformAsync(message, stage, cancellationToken);
}

/// <summary>
/// Terminal interviewer: prints the question with option keys and reads the answer from standard input
/// (with timeout support).
/// </summary>
public sealed class ConsoleInterviewer : IInterviewer
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConsoleInterviewer(TextReader? input = null, TextWriter? output = null)
    {
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    public async Task<Answer> AskAsync(Question question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync().ConfigureAwait(false);
            await _output.WriteLineAsync($"[?] {question.Text}").ConfigureAwait(false);
            string prompt;
            switch (question.Type)
            {
                case QuestionType.MultipleChoice:
                    foreach (var option in question.Options)
                    {
                        await _output.WriteLineAsync($"  [{option.Key}] {Accelerators.StripPrefix(option.Label)}").ConfigureAwait(false);
                    }

                    prompt = "Select: ";
                    break;
                case QuestionType.YesNo or QuestionType.Confirmation:
                    prompt = "[Y/N]: ";
                    break;
                default:
                    prompt = "> ";
                    break;
            }

            while (true)
            {
                await _output.WriteAsync(prompt).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
                var line = await ReadLineAsync(question.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    await _output.WriteLineAsync().ConfigureAwait(false);
                    return question.Default ?? (question.TimeoutSeconds is not null ? Answer.Timeout() : Answer.Skipped());
                }

                var response = line.Trim();
                switch (question.Type)
                {
                    case QuestionType.MultipleChoice:
                        var match = MatchOption(response, question.Options);
                        if (match is not null)
                        {
                            return Answer.ForOption(match);
                        }

                        await _output.WriteLineAsync($"  '{response}' is not one of the options; enter a key or label.").ConfigureAwait(false);
                        continue;
                    case QuestionType.YesNo or QuestionType.Confirmation:
                        if (response.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                        {
                            return Answer.Yes();
                        }

                        if (response.StartsWith("n", StringComparison.OrdinalIgnoreCase))
                        {
                            return Answer.No();
                        }

                        continue;
                    default:
                        return Answer.FromText(response);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InformAsync(string message, string stage, CancellationToken cancellationToken = default) =>
        await _output.WriteLineAsync(string.IsNullOrEmpty(stage) ? $"[i] {message}" : $"[i] ({stage}) {message}").ConfigureAwait(false);

    /// <summary>Matches a typed response to an option by key, label, or 1-based index.</summary>
    public static Option? MatchOption(string response, IReadOnlyList<Option> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var trimmed = response.Trim();
        var byKey = options.FirstOrDefault(o => string.Equals(o.Key, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byKey is not null)
        {
            return byKey;
        }

        var normalized = Accelerators.NormalizeLabel(trimmed);
        var byLabel = options.FirstOrDefault(o => Accelerators.NormalizeLabel(o.Label) == normalized);
        if (byLabel is not null)
        {
            return byLabel;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 1 && index <= options.Count)
        {
            return options[index - 1];
        }

        return null;
    }

    private async Task<string?> ReadLineAsync(double? timeoutSeconds, CancellationToken cancellationToken)
    {
        var read = _input.ReadLineAsync(cancellationToken).AsTask();
        if (timeoutSeconds is not { } seconds || seconds <= 0)
        {
            return await read.ConfigureAwait(false);
        }

        var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken)).ConfigureAwait(false);
        return completed == read ? await read.ConfigureAwait(false) : null;
    }
}

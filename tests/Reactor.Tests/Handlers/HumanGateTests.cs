using Reactor.Events;
using Reactor.Execution;
using Reactor.Human;
using Reactor.Tests.Support;

namespace Reactor.Tests.Engine;

public class HumanGateTests
{
    private const string Review = """
        digraph Review {
            start [shape=Mdiamond, label="Start"]
            exit  [shape=Msquare, label="Exit"]
            review_gate [shape=hexagon, label="Review Changes", type="wait.human"]
            ship_it [prompt="ship"]
            fixes [prompt="fix"]
            start -> review_gate
            review_gate -> ship_it [label="[A] Approve"]
            review_gate -> fixes   [label="[F] Fix"]
            ship_it -> exit
            fixes -> review_gate
        }
        """;

    [Fact]
    public async Task Presents_edge_labels_as_choices_and_routes_on_selection()
    {
        using var dir = new TempDir();
        var recording = new RecordingInterviewer(new QueueInterviewer("F", "Approve"));
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(interviewer: recording).RunAsync(Review, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "review_gate", "fixes", "review_gate", "ship_it", "exit"], result.CompletedNodes);
        var question = recording.Recordings[0].Question;
        Assert.Equal("Review Changes", question.Text);
        Assert.Equal(QuestionType.MultipleChoice, question.Type);
        Assert.Equal([("A", "[A] Approve"), ("F", "[F] Fix")], question.Options.Select(o => (o.Key, o.Label)));
        Assert.Equal("A", result.Context["human.gate.selected"]);
        Assert.Equal("[A] Approve", result.Context["human.gate.label"]);
        Assert.Equal(2, events.OfType<InterviewStarted>().Count());
        Assert.Equal(2, events.OfType<InterviewCompleted>().Count());
    }

    [Fact]
    public async Task Auto_approve_selects_the_first_option()
    {
        using var dir = new TempDir();
        var result = await TestPipelines.Runner(interviewer: new AutoApproveInterviewer()).RunAsync(Review, TestPipelines.Options(dir));
        Assert.Contains("ship_it", result.CompletedNodes);
    }

    [Fact]
    public async Task Skipped_answer_fails_the_gate_and_timeout_uses_the_default_choice()
    {
        using var dir = new TempDir();
        var skipped = await TestPipelines.Runner(interviewer: new QueueInterviewer()).RunAsync(
            Review.Replace("type=\"wait.human\"]", "type=\"wait.human\", goal_gate=true]", StringComparison.Ordinal), TestPipelines.Options(dir, "skip"));
        Assert.Equal(StageStatus.Fail, skipped.NodeOutcomes["review_gate"].Status);
        Assert.Equal("human skipped interaction", skipped.NodeOutcomes["review_gate"].FailureReason);

        var never = new CallbackInterviewer(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Answer.Yes();
        });
        var timedOut = await TestPipelines.Runner(interviewer: never).RunAsync(
            Review.Replace("type=\"wait.human\"]", "type=\"wait.human\", human.timeout=\"200ms\", human.default_choice=\"fixes\"]", StringComparison.Ordinal)
                .Replace("fixes -> review_gate", "fixes -> exit", StringComparison.Ordinal),
            TestPipelines.Options(dir, "timeout"));
        Assert.Equal(["start", "review_gate", "fixes", "exit"], timedOut.CompletedNodes);
    }

    [Fact]
    public async Task Timeout_without_default_requests_retry()
    {
        using var dir = new TempDir();
        var never = new CallbackInterviewer(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Answer.Yes();
        });
        var result = await TestPipelines.Runner(interviewer: never).RunAsync(
            Review.Replace("type=\"wait.human\"]", "type=\"wait.human\", human.timeout=\"100ms\", max_retries=1, allow_partial=false, goal_gate=true]", StringComparison.Ordinal),
            TestPipelines.Options(dir));
        Assert.Contains("max retries exceeded", result.NodeOutcomes["review_gate"].FailureReason, StringComparison.Ordinal);
        Assert.Contains("human gate timeout", result.NodeOutcomes["review_gate"].FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freeform_gate_collects_text()
    {
        const string source = """
            digraph F {
                start [shape=Mdiamond] exit [shape=Msquare]
                ask [shape=hexagon, label="Any feedback?", human.input="freeform"]
                start -> ask -> exit
            }
            """;
        using var dir = new TempDir();
        var result = await TestPipelines.Runner(interviewer: new QueueInterviewer("Looks great")).RunAsync(source, TestPipelines.Options(dir));
        Assert.Equal("Looks great", result.Context["human.gate.text"]);
    }

    [Fact]
    public async Task Console_interviewer_reads_keys_labels_and_indices()
    {
        var output = new StringWriter();
        var interviewer = new ConsoleInterviewer(new StringReader("nonsense\nf\n2\ny\nfree text\n"), output);
        var question = new Question { Text = "Pick", Options = [new Option("A", "[A] Approve"), new Option("F", "[F] Fix")] };

        Assert.Equal("F", (await interviewer.AskAsync(question)).Value);
        Assert.Equal("F", (await interviewer.AskAsync(question)).Value);
        Assert.True((await interviewer.AskAsync(new Question { Text = "Sure?", Type = QuestionType.YesNo })).IsYes);
        Assert.Equal("free text", (await interviewer.AskAsync(new Question { Text = "Say", Type = QuestionType.Freeform })).Text);
        Assert.Contains("[A] Approve", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("not one of the options", output.ToString(), StringComparison.Ordinal);
        Assert.True((await interviewer.AskAsync(new Question { Text = "eof" })).IsSkipped);
    }

    [Fact]
    public async Task Built_in_interviewers_follow_their_contracts()
    {
        var yesNo = new Question { Text = "ok?", Type = QuestionType.YesNo };
        var choice = new Question { Text = "pick", Options = [new Option("X", "x"), new Option("Y", "y")] };
        var auto = new AutoApproveInterviewer();
        Assert.True((await auto.AskAsync(yesNo)).IsYes);
        Assert.Equal("X", (await auto.AskAsync(choice)).SelectedOption!.Key);
        Assert.Equal("auto-approved", (await auto.AskAsync(new Question { Text = "free", Type = QuestionType.Freeform })).Value);

        var callback = new CallbackInterviewer(q => Answer.FromText(q.Text.ToUpperInvariant()));
        Assert.Equal("PICK", (await callback.AskAsync(choice)).Value);

        var queue = new QueueInterviewer(Answer.No());
        Assert.Equal(AnswerValue.No, (await queue.AskAsync(yesNo)).Special);
        Assert.True((await queue.AskAsync(yesNo)).IsSkipped);

        IInterviewer recorder = new RecordingInterviewer(auto);
        var answers = await recorder.AskMultipleAsync([yesNo, choice]);
        Assert.Equal(2, answers.Count);
        Assert.Equal(2, ((RecordingInterviewer)recorder).Recordings.Count);
    }
}

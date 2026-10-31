using System.Text.RegularExpressions;
using Reactor.Events;

namespace Reactor.Cli.Tests;

public class ConsoleReporterTests
{
    private static readonly TimeSpan S1 = TimeSpan.FromSeconds(1);

    [Fact]
    public void A_single_active_stage_keeps_the_compact_output()
    {
        var lines = Render(
            color: false,
            new StageStarted("plan", "Plan", 1, "codergen", 1),
            new StageLog("plan", "copilot-cli: starting session 1234 (gpt-5-mini)"),
            new StageLog("plan", "→ view README.md"),
            new StageCompleted("plan", 1, TimeSpan.FromSeconds(1.5), "success", ""));

        Assert.Equal(
            [
                "● plan [codergen]",
                "  │ copilot-cli: starting session 1234 (gpt-5-mini)",
                "  │ → view README.md",
                "✔ plan success (1.5s)",
            ],
            lines);
    }

    [Fact]
    public void Concurrent_branches_name_the_stage_on_every_activity_line_until_the_parallel_section_ends()
    {
        var lines = Render(color: false, ParallelRun());

        Assert.Equal(
            [
                "● fork [parallel]",
                "  ⇉ fork: 2 branches",
                "● proto_a [codergen]",
                "● proto_b [codergen]",
                "  │ proto_a: copilot-cli session 3589 (claude-sonnet-5.5)",
                "  │ proto_b: copilot-cli session 4e96 resumed (gpt-6.1-sol)",
                "  │ proto_b → view docs/GAME_DESIGN.md",
                "  │ proto_a [subagent] → grep foo",
                "  │ proto_a: copilot warning: slow down",
                "  ✖ proto_a: timed out (will retry)",
                "  ↻ proto_a retry #2 in 2s",
                "● proto_a [codergen] attempt 2",
                "✔ proto_b success (3.0s)",
                "  ⇢ proto_b: prototype v1 → proto_b/index.html (2 KB)",
                "  │ proto_a → edit main.js",
                "✔ proto_a success (4.0s)",
                "  ✔ branch proto_b (3.0s)",
                "  ✔ branch proto_a (4.0s)",
                "  ⇉ fork: 2 succeeded, 0 failed",
                "✔ fork success (5.0s)",
                "● judge [codergen]",
                "  │ → view proto_a/index.html",
                "✔ judge success (1.0s)",
            ],
            lines);
    }

    [Fact]
    public void Concurrent_branches_get_a_stable_color_each_and_no_color_output_has_no_escape_codes()
    {
        var colored = Render(color: true, ParallelRun());
        var plain = Render(color: false, ParallelRun());

        string ColorOf(string node)
        {
            var codes = colored
                .SelectMany(line => Regex.Matches(line, @"\u001b\[(\d+)m" + node + @"\b").Select(m => m.Groups[1].Value))
                .Distinct()
                .ToList();
            return Assert.Single(codes);
        }

        // Header lines are bold plus the stage color; every other mention uses the stage color alone.
        Assert.NotEqual(ColorOf("proto_a"), ColorOf("proto_b"));
        Assert.Contains(colored, line => line.StartsWith("  │ \u001b[", StringComparison.Ordinal) && line.Contains("proto_b\u001b[0m → view", StringComparison.Ordinal));
        Assert.Equal("  │ → view proto_a/index.html", colored[^2]);
        Assert.DoesNotContain(plain, line => line.Contains('\u001b', StringComparison.Ordinal));
    }

    [Fact]
    public void Branches_that_run_one_at_a_time_keep_the_compact_output()
    {
        var lines = Render(
            color: false,
            new StageStarted("fork", "fork", 1, "parallel", 1),
            new ParallelStarted("fork", 2),
            new StageStarted("a", "a", 2, "codergen", 1),
            new StageLog("a", "→ view x"),
            new StageCompleted("a", 2, S1, "success", ""),
            new StageStarted("b", "b", 3, "codergen", 1),
            new StageLog("b", "→ view y"),
            new StageCompleted("b", 3, S1, "success", ""),
            new ParallelCompleted("fork", S1, 2, 0),
            new StageCompleted("fork", 1, S1, "success", ""));

        Assert.Contains("  │ → view x", lines);
        Assert.Contains("  │ → view y", lines);
    }

    private static PipelineEvent[] ParallelRun() =>
    [
        new StageStarted("fork", "fork", 3, "parallel", 1),
        new ParallelStarted("fork", 2),
        new ParallelBranchStarted("fork", "proto_a", 0),
        new StageStarted("proto_a", "A", 4, "codergen", 1),
        new ParallelBranchStarted("fork", "proto_b", 1),
        new StageStarted("proto_b", "B", 5, "codergen", 1),
        new StageLog("proto_a", "copilot-cli: starting session 3589 (claude-sonnet-5.5)"),
        new StageLog("proto_b", "copilot-cli: resuming session 4e96 (gpt-6.1-sol)"),
        new StageLog("proto_b", "→ view docs/GAME_DESIGN.md"),
        new StageLog("proto_a", "[subagent] → grep foo"),
        new StageLog("proto_a", "assistant: thinking", "debug"),
        new StageLog("proto_a", "copilot warning: slow down", "warn"),
        new StageFailed("proto_a", 4, "timed out", WillRetry: true),
        new StageRetrying("proto_a", 4, 2, TimeSpan.FromSeconds(2)),
        new StageStarted("proto_a", "A", 4, "codergen", 2),
        new StageCompleted("proto_b", 5, TimeSpan.FromSeconds(3), "success", ""),
        new ArtifactProduced("proto_b", "prototype", "file", "proto_b/index.html", 2048, 1, null),
        new StageLog("proto_a", "→ edit main.js"),
        new StageCompleted("proto_a", 4, TimeSpan.FromSeconds(4), "success", ""),
        new ParallelBranchCompleted("fork", "proto_b", 1, TimeSpan.FromSeconds(3), true),
        new ParallelBranchCompleted("fork", "proto_a", 0, TimeSpan.FromSeconds(4), true),
        new ParallelCompleted("fork", TimeSpan.FromSeconds(5), 2, 0),
        new StageCompleted("fork", 3, TimeSpan.FromSeconds(5), "success", ""),
        new StageStarted("judge", "Judge", 6, "codergen", 1),
        new StageLog("judge", "→ view proto_a/index.html"),
        new StageCompleted("judge", 6, S1, "success", ""),
    ];

    [Fact]
    public void Flow_control_events_are_visible()
    {
        var lines = Render(
            color: false,
            new FailedStageContinued("build", "readme", "compile error"),
            new VisitLimitReached("polish", 3, "approve_gate"),
            new MetricsRecorded("qa", new Dictionary<string, double> { ["qa_failed"] = 2, ["score"] = 4.5 }),
            new BudgetExceeded(3.0, 2.0, "deploy"));

        Assert.Equal(
            [
                "  ⚠ build failed; continuing to readme along an unconditional edge (nothing routes its failure)",
                "  ⤳ polish reached its visit limit (3) → approve_gate",
                "  ≡ qa_failed=2, score=4.5",
                "  $ budget reached: $3.00 of $2.00; stopping before deploy",
            ],
            lines);
    }

    [Fact]
    public void The_summary_reports_usage_and_how_to_continue_after_a_budget_stop()
    {
        using var output = new StringWriter();
        var reporter = new ConsoleReporter(output, Verbosity.Normal, color: false, json: false);
        reporter.Summary(new Reactor.Execution.PipelineResult
        {
            RunId = "r1",
            LogsRoot = "C:/runs/r1",
            Status = Reactor.Execution.StageStatus.Fail,
            FinalOutcome = Reactor.Execution.Outcome.Fail("Budget exceeded: $3.00 spent, max_cost is $2.00."),
            CompletedNodes = ["start", "a", "b"],
            Usage = new Dictionary<string, double> { ["cost_usd"] = 3, ["ai_units"] = 300, ["input_tokens"] = 1_200_000, ["output_tokens"] = 40_000 },
            BudgetExceeded = true,
        });

        var text = output.ToString();
        Assert.Contains("■ Budget reached", text);
        Assert.Contains("reactor resume \"C:/runs/r1\" --max-cost <higher budget>", text);
        Assert.Contains("usage:  $3.00 · 300 AI units · 1.2M in / 40K out tokens", text);
        Assert.DoesNotContain("Pipeline failed", text);
    }

    private static string[] Render(bool color, params PipelineEvent[] events)
    {
        using var output = new StringWriter();
        var reporter = new ConsoleReporter(output, Verbosity.Normal, color, json: false);
        foreach (var evt in events)
        {
            reporter.OnEvent(evt);
        }

        return output.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
    }
}

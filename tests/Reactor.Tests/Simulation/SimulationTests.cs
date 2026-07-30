using System.Diagnostics;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;
using Reactor.Simulation;
using Reactor.Tests.Support;

namespace Reactor.Tests.Simulation;

[Collection(Sim.Collection)]
public sealed class SimulationTests
{
    [Fact]
    public async Task Tool_commands_are_never_executed()
    {
        using var dir = new TempDir();
        var marker = dir.Combine("marker.txt").Replace('\\', '/');
        var dot = $$"""
            digraph Tools {
                start [shape=Mdiamond] exit [shape=Msquare]
                touch [shape=parallelogram, tool_command="echo hacked> \"{{marker}}\""]
                start -> touch -> exit
            }
            """;

        // Sanity check: the real engine does run the command.
        var real = await TestPipelines.Runner().RunAsync(dot, TestPipelines.Options(dir, "real"), TestContext.Current.CancellationToken);
        Assert.Equal(StageStatus.Success, real.Status);
        Assert.True(File.Exists(marker), "the command should work when executed for real");
        File.Delete(marker);

        var trace = await Sim.TraceAsync(dot);
        var report = await Sim.ReportAsync(dot, runs: 20);
        var sample = new SimulationSample(SimulationScenario.Empty, 1) { RealTime = true };
        var managed = await sample.CreateRunner(new PipelineRunnerOptions { LogsBaseDirectory = dir.Path })
            .RunAsync(dot, TestPipelines.Options(dir, "sim") with { Interviewer = sample.CreateInterviewer(Sim.Prepare(dot)) }, TestContext.Current.CancellationToken);

        Assert.Equal("success", trace.Status);
        Assert.Equal(20, report.Outcomes["success"]);
        Assert.Equal(StageStatus.Success, managed.Status);
        Assert.False(File.Exists(marker), "simulation must never execute tool commands");
        Assert.Equal($"[Simulated] echo hacked> \"{marker}\"", managed.Context["tool.output"]);
        Assert.Equal(0L, managed.Context["tool.exit_code"]);
        Assert.False(File.Exists(Path.Combine(managed.LogsRoot, "touch", "stdout.txt")));
    }

    [Fact]
    public async Task Declared_outputs_are_recorded_without_touching_the_working_directory()
    {
        const string dot = """
            digraph Pins {
                start [shape=Mdiamond] exit [shape=Msquare]
                plan [prompt="plan", outputs="plan, spec:docs/spec.md"]
                build [shape=parallelogram, tool_command="make", inputs="plan, spec", outputs="log"]
                start -> plan -> build -> exit
            }
            """;

        var trace = await Sim.TraceAsync(dot);

        Assert.Equal("success", trace.Status);
        var produced = trace.Events.OfType<ArtifactProduced>().Select(e => (e.NodeId, e.Name, e.Kind, e.Path)).ToList();
        Assert.Equal([("plan", "plan", "text", null), ("plan", "spec", "file", "docs/spec.md"), ("build", "log", "text", null)], produced);
        Assert.Null(trace.Events.OfType<ArtifactProduced>().Single(e => e.Name == "spec").Sha256);
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "docs", "spec.md")));
    }

    [Fact]
    public async Task Scripted_outcomes_drive_the_exact_path_through_a_test_fix_loop()
    {
        var trace = await Sim.TraceAsync(Sim.TestFixLoop, """{"nodes": {"test": {"script": ["fail", "fail", "success"]}}}""");

        Assert.Equal(["start", "implement", "test", "fix", "test", "fix", "test", "exit"], trace.Path);
        Assert.Equal("success", trace.Kind);
        Assert.Equal(8, trace.Steps);
        var testOutcomes = trace.Events.OfType<StageCompleted>().Where(e => e.NodeId == "test").Select(e => e.Status);
        Assert.Equal(["fail", "fail", "success"], testOutcomes);
    }

    [Fact]
    public async Task Scripts_are_consumed_by_engine_retries_too()
    {
        const string dot = """
            digraph Retry {
                start [shape=Mdiamond] exit [shape=Msquare]
                flaky [prompt="flaky", max_retries=2]
                start -> flaky -> exit
            }
            """;

        var trace = await Sim.TraceAsync(dot, """{"nodes": {"flaky": {"script": ["retry", "fail", "success"]}}}""");

        Assert.Equal(["start", "flaky", "exit"], trace.Path);
        Assert.Equal([1, 2, 3], trace.Events.OfType<StageStarted>().Where(e => e.NodeId == "flaky").Select(e => e.Attempt));
        Assert.Equal(2, trace.Events.OfType<StageRetrying>().Count());
        Assert.Equal("success", trace.Events.OfType<StageCompleted>().Single(e => e.NodeId == "flaky").Status);
    }

    [Fact]
    public async Task Scripted_labels_drive_preferred_label_routing()
    {
        var trace = await Sim.TraceAsync(Sim.LabeledReview, """{"nodes": {"review": {"script": ["success:Fix", "success:Approve"]}}}""");

        Assert.Equal(["start", "write", "review", "fix", "review", "exit"], trace.Path);
        var selections = trace.Events.OfType<EdgeSelected>().Where(e => e.From == "review").ToList();
        Assert.Equal(["fix", "exit"], selections.Select(e => e.To));
        Assert.All(selections, e => Assert.Equal(EdgeSelector.ByPreferredLabel, e.Reason));
    }

    [Fact]
    public async Task Scripted_choices_drive_human_gates_and_gate_context_updates_apply()
    {
        var trace = await Sim.TraceAsync(Sim.ShipGate, """{"nodes": {"gate": {"script": ["success:Rework"]}}}""");

        Assert.Equal(["start", "gate", "rework", "exit"], trace.Path);
        Assert.Equal("[R] [R] Rework", trace.Events.OfType<InterviewCompleted>().Single().Answer);
        Assert.Single(trace.Events.OfType<InterviewStarted>());

        using var dir = new TempDir();
        var graph = Sim.Prepare(Sim.ShipGate);
        var sample = new SimulationSample(Sim.Scenario("""{"nodes": {"gate": {"choices": {"A": 1}, "context_updates": {"success": {"gate.decided": true}}}}}"""), 1);
        var result = await sample.CreateRunner().RunAsync(graph, TestPipelines.Options(dir) with { Interviewer = sample.CreateInterviewer(graph) }, TestContext.Current.CancellationToken);
        Assert.Equal(["start", "gate", "ship", "exit"], result.CompletedNodes);
        Assert.Equal(true, result.Context["gate.decided"]);
        Assert.Equal("A", result.Context["human.gate.selected"]);
    }

    [Fact]
    public async Task Human_gate_outcomes_map_to_skips_and_timeouts()
    {
        const string dot = """
            digraph Gate {
                start [shape=Mdiamond] exit [shape=Msquare]
                gate [shape=hexagon, label="Go?", human.timeout="10m", human.default_choice="later"]
                now [prompt="now"]
                later [prompt="later"]
                start -> gate
                gate -> now [label="[N] Now"]
                gate -> later [label="[L] Later"]
                now -> exit
                later -> exit
            }
            """;

        var skipped = await Sim.TraceAsync(dot, """{"nodes": {"gate": {"script": ["fail"]}}}""");
        var timedOut = await Sim.TraceAsync(dot, """{"nodes": {"gate": {"script": ["retry"]}}}""");
        var slow = await Sim.TraceAsync(dot, """{"nodes": {"gate": {"duration_ms": 900000, "choices": {"now": 1}}}}""");

        Assert.Equal("fail", skipped.Kind);
        Assert.Equal("human skipped interaction", skipped.FailureReason);
        Assert.Equal(["start", "gate", "later", "exit"], timedOut.Path);
        Assert.Single(timedOut.Events.OfType<InterviewTimeout>());
        Assert.Equal(["start", "gate", "later", "exit"], slow.Path);
        Assert.Equal(600_000, slow.SimulatedDurationMs);
    }

    [Fact]
    public async Task Freeform_gates_receive_simulated_input()
    {
        const string dot = """
            digraph Free {
                start [shape=Mdiamond] exit [shape=Msquare]
                ask [shape=hexagon, label="Feedback?", human.input="freeform"]
                start -> ask -> exit
            }
            """;
        using var dir = new TempDir();
        var graph = Sim.Prepare(dot);
        var sample = new SimulationSample(SimulationScenario.Empty, 3);

        var result = await sample.CreateRunner().RunAsync(graph, TestPipelines.Options(dir) with { Interviewer = sample.CreateInterviewer(graph) }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal("Simulated input", result.Context["human.gate.text"]);
    }

    [Fact]
    public async Task Simulated_stages_set_the_context_keys_of_the_real_handlers()
    {
        const string dot = """
            digraph Keys {
                graph [stack.child_dotfile="child.dot"]
                start [shape=Mdiamond] exit [shape=Msquare]
                plan [prompt="plan"]
                run_tests [shape=parallelogram, tool_command="make test"]
                watch [shape=house]
                start -> plan -> run_tests -> watch -> exit
            }
            """;
        using var dir = new TempDir();
        var events = new List<PipelineEvent>();
        var sample = new SimulationSample(Sim.Scenario("""{"nodes": {"run_tests": {"duration_ms": 1500}}}"""), 5);

        var result = await sample.CreateRunner().RunAsync(dot, TestPipelines.Options(dir) with
        {
            OnEvent = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal("plan", result.Context["last_stage"]);
        Assert.Equal("[Simulated] Response for stage: plan", result.Context["last_response"]);
        Assert.Equal("[Simulated] make test", result.Context["tool.output"]);
        Assert.Equal(1500L, result.Context["tool.duration_ms"]);
        Assert.Equal("completed", result.Context["stack.child.status"]);
        Assert.Equal("success", result.Context["context.stack.child.outcome"]);
        Assert.Equal("[Simulated] success", result.NodeOutcomes["plan"].Notes);
        Assert.Equal(1500, sample.SimulatedTimeMs);
        Assert.False(Directory.Exists(Path.Combine(result.LogsRoot, "watch", "child")), "the manager must not launch a child pipeline");
        Assert.False(File.Exists(Path.Combine(result.LogsRoot, "plan", "prompt.md")));
        Assert.Contains(events.OfType<StageLog>(), e => e.NodeId == "plan" && e.Message.StartsWith("[Simulated] success", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Simulated_durations_beyond_the_stage_timeout_time_out_like_the_engine()
    {
        const string dot = """
            digraph Slow {
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="build", timeout="1s", max_retries=1]
                start -> build
                build -> exit [condition="outcome=success"]
            }
            """;

        var trace = await Sim.TraceAsync(dot, """{"nodes": {"build": {"duration_ms": 5000}}}""");

        Assert.Equal("fail", trace.Kind);
        Assert.Equal("Stage 'build' timed out after 1s.", trace.FailureReason);
        Assert.Equal(2, trace.Events.OfType<StageStarted>().Count(e => e.NodeId == "build"));
        Assert.Equal(2000, trace.SimulatedDurationMs);
    }

    [Fact]
    public async Task Real_time_samples_wait_out_simulated_durations()
    {
        const string dot = """
            digraph Timed {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a"]
                b [prompt="b"]
                start -> a -> b -> exit
            }
            """;
        using var dir = new TempDir();
        var sample = new SimulationSample(Sim.Scenario("""{"step_delay_ms": 150}"""), 1) { RealTime = true };
        var stopwatch = Stopwatch.StartNew();

        var result = await sample.CreateRunner().RunAsync(dot, TestPipelines.Options(dir), TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.True(stopwatch.ElapsedMilliseconds >= 280, $"expected two 150 ms stages, took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(300, sample.SimulatedTimeMs);
    }

    [Fact]
    public async Task Interactive_mode_asks_for_every_stage_outcome()
    {
        using var dir = new TempDir();
        var interviewer = new RecordingInterviewer(new QueueInterviewer("S", "F", "S", "success"));
        var events = new List<PipelineEvent>();
        var sample = new SimulationSample(SimulationScenario.Empty, 1, mode: SimulationMode.Interactive);

        var result = await sample.CreateRunner().RunAsync(Sim.TestFixLoop, TestPipelines.Options(dir) with { Interviewer = interviewer, OnEvent = events.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "implement", "test", "fix", "test", "exit"], result.CompletedNodes);
        var question = interviewer.Recordings[0].Question;
        Assert.Equal("Simulate the outcome of 'implement'", question.Text);
        Assert.Equal("implement", question.Stage);
        Assert.Equal(QuestionType.MultipleChoice, question.Type);
        Assert.Equal(["[S] Success", "[F] Fail", "[R] Retry", "[P] Partial success"], question.Options.Select(o => o.Label));
        Assert.Equal(4, events.OfType<InterviewStarted>().Count());
        Assert.Equal(4, events.OfType<InterviewCompleted>().Count());
        Assert.Contains(events.OfType<StageLog>(), e => e.NodeId == "test" && e.Message.Contains("chosen in the walkthrough", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interactive_mode_offers_edge_labels_and_asks_human_gates_for_real()
    {
        const string dot = """
            digraph Walk {
                start [shape=Mdiamond] exit [shape=Msquare]
                review [prompt="review"]
                fix [prompt="fix"]
                gate [shape=hexagon, label="Ship it?"]
                start -> review
                review -> gate [label="[A] Approve"]
                review -> fix [label="Fix"]
                fix -> review
                gate -> exit [label="[S] Ship"]
                gate -> review [label="[B] Back"]
            }
            """;
        using var dir = new TempDir();
        var interviewer = new RecordingInterviewer(new QueueInterviewer("2", "S", "1", "S"));
        var sample = new SimulationSample(SimulationScenario.Empty, 1, mode: SimulationMode.Interactive);

        var result = await sample.CreateRunner().RunAsync(dot, TestPipelines.Options(dir) with { Interviewer = interviewer }, TestContext.Current.CancellationToken);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "review", "fix", "review", "gate", "exit"], result.CompletedNodes);
        Assert.Equal(["[1] → Approve", "[2] → Fix"], interviewer.Recordings[0].Question.Options.Skip(4).Select(o => o.Label));
        Assert.Equal(["1", "2"], interviewer.Recordings[0].Question.Options.Skip(4).Select(o => o.Key));
        Assert.Equal("Ship it?", interviewer.Recordings[3].Question.Text);
    }

    [Fact]
    public async Task Skipped_interactive_questions_fall_back_to_the_scenario()
    {
        using var dir = new TempDir();
        var sample = new SimulationSample(Sim.Scenario("""{"nodes": {"test": {"script": ["fail", "success"]}}}"""), 1, mode: SimulationMode.Interactive);

        var result = await sample.CreateRunner().RunAsync(Sim.TestFixLoop, TestPipelines.Options(dir) with { Interviewer = new QueueInterviewer() }, TestContext.Current.CancellationToken);

        Assert.Equal(["start", "implement", "test", "fix", "test", "exit"], result.CompletedNodes);
    }

    [Fact]
    public async Task Custom_handler_types_are_simulated_with_their_own_defaults()
    {
        const string dot = """
            digraph Custom {
                start [shape=Mdiamond] exit [shape=Msquare]
                lint [type="custom.lint"]
                start -> lint
                lint -> exit [condition="outcome=success"]
            }
            """;
        var graph = Sim.Prepare(dot);
        var scenario = Sim.Scenario("""{"defaults": {"custom.lint": {"outcomes": "fail"}, "codergen": {"outcomes": "success"}}}""");
        var options = new MonteCarloOptions { Runs = 10, CustomHandlerTypes = ["custom.lint"], Seed = 1 };

        var registered = await MonteCarloSimulator.RunAsync(graph, scenario, options, TestContext.Current.CancellationToken);
        var trace = await SimulationTrace.RunAsync(graph, scenario, 1, 0, options, TestContext.Current.CancellationToken);
        var unregistered = await MonteCarloSimulator.RunAsync(graph, scenario, options with { CustomHandlerTypes = [] }, TestContext.Current.CancellationToken);

        Assert.Equal(10, registered.Outcomes["fail"]);
        Assert.Equal("custom.lint", trace.Events.OfType<StageStarted>().Single(e => e.NodeId == "lint").HandlerType);
        Assert.Equal(10, unregistered.Outcomes["success"]);
    }
}

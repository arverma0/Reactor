using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Tests.Support;
using Reactor.Validation;

namespace Reactor.Tests.Engine;

/// <summary>Artifact pins (Reactor extension): <c>inputs</c>/<c>outputs</c> parsing, prompts, the output contract, records, and events.</summary>
public class ArtifactTests
{
    private const string DesignPipeline = """
        digraph D {
            start [shape=Mdiamond] exit [shape=Msquare]
            architect [prompt="Design it", outputs="design:docs/DESIGN.md", max_retries=1]
            fallback [prompt="Recover"]
            start -> architect
            architect -> exit [condition="outcome=success"]
            architect -> fallback [condition="outcome=fail"]
            fallback -> exit
        }
        """;

    // ---- Parsing ----

    [Fact]
    public void Outputs_parse_text_and_file_entries_with_whitespace_and_either_separator()
    {
        var parsed = ArtifactPins.Parse(inputs: null, outputs: " design : docs\\DESIGN.md ,verdict,report:out/./reports//r.txt , plan : a/../plan.md ,");

        Assert.Empty(parsed.Errors);
        Assert.Equal(
            [
                new ArtifactPin("design", ArtifactKind.File, "docs/DESIGN.md"),
                new ArtifactPin("verdict", ArtifactKind.Text, null),
                new ArtifactPin("report", ArtifactKind.File, "out/reports/r.txt"),
                new ArtifactPin("plan", ArtifactKind.File, "plan.md"),
            ],
            parsed.Outputs);
        Assert.Equal("file", parsed.Outputs[0].KindName);
        Assert.Equal("text", parsed.Outputs[1].KindName);
        Assert.Empty(parsed.Inputs);
    }

    [Fact]
    public void Node_attributes_parse_from_dot()
    {
        var graph = DotParser.Parse("""
            digraph G {
                a [inputs=" spec ,design ", outputs="design: docs\DESIGN.md, notes"]
                b [prompt="no pins"]
            }
            """);

        var declared = ArtifactPins.Parse(graph.GetNode("a")!);
        Assert.Equal(["spec", "design"], declared.Inputs);
        Assert.Equal([new ArtifactPin("design", ArtifactKind.File, "docs/DESIGN.md"), new ArtifactPin("notes", ArtifactKind.Text, null)], declared.Outputs);
        Assert.True(ArtifactPins.Declares(graph.GetNode("a")!));
        Assert.False(ArtifactPins.Declares(graph.GetNode("b")!));
        Assert.True(ArtifactPins.Parse(graph.GetNode("b")!).IsEmpty);
    }

    [Theory]
    [InlineData("1design", "'1design' is not a valid artifact name")]
    [InlineData(":x.md", "has no artifact name")]
    [InlineData("docs/DESIGN.md", "give the path after a colon")]
    [InlineData("design:", "the path is empty")]
    [InlineData("design: ", "the path is empty")]
    [InlineData("design:/abs/x.md", "not relative to the working directory")]
    [InlineData("design:\\x.md", "not relative to the working directory")]
    [InlineData("design:C:\\docs\\x.md", "not relative to the working directory")]
    [InlineData("design:~/x.md", "not relative to the working directory")]
    [InlineData("design:../x.md", "leads outside the working directory")]
    [InlineData("design:a/../../x.md", "leads outside the working directory")]
    [InlineData("design:docs/*.md", "wildcard")]
    [InlineData("design:docs/", "names a directory")]
    [InlineData("design:.", "does not name a file")]
    [InlineData("design:docs/a|b.md", "not valid in file names")]
    [InlineData("design:docs\new.md", "control character")]
    [InlineData("design:a.md, design:b.md", "declares 'design' more than once")]
    public void Malformed_outputs_are_reported_with_a_fix(string outputs, string expected)
    {
        var parsed = ArtifactPins.Parse(inputs: null, outputs);

        var error = Assert.Single(parsed.Errors);
        Assert.Equal("outputs", error.Attribute);
        Assert.Contains(expected, error.Message + " " + error.Fix, StringComparison.Ordinal);
        Assert.NotEmpty(error.Fix);
    }

    [Theory]
    [InlineData("spec, spec", "inputs lists 'spec' more than once")]
    [InlineData("spec:docs/SPEC.md", "inputs list artifact names only")]
    [InlineData("my-spec", "'my-spec' is not a valid artifact name")]
    public void Malformed_inputs_are_reported_with_a_fix(string inputs, string expected)
    {
        var error = Assert.Single(ArtifactPins.Parse(inputs, outputs: null).Errors);

        Assert.Equal("inputs", error.Attribute);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_entries_are_kept_next_to_malformed_ones()
    {
        var parsed = ArtifactPins.Parse("spec, 9lives", "good, 1bad, other:x.md");

        Assert.Equal(["spec"], parsed.Inputs);
        Assert.Equal(["good", "other"], parsed.Outputs.Select(p => p.Name));
        Assert.Equal(2, parsed.Errors.Count);
        Assert.True(ArtifactPins.TryNormalizePath(".\\a\\b/../c.txt", out var normalized, out var problem));
        Assert.Equal("a/c.txt", normalized);
        Assert.Null(problem);
        Assert.True(ArtifactPins.TryNormalizePath("~notes.md", out normalized, out _));
        Assert.Equal("~notes.md", normalized);
    }

    // ---- Prompts ----

    [Fact]
    public void Prompt_lists_inputs_and_outputs_between_the_task_and_the_status_protocol()
    {
        var graph = DotParser.Parse("""
            digraph G {
                graph [goal="g"]
                start [shape=Mdiamond] exit [shape=Msquare]
                build [prompt="Build it", inputs="design, verdict, plan, seeded", outputs="code:src/app.py, summary"]
                start -> build -> exit
            }
            """);
        var context = new PipelineContext();
        context.Set("internal.fidelity", FidelityModes.Truncate);
        context.Set("artifacts.design", FileRecord("design", "docs/DESIGN.md", "architect", 1));
        var verdict = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"line {i}"));
        context.Set("artifacts.verdict", new ArtifactRecord { Name = "verdict", Kind = "text", Text = verdict, Producer = "review", Version = 2 }.ToContextValue());
        context.Set("artifacts.seeded", "Ticket ABC-1: fix the login bug");
        var node = graph.GetNode("build")!;
        var backend = new ScriptedBackend { SupportsStatusFile = true };

        var prompt = CodergenHandler.BuildPrompt(node, context, graph, [], "r1", backend, "stage", includeStatusProtocol: true);

        Assert.StartsWith(
            "## Pipeline context\nGoal: g\nRun: r1\n\n---\n\nBuild it\n\n## Inputs\n" +
            "- `design` — file `docs/DESIGN.md` (written by stage `architect`). Read it.\n" +
            "- `verdict` — text from stage `review` (version 2):\n  > line 1\n  > line 2\n",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("  > line 300\n", prompt, StringComparison.Ordinal);
        Assert.Contains("\n  > [… truncated]\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("  > line 1000\n", prompt, StringComparison.Ordinal);
        Assert.Contains("  > line 2000\n", prompt, StringComparison.Ordinal);
        const string verdictHeader = "(version 2):\n";
        var quoted = prompt[(prompt.IndexOf(verdictHeader, StringComparison.Ordinal) + verdictHeader.Length)..prompt.IndexOf("\n- `plan`", StringComparison.Ordinal)];
        Assert.Equal(StageArtifacts.Clip(verdict, StageArtifacts.MaxPromptTextChars), string.Join("\n", quoted.Split('\n').Select(l => l == "  >" ? "" : l[4..])));
        Assert.Contains(
            "- `plan` — not produced yet in this run.\n" +
            "- `seeded` — text:\n  > Ticket ABC-1: fix the login bug\n\n" +
            "## Outputs\nProduce these before you finish:\n" +
            "- `code` → write the file `src/app.py` (relative to the working directory)\n" +
            "- `summary` → your final response is recorded as this artifact\n\n" +
            CodergenHandler.StatusProtocol(node, graph, "stage"),
            prompt,
            StringComparison.Ordinal);
        Assert.EndsWith(CodergenHandler.StatusProtocol(node, graph, "stage"), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Nodes_without_pins_get_exactly_the_prompt_they_got_before()
    {
        var graph = DotParser.Parse("""
            digraph G {
                graph [goal="g"]
                start [shape=Mdiamond] exit [shape=Msquare]
                plain [prompt="Do $goal"]
                start -> plain -> exit
            }
            """);
        var node = graph.GetNode("plain")!;
        var context = new PipelineContext();
        context.Set("internal.fidelity", FidelityModes.Compact);
        context.Set("ticket", "ABC-1");
        context.Set("artifacts.design", FileRecord("design", "docs/DESIGN.md", "architect", 1));
        StageRecord[] history = [new("architect", "architect", StageStatus.Success, "done", "", DateTimeOffset.UnixEpoch, 1)];

        var withProtocol = CodergenHandler.BuildPrompt(node, context, graph, history, "r1", new ScriptedBackend { SupportsStatusFile = true }, "stage", includeStatusProtocol: true);
        var withoutBackend = CodergenHandler.BuildPrompt(node, context, graph, history, "r1", backend: null, "stage", includeStatusProtocol: true);

        const string preamble = "## Pipeline context\nGoal: g\nRun: r1\nCompleted stages:\n- architect: success — done\nKey context:\n- ticket: ABC-1";
        Assert.Equal(preamble + "\n\n---\n\nDo g\n\n" + CodergenHandler.StatusProtocol(node, graph, "stage"), withProtocol);
        Assert.Equal(preamble + "\n\n---\n\nDo g", withoutBackend);
    }

    [Fact]
    public void Preview_prompts_show_declared_inputs_as_not_produced_without_records()
    {
        // The Studio's prompt preview calls the explicit-state overload with a context that has no run services.
        var graph = DotParser.Parse("""
            digraph G {
                start [shape=Mdiamond] exit [shape=Msquare]
                architect [prompt="Design", outputs="design:docs/DESIGN.md"]
                build [prompt="Build", inputs="design", outputs="summary"]
                start -> architect -> build -> exit
            }
            """);
        var context = new PipelineContext();
        context.Set("internal.fidelity", FidelityModes.Full);
        context.Set("internal.retry_count.build", 3);

        var prompt = CodergenHandler.BuildPrompt(graph.GetNode("build")!, context, graph, [], "<run-id>", backend: null, "stage", includeStatusProtocol: true);

        Assert.Equal(
            "Build\n\n## Inputs\n- `design` — not produced yet in this run.\n\n## Outputs\nProduce these before you finish:\n" +
            "- `summary` → your final response is recorded as this artifact",
            prompt);
        Assert.Equal(
            "## Outputs\nProduce these before you finish:\n- `design` → write the file `docs/DESIGN.md` (relative to the working directory)",
            StageArtifacts.PromptSections(graph.GetNode("architect")!, context));
    }

    [Fact]
    public void Preamble_leaves_artifact_records_to_the_inputs_section()
    {
        var graph = DotParser.Parse("digraph G { graph [goal=\"g\"] start [shape=Mdiamond] exit [shape=Msquare] start -> exit }");
        var context = new PipelineContext();
        context.Set("ticket", "ABC-1");
        context.Set("artifacts.design", FileRecord("design", "docs/DESIGN.md", "architect", 1));
        StageRecord[] history = [new("architect", "architect", StageStatus.Success, "done", "", DateTimeOffset.UnixEpoch, 1)];

        var preamble = PreambleBuilder.Build(FidelityModes.SummaryHigh, graph, context, history, "r1");

        Assert.Contains("- ticket: ABC-1", preamble, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts.", preamble, StringComparison.Ordinal);
        Assert.True(PreambleBuilder.IsInternalKey("artifacts.design"));
        Assert.False(PreambleBuilder.IsInternalKey("artifactsfoo"));
    }

    // ---- The output contract ----

    [Fact]
    public async Task Missing_file_output_fails_the_attempt_and_the_retry_can_write_it()
    {
        using var dir = new TempDir();
        var prompts = new List<string>();
        var backend = new ScriptedBackend().On("architect", (_, prompt, ctx) =>
        {
            prompts.Add(prompt);
            if (prompts.Count == 2)
            {
                WriteFile(ctx, "docs/DESIGN.md", "# Design");
            }

            return "designed";
        });
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(DesignPipeline, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(["start", "architect", "exit"], result.CompletedNodes);
        var failed = Assert.Single(events.OfType<StageFailed>());
        Assert.Equal("Declared output `design` (docs/DESIGN.md) was not written.", failed.Error);
        Assert.True(failed.WillRetry);
        Assert.DoesNotContain("previous attempt", prompts[0], StringComparison.Ordinal);
        Assert.Contains("Your previous attempt finished without writing `docs/DESIGN.md`. Write it this time.", prompts[1], StringComparison.Ordinal);
        var record = Record(result.Context, "design");
        Assert.Equal(1L, record["version"]);
        Assert.Equal("architect", record["producer"]);
        Assert.Single(events.OfType<ArtifactProduced>());
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["architect"].Status);
    }

    [Fact]
    public async Task Missing_file_output_without_retries_fails_the_stage_and_routes_on_outcome_fail()
    {
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("architect", "Here is the design (but no file).");
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(
            DesignPipeline.Replace(", max_retries=1", "", StringComparison.Ordinal),
            TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(["start", "architect", "fallback", "exit"], result.CompletedNodes);
        var outcome = result.NodeOutcomes["architect"];
        Assert.Equal(StageStatus.Fail, outcome.Status);
        Assert.Equal("Declared output `design` (docs/DESIGN.md) was not written.", outcome.FailureReason);
        Assert.Equal("architect", outcome.ContextUpdates["last_stage"]);
        Assert.Equal(1, backend.CallCount("architect"));
        Assert.False(result.Context.ContainsKey("artifacts.design"));
        Assert.Empty(events.OfType<ArtifactProduced>());
        var status = JsonNode.Parse(File.ReadAllText(Path.Combine(result.LogsRoot, "architect", "status.json")))!;
        Assert.Equal("fail", status["outcome"]!.GetValue<string>());
        Assert.Equal("Declared output `design` (docs/DESIGN.md) was not written.", status["failure_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_reported_success_without_the_declared_file_fails()
    {
        const string source = """
            digraph S {
                start [shape=Mdiamond] exit [shape=Msquare]
                architect [prompt="Design it", outputs="design:docs/DESIGN.md"]
                ship [prompt="ship"]
                fix [prompt="fix"]
                start -> architect
                architect -> ship [label="Ship"]
                architect -> fix [condition="outcome=fail"]
                ship -> exit
                fix -> exit
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend { SupportsStatusFile = true }.On("architect", (node, prompt, ctx) =>
        {
            Assert.True(prompt.IndexOf("## Outputs", StringComparison.Ordinal) < prompt.IndexOf("## Reporting the stage outcome", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(ctx.Run!.StageDirectory(node.Id), "status.json"),
                """{"outcome":"success","preferred_label":"Ship","notes":"all done","context_updates":{"reviewed":true}}""");
            return "Done.";
        });

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        var outcome = result.NodeOutcomes["architect"];
        Assert.Equal(StageStatus.Fail, outcome.Status);
        Assert.Equal("Declared output `design` (docs/DESIGN.md) was not written.", outcome.FailureReason);
        Assert.Equal("", outcome.PreferredLabel);
        Assert.True(Assert.IsType<bool>(result.Context["reviewed"]));
        Assert.Contains("fix", result.CompletedNodes);
        Assert.DoesNotContain("ship", result.CompletedNodes);
    }

    [Fact]
    public async Task Explicit_backend_outcomes_are_checked_when_successful()
    {
        const string source = """
            digraph E {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="a", outputs="out:a.txt, note"]
                b [prompt="b", outputs="out2:b.txt"]
                c [prompt="c", outputs="out3:c.txt"]
                d [prompt="d", outputs="out4:d.txt, note4"]
                start -> a -> b -> c -> d -> exit
            }
            """;
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("c.txt"));
        var backend = new ScriptedBackend()
            .On("a", CodergenResult.FromOutcome(Outcome.Success("done"), "explicit text"))
            .On("b", CodergenResult.FromOutcome(Outcome.PartialSuccess("mostly")))
            .On("c", CodergenResult.FromOutcome(Outcome.Success("dir")))
            .On("d", CodergenResult.FromOutcome(Outcome.Fail("gave up"), "partial notes"));

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("Declared output `out` (a.txt) was not written.", result.NodeOutcomes["a"].FailureReason);
        Assert.Equal("Declared output `out2` (b.txt) was not written.", result.NodeOutcomes["b"].FailureReason);
        Assert.Equal("Declared output `out3` (c.txt) is a directory, not a file.", result.NodeOutcomes["c"].FailureReason);
        Assert.Equal("gave up", result.NodeOutcomes["d"].FailureReason);
        Assert.DoesNotContain(result.Context.Keys, k => k.StartsWith("artifacts.", StringComparison.Ordinal));
    }

    // ---- Records ----

    [Fact]
    public async Task Text_output_records_the_final_response()
    {
        const string source = "digraph T { start [shape=Mdiamond] exit [shape=Msquare] review [prompt=\"Review\", outputs=\"verdict\"] start -> review -> exit }";
        using var dir = new TempDir();
        var response = "Looks good.\n" + new string('x', 20_000) + "\nShip it.";
        var backend = new ScriptedBackend().On("review", response);

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        var record = Record(result.Context, "verdict");
        Assert.Equal(["name", "kind", "path", "text", "producer", "version", "bytes", "sha256", "produced_at"], record.Keys);
        Assert.Equal("verdict", record["name"]);
        Assert.Equal("text", record["kind"]);
        Assert.Null(record["path"]);
        Assert.Equal("review", record["producer"]);
        Assert.Equal(1L, record["version"]);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(response), record["bytes"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(response))), record["sha256"]);
        var text = Assert.IsType<string>(record["text"]);
        Assert.Equal(StageArtifacts.MaxRecordedTextChars, text.Length);
        Assert.StartsWith("Looks good.\n", text, StringComparison.Ordinal);
        Assert.Contains("\n[… truncated]\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\nShip it.", text, StringComparison.Ordinal);
        Assert.InRange(DateTimeOffset.Parse((string)record["produced_at"]!, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);

        var restored = new PipelineContext();
        restored.Restore(result.Context, []);
        var typed = StageArtifacts.Read(restored, "verdict")!;
        Assert.Equal(1, typed.Version);
        Assert.False(typed.IsFile);
        Assert.Equal(text, typed.Text);
    }

    [Fact]
    public async Task Empty_responses_are_valid_text_artifacts()
    {
        const string source = "digraph T { start [shape=Mdiamond] exit [shape=Msquare] quiet [prompt=\"q\", outputs=\"nothing\"] start -> quiet -> exit }";
        using var dir = new TempDir();

        var result = await TestPipelines.Runner(new ScriptedBackend().On("quiet", "")).RunAsync(source, TestPipelines.Options(dir));

        var record = Record(result.Context, "nothing");
        Assert.Equal("", record["text"]);
        Assert.Equal(0L, record["bytes"]);
        Assert.Equal(StageStatus.Success, result.NodeOutcomes["quiet"].Status);
    }

    [Fact]
    public async Task Tool_outputs_record_stdout_and_require_declared_files()
    {
        const string source = """
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                say    [shape=parallelogram, tool_command="echo hello artifacts", outputs="greeting"]
                write  [shape=parallelogram, tool_command="echo report> report.txt", outputs="report: report.txt, log"]
                forget [shape=parallelogram, tool_command="echo nothing", outputs="missing:out/missing.txt"]
                start -> say -> write -> forget -> exit
            }
            """;
        using var dir = new TempDir();
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add });

        var greeting = Record(result.Context, "greeting");
        Assert.Equal("text", greeting["kind"]);
        Assert.Equal("hello artifacts", greeting["text"]);
        Assert.Equal("say", greeting["producer"]);

        var bytes = File.ReadAllBytes(dir.Combine("report.txt"));
        var report = Record(result.Context, "report");
        Assert.Equal("file", report["kind"]);
        Assert.Equal("report.txt", report["path"]);
        Assert.Null(report["text"]);
        Assert.Equal(bytes.LongLength, report["bytes"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), report["sha256"]);
        Assert.Equal("", Record(result.Context, "log")["text"]);

        var forget = result.NodeOutcomes["forget"];
        Assert.Equal(StageStatus.Fail, forget.Status);
        Assert.Equal("Declared output `missing` (out/missing.txt) was not written.", forget.FailureReason);
        Assert.Equal("nothing", forget.ContextUpdates["tool.output"]);
        Assert.Equal(["greeting", "report", "log"], events.OfType<ArtifactProduced>().Select(e => e.Name));
    }

    [Fact]
    public async Task Tool_inputs_are_passed_as_file_paths_in_environment_variables()
    {
        var print = OperatingSystem.IsWindows()
            ? "type \"%REACTOR_INPUT_NOTES%\" && type \"%REACTOR_INPUT_DESIGN%\""
            : "cat \"$REACTOR_INPUT_NOTES\" && cat \"$REACTOR_INPUT_DESIGN\"";
        var source = $$"""
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                notes [prompt="Take notes", outputs="notes"]
                architect [prompt="Design", outputs="design:docs/DESIGN.md"]
                show [shape=parallelogram, inputs="notes, design", outputs="shown", tool_command="{{print.Replace("\"", "\\\"", StringComparison.Ordinal)}}"]
                start -> notes -> architect -> show -> exit
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend()
            .On("notes", "Remember the edge cases.")
            .On("architect", (_, _, ctx) =>
            {
                WriteFile(ctx, "docs/DESIGN.md", "# Design v1\n");
                return "ok";
            });

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, result.NodeOutcomes["show"].Status);
        var shown = (string)Record(result.Context, "shown")["text"]!;
        Assert.Contains("Remember the edge cases.", shown, StringComparison.Ordinal);
        Assert.Contains("# Design v1", shown, StringComparison.Ordinal);
        Assert.Equal("Remember the edge cases.", File.ReadAllText(Path.Combine(result.LogsRoot, "show", "inputs", "notes.txt")));
    }

    [Fact]
    public async Task Tool_input_environment_names_only_produced_inputs()
    {
        using var dir = new TempDir();
        var graph = DotParser.Parse("digraph G { t [shape=parallelogram, inputs=\"notes, design, later\"] }");
        var context = new PipelineContext();
        context.Set("artifacts.notes", new ArtifactRecord { Name = "notes", Kind = "text", Text = "n" }.ToContextValue());
        context.Set("artifacts.design", FileRecord("design", "docs/DESIGN.md", "architect", 1));

        var environment = await StageArtifacts.ToolInputEnvironmentAsync(graph.GetNode("t")!, context, dir.Path, TestContext.Current.CancellationToken);

        Assert.Equal(["REACTOR_INPUT_DESIGN", "REACTOR_INPUT_NOTES"], environment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Path.GetFullPath(Path.Combine("docs", "DESIGN.md")), environment["REACTOR_INPUT_DESIGN"]);
        Assert.Equal(Path.Combine(dir.Path, "inputs", "notes.txt"), environment["REACTOR_INPUT_NOTES"]);
        Assert.Equal("n", File.ReadAllText(environment["REACTOR_INPUT_NOTES"]));
    }

    [Fact]
    public async Task Versions_increment_each_time_an_artifact_is_produced()
    {
        const string source = """
            digraph L {
                start [shape=Mdiamond] exit [shape=Msquare]
                draft [prompt="Draft", outputs="design:docs/DESIGN.md, notes"]
                check [prompt="Check", inputs="design, notes"]
                start -> draft -> check
                check -> exit [condition="outcome=success"]
                check -> draft [condition="outcome=fail"]
            }
            """;
        using var dir = new TempDir();
        var drafts = 0;
        var prompts = new List<string>();
        var backend = new ScriptedBackend()
            .On("draft", (_, _, ctx) =>
            {
                drafts++;
                WriteFile(ctx, "docs/DESIGN.md", $"# Design v{drafts}");
                return $"notes v{drafts}";
            })
            .On("check", (_, prompt, _) =>
            {
                prompts.Add(prompt);
                return prompts.Count < 3 ? CodergenResult.FromOutcome(Outcome.Fail("not yet")) : CodergenResult.FromText("approved");
            });
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal(3, drafts);
        Assert.Contains("- `design` — file `docs/DESIGN.md` (written by stage `draft`). Read it.", prompts[0], StringComparison.Ordinal);
        Assert.EndsWith("- `notes` — text from stage `draft` (version 2):\n  > notes v2", prompts[1], StringComparison.Ordinal);
        Assert.Contains("- `design` — file `docs/DESIGN.md` (version 3, written by stage `draft`). Read it.", prompts[2], StringComparison.Ordinal);
        Assert.Equal(3L, Record(result.Context, "design")["version"]);
        Assert.Equal("notes v3", Record(result.Context, "notes")["text"]);
        Assert.Equal([1, 2, 3], events.OfType<ArtifactProduced>().Where(e => e.Name == "design").Select(e => e.Version));
        Assert.Equal(3, events.OfType<ArtifactProduced>().Where(e => e.Name == "design").Select(e => e.Sha256).Distinct().Count());
    }

    [Fact]
    public async Task Records_survive_checkpoints_and_resume()
    {
        const string source = """
            digraph R {
                start [shape=Mdiamond] exit [shape=Msquare]
                architect [prompt="Design", outputs="design:docs/DESIGN.md, notes"]
                build [prompt="Build", inputs="design, notes", outputs="summary"]
                start -> architect -> build -> exit
            }
            """;
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var first = new ScriptedBackend()
            .On("architect", (_, _, ctx) =>
            {
                WriteFile(ctx, "docs/DESIGN.md", "# Design");
                return "use sqlite";
            })
            .On("build", (_, _, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var interrupted = await TestPipelines.Runner(first).RunAsync(source, TestPipelines.Options(dir), cts.Token);
        Assert.True(interrupted.Cancelled);

        var checkpoint = Checkpoint.Load(Path.Combine(interrupted.LogsRoot, Checkpoint.FileName));
        var saved = Assert.IsType<Dictionary<string, object?>>(checkpoint.ContextValues["artifacts.design"]);
        Assert.Equal("docs/DESIGN.md", saved["path"]);
        Assert.Equal(1L, saved["version"]);
        Assert.Equal(8L, saved["bytes"]);
        Assert.Null(saved["text"]);
        Assert.Equal("use sqlite", Assert.IsType<Dictionary<string, object?>>(checkpoint.ContextValues["artifacts.notes"])["text"]);
        Assert.IsType<Dictionary<string, object?>>(checkpoint.NodeOutcomes["architect"].ContextUpdates["artifacts.design"]);

        string? resumedPrompt = null;
        var second = new ScriptedBackend().On("build", (_, prompt, _) =>
        {
            resumedPrompt = prompt;
            return "built";
        });
        var resumed = await TestPipelines.Runner(second).ResumeAsync(interrupted.LogsRoot, TestPipelines.Options(dir));

        Assert.Equal(StageStatus.Success, resumed.Status);
        Assert.Contains("- `design` — file `docs/DESIGN.md` (written by stage `architect`). Read it.", resumedPrompt, StringComparison.Ordinal);
        Assert.Contains("- `notes` — text from stage `architect`:\n  > use sqlite\n", resumedPrompt, StringComparison.Ordinal);
        Assert.Equal(saved, Record(resumed.Context, "design"));
        Assert.Equal("built", Record(resumed.Context, "summary")["text"]);
    }

    [Fact]
    public async Task ArtifactProduced_events_reach_observers_and_the_event_log()
    {
        const string source = """
            digraph E {
                start [shape=Mdiamond] exit [shape=Msquare]
                architect [prompt="Design", outputs="design: docs\DESIGN.md, summary"]
                start -> architect -> exit
            }
            """;
        using var dir = new TempDir();
        var backend = new ScriptedBackend().On("architect", (_, _, ctx) =>
        {
            WriteFile(ctx, "docs/DESIGN.md", "# Design\n");
            return "summary text";
        });
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner(backend).RunAsync(source, TestPipelines.Options(dir) with { OnEvent = events.Add });

        var produced = events.OfType<ArtifactProduced>().ToList();
        Assert.Equal(2, produced.Count);
        var file = File.ReadAllBytes(dir.Combine("docs", "DESIGN.md"));
        Assert.Equal(
            new ArtifactProduced("architect", "design", "file", "docs/DESIGN.md", file.LongLength, 1, Convert.ToHexStringLower(SHA256.HashData(file))) { RunId = result.RunId, Timestamp = produced[0].Timestamp },
            produced[0]);
        Assert.Equal(
            new ArtifactProduced("architect", "summary", "text", null, 12, 1, Convert.ToHexStringLower(SHA256.HashData("summary text"u8))) { RunId = result.RunId, Timestamp = produced[1].Timestamp },
            produced[1]);
        var index = events.IndexOf(produced[0]);
        Assert.IsType<StageCompleted>(events.Skip(index).First(e => e is StageCompleted or StageStarted));

        var lines = File.ReadAllLines(Path.Combine(result.LogsRoot, "events.jsonl"))
            .Select(l => JsonNode.Parse(l)!)
            .Where(j => j["type"]!.GetValue<string>() == "ArtifactProduced")
            .ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("architect", lines[0]["node_id"]!.GetValue<string>());
        Assert.Equal("design", lines[0]["name"]!.GetValue<string>());
        Assert.Equal("file", lines[0]["kind"]!.GetValue<string>());
        Assert.Equal("docs/DESIGN.md", lines[0]["path"]!.GetValue<string>());
        Assert.Equal(file.LongLength, lines[0]["bytes"]!.GetValue<long>());
        Assert.Equal(1, lines[0]["version"]!.GetValue<int>());
        Assert.Equal(64, lines[0]["sha256"]!.GetValue<string>().Length);
        Assert.Equal(result.RunId, lines[0]["run_id"]!.GetValue<string>());
        Assert.Contains("design", lines[0]["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(lines[1]["path"]);
        Assert.Equal("text", lines[1]["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Simulation_records_file_outputs_without_writing_them()
    {
        using var dir = new TempDir();
        var events = new List<PipelineEvent>();

        var result = await TestPipelines.Runner().RunAsync(DesignPipeline, TestPipelines.Options(dir) with { OnEvent = events.Add });

        Assert.Equal(["start", "architect", "exit"], result.CompletedNodes);
        Assert.False(File.Exists(dir.Combine("docs", "DESIGN.md")));
        var record = Record(result.Context, "design");
        Assert.Equal(0L, record["bytes"]);
        Assert.Null(record["sha256"]);
        Assert.Equal("design", Assert.Single(events.OfType<ArtifactProduced>()).Name);
    }

    [Fact]
    public async Task Records_made_in_parallel_branches_stay_in_their_branch()
    {
        const string source = """
            digraph P {
                start [shape=Mdiamond] exit [shape=Msquare]
                fork [shape=component]
                a [prompt="a", outputs="review_a"]
                a2 [prompt="a2", inputs="review_a"]
                b [prompt="b", outputs="review_b"]
                join [shape=tripleoctagon]
                after [prompt="after", inputs="review_a"]
                start -> fork
                fork -> a
                fork -> b
                a -> a2 -> join
                b -> join
                join -> after -> exit
            }
            """;
        using var dir = new TempDir();
        var prompts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        CodergenResult Capture(Node node, string prompt, PipelineContext ctx)
        {
            prompts[node.Id] = prompt;
            return "review by " + node.Id;
        }

        var backend = new ScriptedBackend().On("a", Capture).On("a2", Capture).On("b", Capture).On("after", Capture);
        var events = new List<PipelineEvent>();

        var runner = TestPipelines.Runner(backend);
        var diagnostics = runner.Prepare(source).Diagnostics;
        var result = await runner.RunAsync(source, TestPipelines.Options(dir) with
        {
            OnEvent = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }
            },
        });

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Contains("- `review_a` — text from stage `a`:\n  > review by a", prompts["a2"], StringComparison.Ordinal);
        Assert.Contains("- `review_a` — not produced yet in this run.", prompts["after"], StringComparison.Ordinal);
        Assert.False(result.Context.ContainsKey("artifacts.review_a"));
        Assert.Equal(["review_a", "review_b"], events.OfType<ArtifactProduced>().Select(e => e.Name).Order(StringComparer.Ordinal));
        var warning = Assert.Single(diagnostics, d => d.Rule == "artifact_order");
        Assert.Equal("after", warning.NodeId);
        Assert.Contains("parallel branches", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_read_leniently_from_context_values()
    {
        Assert.Null(ArtifactRecord.FromContextValue("x", null));

        var seeded = ArtifactRecord.FromContextValue("ticket", "Fix the login bug")!;
        Assert.Equal("text", seeded.Kind);
        Assert.Equal("Fix the login bug", seeded.Text);
        Assert.Equal(0, seeded.Version);
        Assert.Equal("", seeded.Producer);

        var parsed = ArtifactRecord.FromContextValue("design", ContextValues.FromJson(JsonNode.Parse("""{"path":"docs/D.md","version":4,"bytes":"12"}""")))!;
        Assert.True(parsed.IsFile);
        Assert.Equal("design", parsed.Name);
        Assert.Equal(4, parsed.Version);
        Assert.Equal(12, parsed.Bytes);

        var record = new ArtifactRecord { Name = "n", Kind = "text", Text = "t", Producer = "p", Version = 2, Bytes = 1, Sha256 = "ab", ProducedAt = DateTimeOffset.UnixEpoch };
        Assert.Equal(record, ArtifactRecord.FromContextValue("n", ContextValues.FromJson(ContextValues.ToJson(record.ToContextValue()))));
    }

    [Fact]
    public void Clip_keeps_the_beginning_and_the_end()
    {
        Assert.Equal("short", StageArtifacts.Clip("short", 10));
        var clipped = StageArtifacts.Clip(new string('a', 100) + new string('z', 100), 60);
        Assert.Equal(60, clipped.Length);
        Assert.StartsWith("aaaa", clipped, StringComparison.Ordinal);
        Assert.EndsWith("zzzz", clipped, StringComparison.Ordinal);
        Assert.Contains("\n[… truncated]\n", clipped, StringComparison.Ordinal);

        // Cuts at line ends do not leave blank lines around the marker.
        var tidy = StageArtifacts.Clip(string.Concat(Enumerable.Repeat("abc\n", 50)), 31);
        Assert.Equal("abc\nabc\nabc\n[… truncated]\nabc\n", tidy);

        var emoji = string.Concat(Enumerable.Repeat("😀", 100));
        var safe = StageArtifacts.Clip(emoji, 51);
        Assert.False(char.IsLowSurrogate(safe[0]));
        Assert.False(char.IsHighSurrogate(safe[safe.IndexOf('\n', StringComparison.Ordinal) - 1]));
        Assert.False(char.IsLowSurrogate(safe[safe.LastIndexOf('\n') + 1]));
    }

    private static Dictionary<string, object?> FileRecord(string name, string path, string producer, int version) =>
        new ArtifactRecord { Name = name, Kind = "file", Path = path, Producer = producer, Version = version, Bytes = 10 }.ToContextValue();

    private static Dictionary<string, object?> Record(IReadOnlyDictionary<string, object?> context, string name) =>
        Assert.IsType<Dictionary<string, object?>>(context[ArtifactPins.ContextKey(name)]);

    private static void WriteFile(PipelineContext context, string relativePath, string content)
    {
        var path = Path.Combine(context.Run!.WorkingDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

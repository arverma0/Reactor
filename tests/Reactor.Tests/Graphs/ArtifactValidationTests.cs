using Reactor.Graphs;
using Reactor.Validation;

namespace Reactor.Tests.Graphs;

/// <summary>The artifact pin lint rules (Reactor extension).</summary>
public class ArtifactValidationTests
{
    private const string Valid = """
        digraph G {
            start [shape=Mdiamond] exit [shape=Msquare]
            architect [prompt="Design", outputs="design:docs/DESIGN.md"]
            build [prompt="Build", inputs="design", outputs="summary"]
            check [shape=parallelogram, tool_command="echo ok", inputs="summary", outputs="report"]
            start -> architect -> build -> check -> exit
        }
        """;

    private static IReadOnlyList<Diagnostic> Lint(string source) => PipelineValidator.Validate(DotParser.Parse(source));

    private static string Edit(string source, string from, string to)
    {
        Assert.Contains(from, source, StringComparison.Ordinal);
        return source.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Well_formed_pins_have_no_artifact_diagnostics()
    {
        Assert.DoesNotContain(Lint(Valid), d => d.Rule.StartsWith("artifact_", StringComparison.Ordinal));
        Assert.Contains(PipelineValidator.BuiltInRules(), r => r.Name == "artifact_syntax");
        Assert.Equal(31, PipelineValidator.BuiltInRules().Count);
    }

    [Fact]
    public void Malformed_entries_are_errors_with_fixes()
    {
        var diagnostics = Lint(Edit(
            Edit(Valid, "outputs=\"design:docs/DESIGN.md\"", "outputs=\"design:docs/DESIGN.md, design:x.md, 2nd, abs:/abs/x.md, up:../x.md, empty:\""),
            "inputs=\"design\"",
            "inputs=\"design, design, bad-name\""));

        var syntax = diagnostics.Where(d => d.Rule == "artifact_syntax").ToList();
        Assert.Equal(7, syntax.Count);
        Assert.All(syntax, d => Assert.Equal(Severity.Error, d.Severity));
        Assert.All(syntax, d => Assert.False(string.IsNullOrEmpty(d.Fix)));
        Assert.Equal(5, syntax.Count(d => d.NodeId == "architect"));
        Assert.Contains(syntax, d => d.Message == "outputs declares 'design' more than once.");
        Assert.Contains(syntax, d => d.Message == "outputs entry '2nd': '2nd' is not a valid artifact name.");
        Assert.Contains(syntax, d => d.Message == "outputs entry 'abs:/abs/x.md': the path is not relative to the working directory.");
        Assert.Contains(syntax, d => d.Message == "outputs entry 'up:../x.md': the path leads outside the working directory.");
        Assert.Contains(syntax, d => d.Message == "outputs entry 'empty:': the path is empty.");
        Assert.Contains(syntax, d => d.NodeId == "build" && d.Message == "inputs lists 'design' more than once.");
        Assert.Contains(syntax, d => d.NodeId == "build" && d.Message == "inputs entry 'bad-name': 'bad-name' is not a valid artifact name.");

        Assert.Throws<ValidationException>(() => PipelineValidator.ValidateOrRaise(DotParser.Parse(Edit(Valid, "outputs=\"summary\"", "outputs=\"summary:\""))));
    }

    [Fact]
    public void Inputs_that_no_stage_produces_are_warnings()
    {
        var diagnostics = Lint(Edit(Valid, "inputs=\"design\"", "inputs=\"design, plan\""));

        var warning = Assert.Single(diagnostics, d => d.Rule == "artifact_inputs");
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal("build", warning.NodeId);
        Assert.Equal("Input 'plan' is not produced by any stage, so it is never available.", warning.Message);
        Assert.StartsWith("Add outputs=\"plan:…\" to the stage that produces it", warning.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Rule == "artifact_order");
    }

    [Fact]
    public void Inputs_whose_producers_cannot_run_earlier_are_warnings()
    {
        var late = Edit(Valid, "inputs=\"design\"", "inputs=\"design, report\"");

        var warning = Assert.Single(Lint(late), d => d.Rule == "artifact_order");
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal("build", warning.NodeId);
        Assert.Equal("Input 'report' is produced only by 'check', which cannot run before this stage, so it is never available.", warning.Message);
        Assert.NotNull(warning.Fix);

        // A loop back to the consumer makes the input available on later visits.
        var looped = Edit(late, "check -> exit", "check -> exit [condition=\"outcome=success\"]\n check -> build [condition=\"outcome=fail\"]");
        Assert.DoesNotContain(Lint(looped), d => d.Rule.StartsWith("artifact_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stage_consuming_its_own_output_needs_a_loop()
    {
        var self = Edit(Valid, "inputs=\"summary\"", "inputs=\"report\"");
        Assert.Contains(Lint(self), d => d.Rule == "artifact_order" && d.NodeId == "check");

        var looped = Edit(self, "check -> exit", "check -> exit [condition=\"outcome=success\"]\n check -> check [condition=\"outcome=fail\"]");
        Assert.DoesNotContain(Lint(looped), d => d.Rule == "artifact_order");
    }

    [Fact]
    public void Inputs_produced_in_parallel_branches_reach_stages_after_the_join_but_not_sibling_branches()
    {
        var diagnostics = Lint("""
            digraph P {
                start [shape=Mdiamond] exit [shape=Msquare]
                prep [prompt="p", outputs="plan"]
                fork [shape=component]
                a [prompt="a", inputs="plan", outputs="review_a"]
                a2 [prompt="a2", inputs="review_a"]
                b [prompt="b", inputs="review_a", outputs="review_b"]
                join [shape=tripleoctagon]
                after [prompt="after", inputs="plan, review_a, review_b"]
                start -> prep -> fork
                fork -> a
                fork -> b
                a -> a2 -> join
                b -> join
                join -> after -> exit
            }
            """);

        // Branch records are merged back at the join, so only the sibling branch cannot see review_a.
        var order = Assert.Single(diagnostics, d => d.Rule.StartsWith("artifact_", StringComparison.Ordinal));
        Assert.Equal("artifact_order", order.Rule);
        Assert.Equal("b", order.NodeId);
        Assert.Equal("Input 'review_a' is produced only by 'a', which cannot run before this stage, so it is never available.", order.Message);
    }

    [Fact]
    public void Pins_on_other_node_types_are_ignored_with_a_warning()
    {
        var diagnostics = Lint("""
            digraph H {
                start [shape=Mdiamond, outputs="kickoff"] exit [shape=Msquare]
                architect [prompt="Design", outputs="design:docs/DESIGN.md"]
                gate [shape=hexagon, label="OK?", inputs="design", outputs="approval"]
                build [prompt="Build", inputs="approval"]
                start -> architect -> gate
                gate -> build [label="[Y] Yes"]
                build -> exit
            }
            """);

        var handlers = diagnostics.Where(d => d.Rule == "artifact_handlers").ToList();
        Assert.Equal(2, handlers.Count);
        Assert.All(handlers, d => Assert.Equal(Severity.Warning, d.Severity));
        Assert.Contains(handlers, d => d.NodeId == "start" && d.Message == "outputs is ignored on start nodes; only codergen, tool, and judged fan-in stages consume and produce artifacts.");
        Assert.Contains(handlers, d => d.NodeId == "gate" && d.Message == "inputs and outputs are ignored on wait.human nodes; only codergen, tool, and judged fan-in stages consume and produce artifacts.");

        // An output declared where it is ignored does not count as produced.
        Assert.Contains(diagnostics, d => d.Rule == "artifact_inputs" && d.NodeId == "build" && d.Message.Contains("'approval'", StringComparison.Ordinal));
    }

    [Fact]
    public void Unregistered_types_run_as_codergen_but_registered_custom_types_ignore_pins()
    {
        const string source = """
            digraph C {
                start [shape=Mdiamond] exit [shape=Msquare]
                m [type="mystery", prompt="x", outputs="notes"]
                t [type="mystery", shape=parallelogram, tool_command="echo", outputs="log"]
                start -> m -> t -> exit
            }
            """;

        Assert.DoesNotContain(Lint(source), d => d.Rule == "artifact_handlers");

        var registered = PipelineValidator.Validate(DotParser.Parse(source), knownHandlerTypes: HandlerTypes.BuiltIn.Append("mystery"));
        Assert.Equal(["m", "t"], registered.Where(d => d.Rule == "artifact_handlers").Select(d => d.NodeId));
    }
}

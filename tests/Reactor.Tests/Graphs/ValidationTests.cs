using Reactor.Graphs;
using Reactor.Validation;

namespace Reactor.Tests.Graphs;

public class ValidationTests
{
    private static IReadOnlyList<Diagnostic> Lint(string source) => PipelineValidator.Validate(DotParser.Parse(source));

    private const string Valid = """
        digraph G {
            graph [goal="g"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            work [prompt="do it"]
            start -> work -> exit
        }
        """;

    [Fact]
    public void Valid_pipeline_has_no_errors()
    {
        Assert.DoesNotContain(Lint(Valid), d => d.Severity == Severity.Error);
    }

    [Fact]
    public void Missing_start_node_is_an_error()
    {
        var diagnostics = Lint("digraph G { exit [shape=Msquare] work [prompt=\"x\"] work -> exit }");
        Assert.Contains(diagnostics, d => d.Rule == "start_node" && d.Severity == Severity.Error);
    }

    [Fact]
    public void Missing_or_duplicate_exit_node_is_an_error()
    {
        Assert.Contains(Lint("digraph G { start [shape=Mdiamond] work [prompt=\"x\"] start -> work }"), d => d.Rule == "terminal_node");
        Assert.Contains(
            Lint("digraph G { start [shape=Mdiamond] a [shape=Msquare] b [shape=Msquare] start -> a start -> b }"),
            d => d.Rule == "terminal_node" && d.Message.Contains("2 exit nodes", StringComparison.Ordinal));
    }

    [Fact]
    public void Id_based_start_and_exit_are_recognized()
    {
        var diagnostics = Lint("digraph G { start -> work -> end  work [prompt=\"x\"] }");
        Assert.DoesNotContain(diagnostics, d => d.Rule is "start_node" or "terminal_node");
    }

    [Fact]
    public void Orphan_node_is_reported()
    {
        var diagnostics = Lint(Valid.Replace("start -> work -> exit", "start -> work -> exit\n orphan [prompt=\"x\"]", StringComparison.Ordinal));
        var orphan = Assert.Single(diagnostics, d => d.Rule == "reachability");
        Assert.Equal("orphan", orphan.NodeId);
        Assert.Equal(Severity.Error, orphan.Severity);
    }

    [Fact]
    public void Start_incoming_and_exit_outgoing_are_errors()
    {
        var diagnostics = Lint("""
            digraph G {
                start [shape=Mdiamond] exit [shape=Msquare] w [prompt="x"]
                start -> w -> exit
                w -> start
                exit -> w
            }
            """);
        Assert.Contains(diagnostics, d => d.Rule == "start_no_incoming" && d.Edge == ("w", "start"));
        Assert.Contains(diagnostics, d => d.Rule == "exit_no_outgoing" && d.Edge == ("exit", "w"));
    }

    [Fact]
    public void Edge_targets_must_exist()
    {
        var graph = DotParser.Parse(Valid);
        graph.AddEdge("work", "ghost");
        Assert.Contains(PipelineValidator.Validate(graph), d => d.Rule == "edge_target_exists" && d.Edge == ("work", "ghost"));
    }

    [Fact]
    public void Condition_and_stylesheet_syntax_errors_are_errors()
    {
        var diagnostics = Lint("""
            digraph G {
                graph [model_stylesheet="* { bogus: 1; }"]
                start [shape=Mdiamond] exit [shape=Msquare]
                start -> exit [condition="outcome==success"]
            }
            """);
        Assert.Contains(diagnostics, d => d.Rule == "condition_syntax" && d.Severity == Severity.Error);
        Assert.Contains(diagnostics, d => d.Rule == "stylesheet_syntax" && d.Severity == Severity.Error);
    }

    [Fact]
    public void Warnings_for_types_fidelity_retry_targets_goal_gates_and_prompts()
    {
        var diagnostics = Lint("""
            digraph G {
                graph [default_fidelity="everything"]
                start [shape=Mdiamond] exit [shape=Msquare]
                a [type="mystery", prompt="x"]
                b [fidelity="lossy", prompt="x", retry_target="nowhere"]
                c [goal_gate=true, prompt="x"]
                d
                start -> a -> b -> c -> d
                d -> exit [fidelity="bad"]
            }
            """);
        Assert.Contains(diagnostics, d => d.Rule == "type_known" && d.NodeId == "a" && d.Severity == Severity.Warning);
        Assert.Equal(3, diagnostics.Count(d => d.Rule == "fidelity_valid"));
        Assert.Contains(diagnostics, d => d.Rule == "retry_target_exists" && d.NodeId == "b");
        Assert.Contains(diagnostics, d => d.Rule == "goal_gate_has_retry" && d.NodeId == "c");
        Assert.Contains(diagnostics, d => d.Rule == "prompt_on_llm_nodes" && d.NodeId == "d");
        Assert.DoesNotContain(diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void Extra_rules_for_values_attributes_dead_ends_and_tools()
    {
        var diagnostics = Lint("""
            digraph G {
                start [shape=Mdiamond] exit [shape=Msquare]
                a [prompt="x", max_retries="lots", timeout="soon"]
                t [shape=parallelogram]
                dead [prompt="x"]
                start -> a
                a -> exit [condition="outcome=sucess"]
                a -> t [weight="heavy"]
                t -> dead
            }
            """);
        Assert.Contains(diagnostics, d => d.Rule == "condition_values" && d.Message.Contains("sucess", StringComparison.Ordinal));
        Assert.Equal(3, diagnostics.Count(d => d.Rule == "attribute_types"));
        Assert.Contains(diagnostics, d => d.Rule == "dead_end" && d.NodeId == "dead");
        Assert.Contains(diagnostics, d => d.Rule == "tool_command" && d.NodeId == "t");
    }

    [Fact]
    public void Validate_or_raise_throws_with_all_errors()
    {
        var error = Assert.Throws<ValidationException>(() => PipelineValidator.ValidateOrRaise(DotParser.Parse("digraph G { a -> b }")));
        Assert.Contains(error.Errors, d => d.Rule == "start_node");
        Assert.Contains(error.Errors, d => d.Rule == "terminal_node");
        Assert.Contains("start_node", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_lint_rules_are_appended()
    {
        var diagnostics = PipelineValidator.Validate(DotParser.Parse(Valid), [new NoWorkRule()]);
        Assert.Equal("no_work", diagnostics[^1].Rule);
    }

    [Fact]
    public void Diagnostics_render_rule_severity_location_and_message()
    {
        var text = new Diagnostic("reachability", Severity.Error, "Node 'x' is unreachable.") { NodeId = "x", Fix = "connect it" }.ToString();
        Assert.Equal("ERROR [reachability] node 'x': Node 'x' is unreachable. (fix: connect it)", text);
    }

    [Fact]
    public void Tool_color_must_be_a_boolean()
    {
        var diagnostics = Lint("""
            digraph G {
                start [shape=Mdiamond] exit [shape=Msquare]
                good [shape=parallelogram, tool_command="npm test", tool_color=true]
                bad [shape=parallelogram, tool_command="npm test", tool_color="sometimes"]
                start -> good -> bad -> exit
            }
            """);

        var diagnostic = Assert.Single(diagnostics, d => d.Rule == "attribute_types");
        Assert.Equal("bad", diagnostic.NodeId);
        Assert.Contains("tool_color='sometimes'", diagnostic.Message, StringComparison.Ordinal);
    }

    private sealed class NoWorkRule : ILintRule
    {
        public string Name => "no_work";

        public IEnumerable<Diagnostic> Apply(Graph graph)
        {
            if (graph.Nodes.ContainsKey("work"))
            {
                yield return new Diagnostic(Name, Severity.Info, "Found a node named work.") { NodeId = "work" };
            }
        }
    }
}

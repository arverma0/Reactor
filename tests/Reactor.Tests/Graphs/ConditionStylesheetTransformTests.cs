using Reactor.Conditions;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Stylesheets;
using Reactor.Transforms;

namespace Reactor.Tests.Graphs;

public class ConditionTests
{
    private static PipelineContext Context(params (string Key, object? Value)[] values)
    {
        var context = new PipelineContext();
        foreach (var (key, value) in values)
        {
            context.Set(key, value);
        }

        return context;
    }

    [Theory]
    [InlineData("outcome=success", true)]
    [InlineData("outcome!=success", false)]
    [InlineData("outcome=fail", false)]
    [InlineData("outcome=success && context.tests_passed=true", true)]
    [InlineData("outcome=success && context.tests_passed=false", false)]
    [InlineData("context.loop_state!=exhausted", true)]
    [InlineData("preferred_label=Fix", true)]
    [InlineData("context.missing=", true)]
    [InlineData("context.missing!=x", true)]
    [InlineData("context.missing=x", false)]
    [InlineData("tests_passed=true", true)]
    [InlineData("context.name=\"Ada Lovelace\"", true)]
    [InlineData("context.count=3", true)]
    [InlineData("context.tests_passed", true)]
    [InlineData("context.disabled", false)]
    [InlineData("context.missing", false)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    public void Evaluates_clauses(string condition, bool expected)
    {
        var outcome = new Outcome { Status = StageStatus.Success, PreferredLabel = "Fix" };
        var context = Context(("tests_passed", true), ("loop_state", "running"), ("name", "Ada Lovelace"), ("count", 3), ("disabled", "false"));

        Assert.Equal(expected, ConditionEvaluator.Evaluate(condition, outcome, context));
    }

    [Fact]
    public void Context_prefix_falls_back_to_unprefixed_keys_and_exact_keys_win()
    {
        var context = Context(("context.x", "prefixed"), ("y", "plain"));
        Assert.Equal("prefixed", ConditionEvaluator.ResolveKey("context.x", Outcome.Success(), context));
        Assert.Equal("plain", ConditionEvaluator.ResolveKey("context.y", Outcome.Success(), context));
        Assert.Equal("", ConditionEvaluator.ResolveKey("context.z", Outcome.Success(), context));
    }

    [Fact]
    public void String_comparison_is_case_sensitive()
    {
        var context = Context(("mode", "Fast"));
        Assert.False(ConditionEvaluator.Evaluate("context.mode=fast", Outcome.Success(), context));
        Assert.True(ConditionEvaluator.Evaluate("context.mode=Fast", Outcome.Success(), context));
    }

    [Fact]
    public void Outcome_values_cover_all_statuses()
    {
        var context = new PipelineContext();
        Assert.True(ConditionEvaluator.Evaluate("outcome=partial_success", new Outcome { Status = StageStatus.PartialSuccess }, context));
        Assert.True(ConditionEvaluator.Evaluate("outcome=retry", new Outcome { Status = StageStatus.Retry }, context));
        Assert.True(ConditionEvaluator.Evaluate("outcome=fail", Outcome.Fail("x"), context));
    }

    [Theory]
    [InlineData("outcome==success", "not '=='")]
    [InlineData("outcome=success || outcome=fail", "OR is not supported")]
    [InlineData("context.n>3", "comparison operator")]
    [InlineData("!context.flag", "negation")]
    [InlineData("=success", "missing key")]
    [InlineData("outcome=success &&", "dangling")]
    [InlineData("context.x=\"open", "unterminated")]
    [InlineData("context.x=two words", "invalid literal")]
    [InlineData("bad key=1", "invalid key")]
    public void Rejects_malformed_conditions(string condition, string expected)
    {
        var error = Assert.Throws<ConditionSyntaxException>(() => ConditionParser.Parse(condition));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void And_inside_quotes_does_not_split()
    {
        var expression = ConditionParser.Parse("context.q=\"a && b\" && outcome=success");
        Assert.Equal(2, expression.Clauses.Count);
        Assert.Equal("a && b", expression.Clauses[0].Value);
    }
}

public class StylesheetTests
{
    private static Graph Parse(string stylesheet, string nodes) =>
        DotParser.Parse($"digraph G {{ graph [model_stylesheet=\"{stylesheet}\"] {nodes} }}");

    [Fact]
    public void Spec_example_resolves_by_specificity()
    {
        var graph = DotParser.Parse("""
            digraph Pipeline {
                graph [
                    goal="Implement feature X",
                    model_stylesheet="
                        * { llm_model: claude-sonnet-4-5; llm_provider: anthropic; }
                        .code { llm_model: claude-opus-4-6; llm_provider: anthropic; }
                        #critical_review { llm_model: gpt-5.2; llm_provider: openai; reasoning_effort: high; }
                    "
                ]
                start [shape=Mdiamond]
                exit  [shape=Msquare]
                plan            [label="Plan", class="planning"]
                implement       [label="Implement", class="code"]
                critical_review [label="Critical Review", class="code"]
                start -> plan -> implement -> critical_review -> exit
            }
            """);

        var result = new StylesheetTransform().Apply(graph);

        Assert.Equal("claude-sonnet-4-5", result.Nodes["plan"].LlmModel);
        Assert.Equal("claude-opus-4-6", result.Nodes["implement"].LlmModel);
        Assert.Equal("gpt-5.2", result.Nodes["critical_review"].LlmModel);
        Assert.Equal("openai", result.Nodes["critical_review"].LlmProvider);
        Assert.Equal("high", result.Nodes["critical_review"].ReasoningEffort);
        Assert.Null(graph.Nodes["plan"].LlmModel);
    }

    [Fact]
    public void Shape_selector_and_later_rules_win_at_equal_specificity()
    {
        var graph = Parse("box { llm_model: a; } box { llm_model: b; } hexagon { llm_model: c; }", "x [shape=box] y [shape=hexagon] z");
        var result = new StylesheetTransform().Apply(graph);
        Assert.Equal("b", result.Nodes["x"].LlmModel);
        Assert.Equal("c", result.Nodes["y"].LlmModel);
        Assert.Equal("b", result.Nodes["z"].LlmModel);
    }

    [Fact]
    public void Explicit_node_attributes_override_stylesheet_but_defaults_do_not()
    {
        var graph = Parse("* { llm_model: sheet; reasoning_effort: low; }", "node [llm_model=default_block] a [llm_model=explicit] b");
        var result = new StylesheetTransform().Apply(graph);
        Assert.Equal("explicit", result.Nodes["a"].LlmModel);
        Assert.Equal("sheet", result.Nodes["b"].LlmModel);
        Assert.Equal("low", result.Nodes["a"].ReasoningEffort);
    }

    [Fact]
    public void Class_selectors_match_subgraph_derived_classes()
    {
        var graph = DotParser.Parse("""
            digraph G {
                graph [model_stylesheet=".fast { llm_model: \"gemini-3-flash-preview\"; }"]
                subgraph s { label="Fast"  quick [prompt="x"] }
                slow [prompt="y"]
            }
            """);
        var result = new StylesheetTransform().Apply(graph);
        Assert.Equal("gemini-3-flash-preview", result.Nodes["quick"].LlmModel);
        Assert.Null(result.Nodes["slow"].LlmModel);
    }

    [Theory]
    [InlineData("* { llm_model: a }", 1)]
    [InlineData("* { llm_model: a; } /* c */ #n { reasoning_effort: 'high' }", 2)]
    [InlineData("", 0)]
    public void Parses_valid_stylesheets(string text, int rules)
    {
        Assert.Equal(rules, ModelStylesheet.Parse(text).Rules.Count);
    }

    [Theory]
    [InlineData("* { color: red; }", "Unknown property")]
    [InlineData("* llm_model: a; }", "Expected '{'")]
    [InlineData("* { llm_model a; }", "Expected ':'")]
    [InlineData("* { llm_model: a;", "Unterminated rule")]
    [InlineData("* { }", "no declarations")]
    [InlineData("@media { llm_model: a; }", "Expected a selector")]
    public void Rejects_invalid_stylesheets(string text, string expected)
    {
        var error = Assert.Throws<StylesheetParseException>(() => ModelStylesheet.Parse(text));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}

public class TransformTests
{
    [Fact]
    public void Variable_expansion_replaces_goal_in_prompts_without_mutating_input()
    {
        var graph = DotParser.Parse("""digraph G { graph [goal="ship it"] a [prompt="Plan for: $goal. Then $goal again."] b [label="$goal"] }""");

        var result = new VariableExpansionTransform().Apply(graph);

        Assert.Equal("Plan for: ship it. Then ship it again.", result.Nodes["a"].Prompt);
        Assert.Equal("$goal", result.Nodes["b"].Label);
        Assert.Contains("$goal", graph.Nodes["a"].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_transforms_run_after_built_ins_in_registration_order()
    {
        var log = new List<string>();
        var graph = DotParser.Parse("""digraph G { graph [goal="g"] start [shape=Mdiamond] a [prompt="$goal"] exit [shape=Msquare] start -> a -> exit }""");
        var runner = new PipelineRunner();
        runner.RegisterTransform(new RecordingTransform("first", log));
        runner.RegisterTransform(new RecordingTransform("second", log));

        var prepared = runner.Prepare(graph);

        Assert.Equal(["first:g", "second:g"], log);
        Assert.Equal("g", prepared.Graph.Nodes["a"].Prompt);
        Assert.True(prepared.Graph.Nodes.ContainsKey("audit_first"));
    }

    private sealed class RecordingTransform(string name, List<string> log) : IGraphTransform
    {
        public string Name => name;

        public Graph Apply(Graph graph)
        {
            var clone = graph.Clone();
            log.Add($"{name}:{clone.Nodes["a"].Prompt}");
            var audit = clone.AddNode(new Node("audit_" + name));
            audit[AttrKeys.Prompt] = "audit";
            clone.AddEdge("start", audit.Id, new Dictionary<string, string> { ["condition"] = "outcome=fail" });
            clone.AddEdge(audit.Id, "exit");
            return clone;
        }
    }
}

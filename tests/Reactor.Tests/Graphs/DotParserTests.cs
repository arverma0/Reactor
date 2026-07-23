using Reactor.Graphs;

namespace Reactor.Tests.Graphs;

public class DotParserTests
{
    [Fact]
    public void Parses_simple_linear_pipeline_with_graph_attributes()
    {
        var graph = DotParser.Parse("""
            digraph Simple {
                graph [goal="Run tests and report", label="Simple"]
                rankdir=LR

                start [shape=Mdiamond, label="Start"]
                exit  [shape=Msquare, label="Exit"]

                run_tests [label="Run Tests", prompt="Run the test suite and report results"]
                report    [label="Report", prompt="Summarize the test results"]

                start -> run_tests -> report -> exit
            }
            """);

        Assert.Equal("Simple", graph.Name);
        Assert.Equal("Run tests and report", graph.Goal);
        Assert.Equal("Simple", graph.Label);
        Assert.Equal("LR", graph["rankdir"]);
        Assert.Equal(["start", "exit", "run_tests", "report"], graph.Nodes.Keys);
        Assert.Equal(3, graph.Edges.Count);
        Assert.Equal(("start", "run_tests"), (graph.Edges[0].From, graph.Edges[0].To));
        Assert.Equal(("report", "exit"), (graph.Edges[2].From, graph.Edges[2].To));
        Assert.Equal("Run the test suite and report results", graph.Nodes["run_tests"].Prompt);
    }

    [Fact]
    public void Parses_multiline_attribute_blocks_and_typed_values()
    {
        var graph = DotParser.Parse("""
            digraph G {
                review_gate [
                    shape=hexagon,
                    label="Review Changes",
                    type="wait.human",
                    max_retries=3,
                    goal_gate=true,
                    timeout=900s,
                    weight_hint=-1,
                    ratio=0.5,
                    fidelity=summary:high,
                    human.default_choice=ship_it
                ]
            }
            """);

        var node = graph.Nodes["review_gate"];
        Assert.Equal("hexagon", node.Shape);
        Assert.Equal("wait.human", node.Type);
        Assert.Equal(3, node.MaxRetries);
        Assert.True(node.GoalGate);
        Assert.Equal(TimeSpan.FromSeconds(900), node.Timeout);
        Assert.Equal("-1", node["weight_hint"]);
        Assert.Equal("0.5", node["ratio"]);
        Assert.Equal("summary:high", node.Fidelity);
        Assert.Equal("ship_it", node[AttrKeys.HumanDefaultChoice]);
    }

    [Fact]
    public void Chained_edges_expand_with_shared_attributes()
    {
        var graph = DotParser.Parse("""digraph G { A -> B -> C [label="next", weight=2] }""");

        Assert.Equal(2, graph.Edges.Count);
        Assert.All(graph.Edges, e => Assert.Equal("next", e.Label));
        Assert.All(graph.Edges, e => Assert.Equal(2, e.Weight));
        Assert.Equal(["A", "B", "C"], graph.Nodes.Keys);
    }

    [Fact]
    public void Node_and_edge_defaults_apply_to_subsequent_declarations_only()
    {
        var graph = DotParser.Parse("""
            digraph G {
                early [label="Early"]
                node [shape=box, timeout="900s"]
                edge [weight=5]
                late [label="Late"]
                override [timeout="30s"]
                early -> late
                late -> override [weight=1]
            }
            """);

        Assert.Null(graph.Nodes["early"].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(900), graph.Nodes["late"].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), graph.Nodes["override"].Timeout);
        Assert.Equal(5, graph.Edges[0].Weight);
        Assert.Equal(1, graph.Edges[1].Weight);
        Assert.Contains("timeout", graph.Nodes["override"].ExplicitKeys);
        Assert.DoesNotContain("shape", graph.Nodes["override"].ExplicitKeys);
    }

    [Fact]
    public void Subgraphs_scope_defaults_and_derive_classes_from_labels()
    {
        var graph = DotParser.Parse("""
            digraph G {
                start [shape=Mdiamond]
                subgraph cluster_loop {
                    label = "Loop A"
                    node [thread_id="loop-a", timeout="900s"]

                    Plan      [label="Plan next step"]
                    Implement [label="Implement", timeout="1800s"]
                }
                Outside [label="Outside"]
                start -> Plan -> Implement -> Outside
            }
            """);

        Assert.Equal("loop-a", graph.Nodes["Plan"].ThreadId);
        Assert.Equal(TimeSpan.FromSeconds(900), graph.Nodes["Plan"].Timeout);
        Assert.Equal("loop-a", graph.Nodes["Implement"].ThreadId);
        Assert.Equal(TimeSpan.FromSeconds(1800), graph.Nodes["Implement"].Timeout);
        Assert.Contains("loop-a", graph.Nodes["Plan"].Classes);
        Assert.Equal("loop-a", graph.Nodes["Plan"].SubgraphScope);
        Assert.Equal("", graph.Nodes["Outside"].ThreadId);
        Assert.Empty(graph.Nodes["Outside"].Classes);
        Assert.Null(graph["label"]);
    }

    [Fact]
    public void Nested_subgraphs_accumulate_classes_and_label_order_does_not_matter()
    {
        var graph = DotParser.Parse("""
            digraph G {
                subgraph outer {
                    inner_node_first [label="x"]
                    subgraph inner {
                        deep [label="Deep", class="critical"]
                        graph [label="Inner Loop!"]
                    }
                    label="Outer Stage"
                }
            }
            """);

        Assert.Equal(["critical", "outer-stage", "inner-loop"], graph.Nodes["deep"].Classes);
        Assert.Equal(["outer-stage"], graph.Nodes["inner_node_first"].Classes);
        Assert.Equal("inner-loop", graph.Nodes["deep"].SubgraphScope);
    }

    [Fact]
    public void Subgraph_structure_and_declared_defaults_are_recorded_for_editors()
    {
        var graph = DotParser.Parse("""
            digraph G {
                node [timeout="5m"]
                edge [weight=2]
                start [shape=Mdiamond]
                subgraph cluster_outer {
                    label="Deep thinking"
                    node [reasoning_effort=high]
                    plan [prompt="Plan"]
                    subgraph inner {
                        edge [fidelity=full]
                        build [prompt="Build"]
                        build -> check
                    }
                    review [prompt="Review"]
                }
                { loose [prompt="Anonymous scope"] }
                start -> plan -> build
            }
            """);

        Assert.Equal(new Dictionary<string, string> { ["timeout"] = "5m" }, graph.NodeDefaults);
        Assert.Equal(new Dictionary<string, string> { ["weight"] = "2" }, graph.EdgeDefaults);
        Assert.Equal(3, graph.Subgraphs.Count);

        var outer = graph.Subgraphs[0];
        Assert.Equal("cluster_outer", outer.Name);
        Assert.Null(outer.ParentName);
        Assert.Equal(-1, outer.ParentIndex);
        Assert.Equal("Deep thinking", outer.Label);
        Assert.Equal("deep-thinking", outer.DerivedClass);
        Assert.Equal(new Dictionary<string, string> { ["reasoning_effort"] = "high" }, outer.NodeDefaults);
        Assert.Equal(["plan", "review"], outer.NodeIds);

        var inner = graph.Subgraphs[1];
        Assert.Equal("inner", inner.Name);
        Assert.Equal("cluster_outer", inner.ParentName);
        Assert.Equal(0, inner.ParentIndex);
        Assert.Null(inner.Label);
        Assert.Empty(inner.NodeDefaults);
        Assert.Equal(new Dictionary<string, string> { ["fidelity"] = "full" }, inner.EdgeDefaults);
        Assert.Equal(["build", "check"], inner.NodeIds);

        var anonymous = graph.Subgraphs[2];
        Assert.Null(anonymous.Name);
        Assert.Equal(["loose"], anonymous.NodeIds);

        // Inherited defaults still apply to members, and the clone keeps the structure.
        Assert.Equal("high", graph.Nodes["build"].ReasoningEffort);
        Assert.Equal("full", graph.Edges.Single(e => e.From == "build").Fidelity);
        var clone = graph.Clone();
        Assert.Equal(["plan", "review"], clone.Subgraphs[0].NodeIds);
        Assert.Equal("5m", clone.NodeDefaults["timeout"]);
    }

    [Fact]
    public void Class_attribute_is_comma_separated()
    {
        var graph = DotParser.Parse("""digraph G { review_code [shape=box, class="code,critical", prompt="Review the code"] }""");
        Assert.Equal(["code", "critical"], graph.Nodes["review_code"].Classes);
    }

    [Fact]
    public void Comments_semicolons_escapes_and_string_concatenation()
    {
        var graph = DotParser.Parse("""
            // leading comment
            digraph G {
                /* block
                   comment */
                a [prompt="line1\nline2\t\"quoted\" \\ end"];  // trailing
                b [prompt="part one " + "part two"];
                a -> b;
            }
            """);

        Assert.Equal("line1\nline2\t\"quoted\" \\ end", graph.Nodes["a"].Prompt);
        Assert.Equal("part one part two", graph.Nodes["b"].Prompt);
        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Quoted_and_unquoted_values_are_equivalent()
    {
        var graph = DotParser.Parse("""digraph G { a [max_retries=2, goal_gate="true"] b [max_retries="2", goal_gate=true] }""");
        Assert.Equal(graph.Nodes["a"].MaxRetries, graph.Nodes["b"].MaxRetries);
        Assert.Equal(graph.Nodes["a"].GoalGate, graph.Nodes["b"].GoalGate);
    }

    [Fact]
    public void Repeated_node_statements_merge_attributes()
    {
        var graph = DotParser.Parse("""digraph G { a [label="A"] a [prompt="P"] a -> b }""");
        Assert.Equal("A", graph.Nodes["a"].Label);
        Assert.Equal("P", graph.Nodes["a"].Prompt);
        Assert.Equal("b", graph.Nodes["b"].Label);
    }

    [Theory]
    [InlineData("graph G { a -- b }", "Undirected graphs are not supported")]
    [InlineData("strict digraph G { a -> b }", "'strict' graphs are not supported")]
    [InlineData("digraph G { a -- b }", "Undirected edges")]
    [InlineData("digraph G { a } digraph H { b }", "Only one graph per file")]
    [InlineData("digraph G { a [label=<b>bold</b>] }", "HTML")]
    [InlineData("digraph G { a [shape=box label=\"x\"] }", "Expected ',' between attributes")]
    [InlineData("digraph G { \"my node\" [label=\"x\"] }", "Invalid node id")]
    [InlineData("digraph G { a:port -> b }", "ports are not supported")]
    [InlineData("digraph G { a -> {b c} }", "Subgraphs as edge endpoints")]
    [InlineData("digraph G { a [label=\"unterminated] }", "Unterminated string")]
    [InlineData("digraph G { a [shape] }", "Expected '='")]
    [InlineData("digraph G { /* never closed ", "Unterminated block comment")]
    [InlineData("digraph G { a [weight=1.5x] }", "Invalid unquoted value")]
    [InlineData("digraph G { node -> b }", "Expected '['")]
    public void Rejects_unsupported_or_malformed_dot(string source, string expectedMessage)
    {
        var error = Assert.Throws<DotParseException>(() => DotParser.Parse(source));
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        Assert.True(error.Line >= 1);
    }

    [Fact]
    public void Reports_line_and_column_of_errors()
    {
        var error = Assert.Throws<DotParseException>(() => DotParser.Parse("digraph G {\n  a [x=1]\n  b [y=2 z=3]\n}"));
        Assert.Equal(3, error.Line);
        Assert.Equal(10, error.Column);
    }

    [Fact]
    public void Writer_round_trips_through_the_parser()
    {
        var original = DotParser.Parse("""
            digraph RoundTrip {
                graph [goal="Ship \"it\"", model_stylesheet="* { llm_model: x; }"]
                start [shape=Mdiamond]
                work [prompt="multi\nline", class="code", timeout=15m]
                exit [shape=Msquare]
                start -> work [weight=3]
                work -> exit [condition="outcome=success", label="Done"]
            }
            """);

        var reparsed = DotParser.Parse(DotWriter.Write(original));

        Assert.Equal(original.Goal, reparsed.Goal);
        Assert.Equal(original.ModelStylesheet, reparsed.ModelStylesheet);
        Assert.Equal(original.Nodes.Keys, reparsed.Nodes.Keys);
        Assert.Equal("multi\nline", reparsed.Nodes["work"].Prompt);
        Assert.Equal(TimeSpan.FromMinutes(15), reparsed.Nodes["work"].Timeout);
        Assert.Equal(original.Edges.Select(e => (e.From, e.To, e.Condition, e.Weight)), reparsed.Edges.Select(e => (e.From, e.To, e.Condition, e.Weight)));
    }

    [Fact]
    public void Derived_class_names_follow_the_spec()
    {
        Assert.Equal("loop-a", DotParser.DeriveClassName("Loop A"));
        Assert.Equal("phase-2-build", DotParser.DeriveClassName("Phase 2: Build!"));
    }

    [Theory]
    [InlineData("900s", 900_000)]
    [InlineData("15m", 900_000)]
    [InlineData("2h", 7_200_000)]
    [InlineData("250ms", 250)]
    [InlineData("1d", 86_400_000)]
    [InlineData("30", 30_000)]
    public void Durations_parse(string text, double expectedMs)
    {
        Assert.Equal(expectedMs, AttributeValues.ParseDuration(text)!.Value.TotalMilliseconds);
    }

    [Fact]
    public void Handler_types_resolve_from_type_then_shape_then_id()
    {
        var graph = DotParser.Parse("""
            digraph G {
                start
                exit
                End2 [label="x"]
                gate [shape=hexagon]
                custom [shape=hexagon, type="my.type"]
                plain [label="LLM"]
                weird [shape=ellipse]
            }
            """);

        Assert.Equal(HandlerTypes.Start, HandlerTypes.Resolve(graph.Nodes["start"]));
        Assert.Equal(HandlerTypes.Exit, HandlerTypes.Resolve(graph.Nodes["exit"]));
        Assert.Equal(HandlerTypes.Codergen, HandlerTypes.Resolve(graph.Nodes["End2"]));
        Assert.Equal(HandlerTypes.WaitHuman, HandlerTypes.Resolve(graph.Nodes["gate"]));
        Assert.Equal("my.type", HandlerTypes.Resolve(graph.Nodes["custom"]));
        Assert.Equal(HandlerTypes.Codergen, HandlerTypes.Resolve(graph.Nodes["plain"]));
        Assert.Equal(HandlerTypes.Codergen, HandlerTypes.Resolve(graph.Nodes["weird"]));
    }
}

using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Stylesheets;
using Reactor.Tests.Support;
using Reactor.Transforms;

namespace Reactor.Tests.Graphs;

public class StageInstructionsTests
{
    private static Graph Parse(string dot) => GraphTransforms.ApplyAll(DotParser.Parse(dot));

    [Fact]
    public void House_rules_then_pipeline_then_stage_instructions()
    {
        var graph = Parse("""
            digraph G {
              graph [instructions="Use PowerShell syntax."]
              plan [prompt="Plan", instructions="Cite the brief."]
            }
            """);

        var text = StageInstructions.Resolve(graph.GetNode("plan")!, graph);

        Assert.StartsWith("## House rules\n- Write large files incrementally", text, StringComparison.Ordinal);
        var rules = text.IndexOf("## House rules", StringComparison.Ordinal);
        var pipeline = text.IndexOf("## Pipeline instructions\nUse PowerShell syntax.", StringComparison.Ordinal);
        var stage = text.IndexOf("## Stage instructions\nCite the brief.", StringComparison.Ordinal);
        Assert.True(rules < pipeline && pipeline < stage, text);
        Assert.Equal(StageInstructions.DefaultHouseRules.Count, text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("graph [house_rules=false]", "", false)]
    [InlineData("graph [house_rules=false]", ", house_rules=true", true)]
    [InlineData("", ", house_rules=false", false)]
    [InlineData("", "", true)]
    public void House_rules_can_be_turned_off_per_graph_and_back_on_per_stage(string graphAttrs, string nodeAttrs, bool expected)
    {
        var graph = Parse($$"""digraph G { {{graphAttrs}} plan [prompt="Plan"{{nodeAttrs}}] }""");

        var text = StageInstructions.Resolve(graph.GetNode("plan")!, graph);

        Assert.Equal(expected, text.Contains("## House rules", StringComparison.Ordinal));
        Assert.Equal(expected, StageInstructions.HouseRulesEnabled(graph.GetNode("plan")!, graph));
    }

    [Fact]
    public void Agents_without_tools_get_no_house_rules_and_only_the_agent_backend_reads_the_legacy_attribute()
    {
        var graph = Parse("""digraph G { plan [prompt="Plan", agent.instructions="Legacy rule."] }""");
        var node = graph.GetNode("plan")!;

        Assert.Equal("", StageInstructions.Resolve(node, graph, toolUse: false));
        Assert.DoesNotContain("Legacy rule.", StageInstructions.Resolve(node, graph), StringComparison.Ordinal);
        Assert.EndsWith("## Stage instructions\nLegacy rule.", StageInstructions.Resolve(node, graph, includeLegacyAgentInstructions: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_md_is_written_only_when_there_are_instructions()
    {
        using var dir = new TempDir();
        var graph = Parse("""digraph G { graph [house_rules=false] a [prompt="A"] b [prompt="B", instructions="Be brief."] }""");

        Assert.Equal("", StageInstructions.ResolveAndRecord(graph.GetNode("a")!, graph, dir.Combine("a")));
        Assert.False(File.Exists(dir.Combine("a", StageInstructions.FileName)));

        Assert.Equal("## Stage instructions\nBe brief.", StageInstructions.ResolveAndRecord(graph.GetNode("b")!, graph, dir.Combine("b")));
        Assert.Equal("## Stage instructions\nBe brief.\n", File.ReadAllText(dir.Combine("b", StageInstructions.FileName)));
    }

    [Fact]
    public void The_model_stylesheet_sets_instructions_and_explicit_attributes_win()
    {
        var graph = Parse("""
            digraph G {
              graph [house_rules=false, model_stylesheet=".docs { instructions: \"Write in parts; then append.\"; } #pinned { instructions: \"from stylesheet\" }"]
              spec [prompt="Spec", class="docs"]
              pinned [prompt="Pinned", class="docs", instructions="from node"]
              other [prompt="Other"]
            }
            """);

        Assert.Equal("## Stage instructions\nWrite in parts; then append.", StageInstructions.Resolve(graph.GetNode("spec")!, graph));
        Assert.Equal("## Stage instructions\nfrom node", StageInstructions.Resolve(graph.GetNode("pinned")!, graph));
        Assert.Equal("", StageInstructions.Resolve(graph.GetNode("other")!, graph));
        Assert.Contains(AttrKeys.Instructions, ModelStylesheet.Properties);
    }

    [Fact]
    public void Prompt_section_is_delimited_and_empty_without_instructions()
    {
        Assert.Equal("", StageInstructions.PromptSection("  "));
        var section = StageInstructions.PromptSection("## Stage instructions\nBe brief.");
        Assert.StartsWith("# Instructions\n", section, StringComparison.Ordinal);
        Assert.EndsWith("Be brief.\n\n---\n\n", section, StringComparison.Ordinal);
    }
}

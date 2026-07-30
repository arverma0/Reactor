using System.Text;
using Reactor.Graphs;

namespace Reactor.Execution;

/// <summary>
/// Reactor extension: standing instructions ("house rules") for LLM stages, independent of the backend. The effective
/// instructions of a stage are, in order:
/// <list type="number">
/// <item>the built-in <see cref="DefaultHouseRules"/>, for stages whose agent uses tools, unless <c>house_rules=false</c>
/// (graph attribute, or node attribute for one stage);</item>
/// <item>the graph's <c>instructions</c> attribute (applies to every LLM stage);</item>
/// <item>the node's <c>instructions</c> attribute (also settable from the <c>model_stylesheet</c>);</item>
/// <item>for the <c>agent</c> backend, the legacy node attribute <c>agent.instructions</c>.</item>
/// </list>
/// Backends deliver them as system-level instructions where the agent supports it (the agent loop's user
/// instructions, the Copilot SDK session's system message, Claude Code's <c>--append-system-prompt</c>, the direct
/// backend's system message), else as a delimited section of the prompt; the effective text is written to
/// <c>{stage}/instructions.md</c>.
/// </summary>
public static class StageInstructions
{
    /// <summary>Graph and node attribute (and stylesheet property) holding standing instructions.</summary>
    public const string Attribute = AttrKeys.Instructions;

    /// <summary>Boolean graph or node attribute; <c>false</c> leaves out <see cref="DefaultHouseRules"/>.</summary>
    public const string HouseRulesAttribute = AttrKeys.HouseRules;

    /// <summary>The legacy node attribute honored by the <c>agent</c> backend only.</summary>
    public const string LegacyAgentAttribute = "agent.instructions";

    /// <summary>File in the stage directory that records the effective instructions.</summary>
    public const string FileName = "instructions.md";

    /// <summary>
    /// The built-in house rules: lessons that apply to coding agents running unattended as pipeline stages. They are
    /// included for agents with tools unless the graph (or node) sets <c>house_rules=false</c>.
    /// </summary>
    public static IReadOnlyList<string> DefaultHouseRules { get; } =
    [
        "Write large files incrementally: create the file with its first section, then add the rest with further edits " +
        "or appends. The arguments of one tool call are limited by your output-token limit, and a call that exceeds it is " +
        "cut off and its whole generation is lost.",
        "Keep scratch, temporary, and debugging files out of the product tree (use the system temp directory), and " +
        "delete them when you are done.",
        "Nobody can answer questions while this stage runs: make reasonable assumptions, follow the conventions you find, " +
        "and state your assumptions in your final message.",
        "Stop every process you start (dev servers, watchers, browsers) before you finish, and give long-running " +
        "commands a timeout.",
        "End with a short summary: what you changed, how you verified it, and anything left undone. It is handed to " +
        "later stages (and to a retry if this attempt is cut off).",
    ];

    /// <summary>The default house rules as a markdown list.</summary>
    public static string DefaultHouseRulesText { get; } = string.Join("\n", DefaultHouseRules.Select(r => "- " + r));

    /// <summary>
    /// The effective instructions for <paramref name="node"/>, or an empty string when there are none.
    /// </summary>
    /// <param name="node">The stage.</param>
    /// <param name="graph">The pipeline (for the graph-level attributes); null outside a run.</param>
    /// <param name="toolUse">True when the stage's agent has tools (the default house rules are about tool use).</param>
    /// <param name="includeLegacyAgentInstructions">True for the <c>agent</c> backend, which also honors <c>agent.instructions</c>.</param>
    public static string Resolve(Node node, Graph? graph, bool toolUse = true, bool includeLegacyAgentInstructions = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sections = new List<(string Title, string Text)>();
        if (toolUse && HouseRulesEnabled(node, graph))
        {
            sections.Add(("House rules", DefaultHouseRulesText));
        }

        if (graph?[Attribute] is { } pipeline && !string.IsNullOrWhiteSpace(pipeline))
        {
            sections.Add(("Pipeline instructions", pipeline));
        }

        var stage = node[Attribute];
        if (includeLegacyAgentInstructions && node[LegacyAgentAttribute] is { } legacy && !string.IsNullOrWhiteSpace(legacy))
        {
            stage = string.IsNullOrWhiteSpace(stage) ? legacy : stage.TrimEnd() + "\n\n" + legacy.Trim();
        }

        if (!string.IsNullOrWhiteSpace(stage))
        {
            sections.Add(("Stage instructions", stage));
        }

        if (sections.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var (title, text) in sections)
        {
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append("## ").Append(title).Append('\n').Append(text.Trim().ReplaceLineEndings("\n"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The effective instructions for the stage, written to <see cref="FileName"/> in <paramref name="stageDirectory"/>
    /// when non-empty (best effort). Returns the text (empty when there are none).
    /// </summary>
    public static string ResolveAndRecord(Node node, Graph? graph, string? stageDirectory, bool toolUse = true, bool includeLegacyAgentInstructions = false)
    {
        var text = Resolve(node, graph, toolUse, includeLegacyAgentInstructions);
        if (text.Length > 0 && stageDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(stageDirectory);
                File.WriteAllText(Path.Combine(stageDirectory, FileName), text + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transparency only; the stage still gets its instructions.
            }
        }

        return text;
    }

    /// <summary>
    /// The instructions as a delimited section to put before a prompt, for agents that cannot take system-level
    /// instructions (empty when there are none).
    /// </summary>
    public static string PromptSection(string instructions) =>
        string.IsNullOrWhiteSpace(instructions)
            ? ""
            : "# Instructions\n\nStanding instructions for this pipeline stage; follow them throughout the task below.\n\n" +
              instructions.Trim() + "\n\n---\n\n";

    /// <summary>True unless <c>house_rules</c> is false on the node or (when the node does not set it) on the graph.</summary>
    public static bool HouseRulesEnabled(Node node, Graph? graph)
    {
        ArgumentNullException.ThrowIfNull(node);
        return AttributeValues.ParseBool(node[HouseRulesAttribute]) ?? AttributeValues.ParseBool(graph?[HouseRulesAttribute]) ?? true;
    }
}

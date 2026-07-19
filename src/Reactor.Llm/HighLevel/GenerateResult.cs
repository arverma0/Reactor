using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>Detailed result of one step (one LLM call plus any tool executions) of a generation.</summary>
public sealed record StepResult
{
    public string Text { get; init; } = "";

    public string? Reasoning { get; init; }

    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<ToolResult> ToolResults { get; init; } = [];

    public FinishReason FinishReason { get; init; } = FinishReason.Stop;

    public Usage Usage { get; init; } = Usage.Empty;

    public required Response Response { get; init; }

    public IReadOnlyList<Warning> Warnings { get; init; } = [];

    internal static StepResult From(Response response, IReadOnlyList<ToolResult> toolResults) => new()
    {
        Text = response.Text,
        Reasoning = response.Reasoning,
        ToolCalls = response.ToolCalls,
        ToolResults = toolResults,
        FinishReason = response.FinishReason,
        Usage = response.Usage,
        Response = response,
        Warnings = response.Warnings,
    };
}

/// <summary>The result of <see cref="Ai.GenerateAsync(GenerateOptions, CancellationToken)"/>.</summary>
public sealed record GenerateResult
{
    /// <summary>Text from the final step.</summary>
    public string Text { get; init; } = "";

    /// <summary>Reasoning from the final step.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Tool calls from the final step (non-empty when returned to the caller unexecuted).</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Tool results from the final step.</summary>
    public IReadOnlyList<ToolResult> ToolResults { get; init; } = [];

    public FinishReason FinishReason { get; init; } = FinishReason.Stop;

    /// <summary>Usage from the final step.</summary>
    public Usage Usage { get; init; } = Usage.Empty;

    /// <summary>Usage aggregated across all steps.</summary>
    public Usage TotalUsage { get; init; } = Usage.Empty;

    public IReadOnlyList<StepResult> Steps { get; init; } = [];

    /// <summary>The final response.</summary>
    public required Response Response { get; init; }

    /// <summary>Parsed structured output (generate_object only).</summary>
    public JsonNode? Output { get; init; }

    /// <summary>The full conversation including every assistant and tool message produced.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];

    internal static GenerateResult FromSteps(IReadOnlyList<StepResult> steps, IReadOnlyList<Message> conversation)
    {
        var last = steps[^1];
        var total = Usage.Empty;
        foreach (var step in steps)
        {
            total += step.Usage;
        }

        return new GenerateResult
        {
            Text = last.Text,
            Reasoning = last.Reasoning,
            ToolCalls = last.ToolCalls,
            ToolResults = last.ToolResults,
            FinishReason = last.FinishReason,
            Usage = last.Usage,
            TotalUsage = total,
            Steps = steps,
            Response = last.Response,
            Messages = conversation,
        };
    }
}

using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>Total and per-step timeouts for high-level operations.</summary>
public sealed record TimeoutConfig
{
    /// <summary>Maximum time for the entire (possibly multi-step) operation.</summary>
    public TimeSpan? Total { get; init; }

    /// <summary>Maximum time for each individual LLM call.</summary>
    public TimeSpan? PerStep { get; init; }

    /// <summary>A simple duration is a total timeout.</summary>
    public static implicit operator TimeoutConfig(TimeSpan total) => new() { Total = total };
}

/// <summary>Repairs a tool call whose arguments failed parsing or validation. Return null to give up.</summary>
public delegate Task<ToolCall?> ToolCallRepairHandler(ToolCall call, string error, CancellationToken cancellationToken);

/// <summary>Parameters for <see cref="Ai.GenerateAsync(GenerateOptions, CancellationToken)"/> and <see cref="Ai.Stream"/>.</summary>
public record GenerateOptions
{
    /// <summary>The provider's native model id.</summary>
    public required string Model { get; init; }

    /// <summary>A simple text prompt (converted to one user message). Mutually exclusive with <see cref="Messages"/>.</summary>
    public string? Prompt { get; init; }

    /// <summary>The full conversation. Mutually exclusive with <see cref="Prompt"/>.</summary>
    public IReadOnlyList<Message>? Messages { get; init; }

    /// <summary>System instructions, prepended as a system message.</summary>
    public string? System { get; init; }

    /// <summary>Tools; those with execute handlers run automatically in the tool loop.</summary>
    public IReadOnlyList<Tool>? Tools { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    /// <summary>Maximum tool execution rounds. 0 disables automatic execution; total LLM calls are at most this + 1.</summary>
    public int MaxToolRounds { get; init; } = 1;

    /// <summary>Custom stop condition evaluated after each step of the tool loop.</summary>
    public Func<IReadOnlyList<StepResult>, bool>? StopWhen { get; init; }

    public ResponseFormat? ResponseFormat { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? StopSequences { get; init; }

    public string? ReasoningEffort { get; init; }

    public string? Provider { get; init; }

    public JsonObject? ProviderOptions { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Retries for transient errors per LLM call. Ignored when <see cref="RetryPolicy"/> is set.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Full retry policy; overrides <see cref="MaxRetries"/>.</summary>
    public RetryPolicy? RetryPolicy { get; init; }

    public TimeoutConfig? Timeout { get; init; }

    /// <summary>Client override; the module-level default client is used when null.</summary>
    public Client? Client { get; init; }

    /// <summary>Validate tool arguments against each tool's JSON Schema before execution (default true).</summary>
    public bool ValidateToolArguments { get; init; } = true;

    /// <summary>Optional repair hook for tool calls with invalid arguments.</summary>
    public ToolCallRepairHandler? RepairToolCall { get; init; }

    internal RetryPolicy EffectiveRetryPolicy => RetryPolicy ?? (MaxRetries == RetryPolicy.Default.MaxRetries
        ? RetryPolicy.Default
        : RetryPolicy.Default with { MaxRetries = Math.Max(0, MaxRetries) });
}

/// <summary>How <see cref="Ai.GenerateObjectAsync(GenerateObjectOptions, CancellationToken)"/> obtains structured output.</summary>
public enum ObjectGenerationStrategy
{
    /// <summary>Native JSON Schema output for OpenAI/Gemini/compatible, tool-based extraction for Anthropic, prompt-based otherwise.</summary>
    Auto,

    /// <summary>Use the provider's native structured output (<see cref="ResponseFormat"/>).</summary>
    Native,

    /// <summary>Force a single tool call whose input schema is the desired output schema.</summary>
    Tool,

    /// <summary>Inject schema instructions into the system prompt and parse the text.</summary>
    Prompt,
}

/// <summary>Parameters for structured output generation.</summary>
public sealed record GenerateObjectOptions : GenerateOptions
{
    /// <summary>JSON Schema the output must satisfy.</summary>
    public required JsonObject Schema { get; init; }

    /// <summary>Name used for the schema (OpenAI) or extraction tool (Anthropic).</summary>
    public string SchemaName { get; init; } = "response";

    public string? SchemaDescription { get; init; }

    public ObjectGenerationStrategy Strategy { get; init; } = ObjectGenerationStrategy.Auto;
}

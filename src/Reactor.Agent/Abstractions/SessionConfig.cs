namespace Reactor.Agent;

/// <summary>
/// Session limits, timeouts, and settings. Mutable so hosts can change configuration between turns
/// (e.g. reasoning effort); changes take effect on the next LLM call.
/// </summary>
public sealed class SessionConfig
{
    /// <summary>Maximum turns across the session; 0 = unlimited.</summary>
    public int MaxTurns { get; set; }

    /// <summary>Maximum tool rounds per user input; 0 = unlimited.</summary>
    public int MaxToolRoundsPerInput { get; set; }

    /// <summary>Default shell command timeout (10s).</summary>
    public int DefaultCommandTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// When set, the default shell command timeout, taking precedence over both the profile's default (e.g. Anthropic's
    /// 120 s) and <see cref="DefaultCommandTimeoutMs"/>. Used by hosts that set a timeout per task.
    /// </summary>
    public int? CommandTimeoutOverrideMs { get; set; }

    /// <summary>Upper bound for any command timeout (10 minutes).</summary>
    public int MaxCommandTimeoutMs { get; set; } = 600_000;

    /// <summary><c>low</c>, <c>medium</c>, <c>high</c> (some providers accept more), or null for the provider default.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Per-tool character limits overriding the defaults.</summary>
    public Dictionary<string, int> ToolOutputLimits { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Per-tool line limits overriding the defaults.</summary>
    public Dictionary<string, int> ToolLineLimits { get; init; } = new(StringComparer.Ordinal);

    public bool EnableLoopDetection { get; set; } = true;

    /// <summary>Number of recent tool calls inspected for repeating patterns.</summary>
    public int LoopDetectionWindow { get; set; } = 10;

    /// <summary>Maximum subagent nesting depth (1 = subagents cannot spawn their own subagents).</summary>
    public int MaxSubagentDepth { get; set; } = 1;

    /// <summary>User instructions appended last to the system prompt (highest priority).</summary>
    public string? UserInstructions { get; set; }

    /// <summary>When true the session uses streaming calls and emits ASSISTANT_TEXT_DELTA events.</summary>
    public bool StreamResponses { get; set; } = true;

    /// <summary>Retry policy for transient LLM API errors (429, 5xx, network).</summary>
    public Reactor.Llm.RetryPolicy LlmRetryPolicy { get; set; } = Reactor.Llm.RetryPolicy.Default;

    /// <summary>Optional maximum output tokens per LLM call.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Creates an independent copy (used for subagents).</summary>
    public SessionConfig Clone() => new()
    {
        MaxTurns = MaxTurns,
        MaxToolRoundsPerInput = MaxToolRoundsPerInput,
        DefaultCommandTimeoutMs = DefaultCommandTimeoutMs,
        CommandTimeoutOverrideMs = CommandTimeoutOverrideMs,
        MaxCommandTimeoutMs = MaxCommandTimeoutMs,
        ReasoningEffort = ReasoningEffort,
        ToolOutputLimits = new Dictionary<string, int>(ToolOutputLimits, StringComparer.Ordinal),
        ToolLineLimits = new Dictionary<string, int>(ToolLineLimits, StringComparer.Ordinal),
        EnableLoopDetection = EnableLoopDetection,
        LoopDetectionWindow = LoopDetectionWindow,
        MaxSubagentDepth = MaxSubagentDepth,
        UserInstructions = UserInstructions,
        StreamResponses = StreamResponses,
        LlmRetryPolicy = LlmRetryPolicy,
        MaxTokens = MaxTokens,
    };
}

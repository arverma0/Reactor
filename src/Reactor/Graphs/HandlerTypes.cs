namespace Reactor.Graphs;

/// <summary>Well-known handler type strings and the canonical shape-to-type mapping (spec 2.8).</summary>
public static class HandlerTypes
{
    public const string Start = "start";
    public const string Exit = "exit";
    public const string Codergen = "codergen";
    public const string WaitHuman = "wait.human";
    public const string Conditional = "conditional";
    public const string Parallel = "parallel";
    public const string FanIn = "parallel.fan_in";
    public const string Tool = "tool";
    public const string ManagerLoop = "stack.manager_loop";

    /// <summary>Shape → handler type.</summary>
    public static IReadOnlyDictionary<string, string> ShapeToType { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Mdiamond"] = Start,
        ["Msquare"] = Exit,
        ["box"] = Codergen,
        ["hexagon"] = WaitHuman,
        ["diamond"] = Conditional,
        ["component"] = Parallel,
        ["tripleoctagon"] = FanIn,
        ["parallelogram"] = Tool,
        ["house"] = ManagerLoop,
    };

    /// <summary>All built-in handler types.</summary>
    public static IReadOnlySet<string> BuiltIn { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Start, Exit, Codergen, WaitHuman, Conditional, Parallel, FanIn, Tool, ManagerLoop,
    };

    /// <summary>
    /// The handler type a node resolves to, independent of which handlers are registered:
    /// explicit <c>type</c>, then <c>shape</c> mapping, then id-based start/exit detection, then <c>codergen</c>.
    /// </summary>
    public static string Resolve(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var type = node.Type;
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type;
        }

        if (node.Attributes.TryGetValue(AttrKeys.Shape, out var shape) && ShapeToType.TryGetValue(shape, out var mapped))
        {
            return mapped;
        }

        if (!node.Attributes.ContainsKey(AttrKeys.Shape))
        {
            if (Graph.IsStartId(node.Id))
            {
                return Start;
            }

            if (Graph.IsExitId(node.Id))
            {
                return Exit;
            }
        }

        return Codergen;
    }
}

/// <summary>Attribute key constants used across the engine.</summary>
public static class AttrKeys
{
    // Graph
    public const string Goal = "goal";
    public const string Label = "label";
    public const string ModelStylesheet = "model_stylesheet";
    public const string DefaultMaxRetries = "default_max_retries";
    public const string DefaultMaxRetryLegacy = "default_max_retry";
    public const string RetryTarget = "retry_target";
    public const string FallbackRetryTarget = "fallback_retry_target";
    public const string DefaultFidelity = "default_fidelity";
    public const string DefaultThreadId = "default_thread_id";
    public const string StackChildDotfile = "stack.child_dotfile";
    public const string StackChildWorkdir = "stack.child_workdir";
    public const string ToolHooksPre = "tool_hooks.pre";
    public const string ToolHooksPost = "tool_hooks.post";

    // Node
    public const string Shape = "shape";
    public const string Type = "type";
    public const string Prompt = "prompt";
    public const string MaxRetries = "max_retries";
    public const string GoalGate = "goal_gate";
    public const string Fidelity = "fidelity";
    public const string ThreadId = "thread_id";
    public const string Class = "class";
    public const string Timeout = "timeout";
    public const string LlmModel = "llm_model";
    public const string LlmProvider = "llm_provider";
    public const string ReasoningEffort = "reasoning_effort";
    public const string AutoStatus = "auto_status";
    public const string AllowPartial = "allow_partial";
    public const string RetryPolicy = "retry_policy";
    public const string ToolCommand = "tool_command";
    public const string JoinPolicy = "join_policy";
    public const string MaxParallel = "max_parallel";
    public const string HumanDefaultChoice = "human.default_choice";
    public const string HumanTimeout = "human.timeout";

    /// <summary>Reactor extension: comma-separated artifact names the stage consumes (see <see cref="ArtifactPins"/>).</summary>
    public const string Inputs = "inputs";

    /// <summary>Reactor extension: comma-separated <c>name</c> or <c>name:path</c> artifacts the stage produces.</summary>
    public const string Outputs = "outputs";

    // Edge
    public const string Condition = "condition";
    public const string Weight = "weight";
    public const string LoopRestart = "loop_restart";
}

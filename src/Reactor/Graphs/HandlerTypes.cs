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

    /// <summary>
    /// Reactor extension: the most times the node may run in one run; the next visit goes to <see cref="OnMaxVisits"/>
    /// instead (or fails the run when that is unset).
    /// </summary>
    public const string MaxVisits = "max_visits";

    /// <summary>Reactor extension: where the run continues when a node with <see cref="MaxVisits"/> has used up its visits.</summary>
    public const string OnMaxVisits = "on_max_visits";

    /// <summary>
    /// Reactor extension: what a failure without an explicit failure route does — <c>continue</c> (the spec: unconditional
    /// edges are followed as if the stage had succeeded) or <c>stop</c> (only conditions, labels, suggested ids, retry
    /// targets, and unconditional edges into a diamond or fan-in route a failure; otherwise the run fails). Node attribute;
    /// the graph default is <see cref="DefaultOnFail"/>.
    /// </summary>
    public const string OnFail = "on_fail";

    /// <summary>Reactor extension: graph-wide default for <see cref="OnFail"/> (default <c>continue</c>).</summary>
    public const string DefaultOnFail = "default_on_fail";

    /// <summary>
    /// Reactor extension: what a stage timeout does — <c>retry</c> (default: the timeout is retried like other transient
    /// errors, with a hand-off from the stopped attempt), <c>fail</c> (not retried), or <c>wrap_up</c> (the agent is told
    /// time is up and gets <see cref="TimeoutGrace"/> to finish before the stage is stopped; needs a steerable backend).
    /// </summary>
    public const string OnTimeout = "on_timeout";

    /// <summary>
    /// Reactor extension: when to remind an agent stage that its time is running out (a duration from the stage start, or
    /// <c>off</c>); default 80% of <see cref="Timeout"/> for timeouts of 5 minutes or more. Needs a steerable backend.
    /// </summary>
    public const string SoftTimeout = "soft_timeout";

    /// <summary>Reactor extension: the grace period for <c>on_timeout=wrap_up</c> (default 10% of the timeout, 1–15 minutes).</summary>
    public const string TimeoutGrace = "timeout_grace";

    /// <summary>
    /// Reactor extension: a JSON file (relative to the working directory) whose numeric fields are recorded as the stage's
    /// outcome metrics after it runs (nested objects flatten to dotted names; arrays are ignored).
    /// </summary>
    public const string MetricsFile = "metrics_file";

    /// <summary>Reactor extension (graph): stop the run, resumably, once its recorded cost reaches this many US dollars.</summary>
    public const string MaxCost = "max_cost";

    /// <summary>
    /// Reactor extension (tool nodes): <c>true</c> leaves the color-related environment alone instead of asking the
    /// command for plain output (<c>NO_COLOR=1</c>, <c>FORCE_COLOR=0</c>, <c>TERM=dumb</c>).
    /// </summary>
    public const string ToolColor = "tool_color";

    /// <summary>
    /// Reactor extension (graph and node; also a stylesheet property): standing instructions for LLM stages, delivered by
    /// every backend (see <see cref="Execution.StageInstructions"/>).
    /// </summary>
    public const string Instructions = "instructions";

    /// <summary>Reactor extension (graph or node): <c>false</c> leaves out the built-in house rules.</summary>
    public const string HouseRules = "house_rules";

    // Edge
    public const string Condition = "condition";
    public const string Weight = "weight";
    public const string LoopRestart = "loop_restart";
}

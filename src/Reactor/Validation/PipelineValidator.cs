using System.Text;
using Reactor.Conditions;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Stylesheets;

namespace Reactor.Validation;

/// <summary>Diagnostic severity.</summary>
public enum Severity
{
    /// <summary>The pipeline will not execute.</summary>
    Error,

    /// <summary>The pipeline will execute but behavior may be unexpected.</summary>
    Warning,

    /// <summary>Informational note.</summary>
    Info,
}

/// <summary>A validation finding.</summary>
public sealed record Diagnostic(string Rule, Severity Severity, string Message)
{
    public string? NodeId { get; init; }

    /// <summary>The related edge as (from, to).</summary>
    public (string From, string To)? Edge { get; init; }

    /// <summary>Suggested fix.</summary>
    public string? Fix { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Severity.ToString().ToUpperInvariant()).Append(" [").Append(Rule).Append("] ");
        if (NodeId is not null)
        {
            sb.Append("node '").Append(NodeId).Append("': ");
        }
        else if (Edge is { } edge)
        {
            sb.Append("edge ").Append(edge.From).Append(" -> ").Append(edge.To).Append(": ");
        }

        sb.Append(Message);
        if (Fix is not null)
        {
            sb.Append(" (fix: ").Append(Fix).Append(')');
        }

        return sb.ToString();
    }
}

/// <summary>A lint rule. Custom rules are appended to the built-in rules.</summary>
public interface ILintRule
{
    string Name { get; }

    IEnumerable<Diagnostic> Apply(Graph graph);
}

/// <summary>Thrown by <see cref="PipelineValidator.ValidateOrRaise"/> when error-severity diagnostics exist.</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(IReadOnlyList<Diagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public IReadOnlyList<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == Severity.Error).ToList();

    private static string BuildMessage(IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        return $"Pipeline validation failed with {errors.Count} error(s):" + Environment.NewLine +
               string.Join(Environment.NewLine, errors.Select(e => "  " + e));
    }
}

/// <summary>Runs lint rules over a graph (spec section 7).</summary>
public static class PipelineValidator
{
    /// <summary>Runs the built-in rules (and <paramref name="extraRules"/>) over <paramref name="graph"/>.</summary>
    /// <param name="graph">The graph (normally after transforms).</param>
    /// <param name="extraRules">Custom rules appended after the built-in ones.</param>
    /// <param name="knownHandlerTypes">Registered handler types for the <c>type_known</c> rule (defaults to the built-in types).</param>
    public static IReadOnlyList<Diagnostic> Validate(
        Graph graph,
        IEnumerable<ILintRule>? extraRules = null,
        IEnumerable<string>? knownHandlerTypes = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var diagnostics = new List<Diagnostic>();
        foreach (var rule in BuiltInRules(knownHandlerTypes).Concat(extraRules ?? []))
        {
            diagnostics.AddRange(rule.Apply(graph));
        }

        return diagnostics;
    }

    /// <summary>Validates and throws <see cref="ValidationException"/> when any error-severity diagnostic exists.</summary>
    public static IReadOnlyList<Diagnostic> ValidateOrRaise(
        Graph graph,
        IEnumerable<ILintRule>? extraRules = null,
        IEnumerable<string>? knownHandlerTypes = null)
    {
        var diagnostics = Validate(graph, extraRules, knownHandlerTypes);
        if (diagnostics.Any(d => d.Severity == Severity.Error))
        {
            throw new ValidationException(diagnostics);
        }

        return diagnostics;
    }

    /// <summary>The built-in rule set.</summary>
    public static IReadOnlyList<ILintRule> BuiltInRules(IEnumerable<string>? knownHandlerTypes = null) =>
    [
        new StartNodeRule(),
        new TerminalNodeRule(),
        new ReachabilityRule(),
        new EdgeTargetExistsRule(),
        new StartNoIncomingRule(),
        new ExitNoOutgoingRule(),
        new ConditionSyntaxRule(),
        new StylesheetSyntaxRule(),
        new TypeKnownRule(knownHandlerTypes ?? HandlerTypes.BuiltIn),
        new FidelityValidRule(),
        new RetryTargetExistsRule(),
        new GoalGateHasRetryRule(),
        new PromptOnLlmNodesRule(),
        new ConditionValuesRule(),
        new AttributeTypesRule(),
        new DeadEndRule(),
        new ToolCommandRule(),
        new ParallelStructureRule(),
        new ManagerLoopChildRule(),
    ];
}

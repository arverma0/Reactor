using Reactor.Execution;

namespace Reactor.Conditions;

/// <summary>Evaluates edge conditions against the executing node's outcome and the run context (spec 10.4–10.5).</summary>
public static class ConditionEvaluator
{
    /// <summary>Context key prefix of the engine's per-node visit counters (<c>internal.visits.&lt;node&gt;</c>).</summary>
    public const string VisitsContextPrefix = "internal.visits.";

    /// <summary>Values treated as false for bare-key (truthy) clauses, in addition to the empty string.</summary>
    private static readonly HashSet<string> FalsyValues = new(StringComparer.OrdinalIgnoreCase) { "false", "0", "no", "none", "null" };

    /// <summary>Evaluates a condition string. An empty condition is always true.</summary>
    /// <exception cref="ConditionSyntaxException">The condition is malformed.</exception>
    public static bool Evaluate(string? condition, Outcome outcome, PipelineContext context) =>
        Evaluate(ConditionParser.Parse(condition), outcome, context);

    public static bool Evaluate(ConditionExpression expression, Outcome outcome, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var clause in expression.Clauses)
        {
            var actual = ResolveKey(clause.Key, outcome, context);
            var passes = clause.Operator switch
            {
                ConditionOperator.Equals => string.Equals(actual, clause.Value, StringComparison.Ordinal),
                ConditionOperator.NotEquals => !string.Equals(actual, clause.Value, StringComparison.Ordinal),
                ConditionOperator.Truthy => IsTruthy(actual),
                _ => CompareNumbers(actual, clause),
            };
            if (!passes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves a key: <c>outcome</c> → the status; <c>preferred_label</c> → the outcome's preferred label;
    /// <c>visits.&lt;node&gt;</c> (or <c>context.visits.&lt;node&gt;</c>) → how many times the engine has executed that node in
    /// this run (0 before its first visit; a Reactor extension); <c>context.*</c> → the context value (falling back to the
    /// key without the <c>context.</c> prefix); any other key (e.g. <c>metrics.&lt;name&gt;</c>) → a direct context lookup.
    /// Missing values resolve to the empty string.
    /// </summary>
    public static string ResolveKey(string key, Outcome outcome, PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(context);
        if (key == "outcome")
        {
            return outcome.Status.ToWire();
        }

        if (key == "preferred_label")
        {
            return outcome.PreferredLabel;
        }

        if (TryVisitsNode(key, out var visitedNode))
        {
            return context.TryGet(VisitsContextPrefix + visitedNode, out var visits) && visits is not null ? ContextValues.ToDisplayString(visits) : "0";
        }

        if (context.TryGet(key, out var value) && value is not null)
        {
            return ContextValues.ToDisplayString(value);
        }

        if (key.StartsWith("context.", StringComparison.Ordinal) &&
            context.TryGet(key["context.".Length..], out var unprefixed) && unprefixed is not null)
        {
            return ContextValues.ToDisplayString(unprefixed);
        }

        return "";
    }

    /// <summary>True for <c>visits.&lt;node&gt;</c> and <c>context.visits.&lt;node&gt;</c> keys.</summary>
    public static bool TryVisitsNode(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? nodeId)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var prefix in new[] { "visits.", "context.visits." })
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length)
            {
                nodeId = key[prefix.Length..];
                return true;
            }
        }

        nodeId = null;
        return false;
    }

    /// <summary>Truthiness for bare-key clauses: non-empty and not false/0/no/none/null.</summary>
    public static bool IsTruthy(string value) => value.Length > 0 && !FalsyValues.Contains(value);

    /// <summary>Numeric comparison; false when either side is not a finite number (e.g. a missing metric).</summary>
    private static bool CompareNumbers(string actual, ConditionClause clause)
    {
        if (!ConditionParser.TryParseNumber(actual, out var left) || !ConditionParser.TryParseNumber(clause.Value, out var right))
        {
            return false;
        }

        return clause.Operator switch
        {
            ConditionOperator.LessThan => left < right,
            ConditionOperator.LessOrEqual => left <= right,
            ConditionOperator.GreaterThan => left > right,
            ConditionOperator.GreaterOrEqual => left >= right,
            _ => false,
        };
    }
}

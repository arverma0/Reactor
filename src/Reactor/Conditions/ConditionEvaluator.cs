using Reactor.Execution;

namespace Reactor.Conditions;

/// <summary>Evaluates edge conditions against the executing node's outcome and the run context (spec 10.4–10.5).</summary>
public static class ConditionEvaluator
{
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
                _ => IsTruthy(actual),
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
    /// <c>context.*</c> → the context value (falling back to the key without the <c>context.</c> prefix);
    /// any other key → a direct context lookup. Missing values resolve to the empty string.
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

    /// <summary>Truthiness for bare-key clauses: non-empty and not false/0/no/none/null.</summary>
    public static bool IsTruthy(string value) => value.Length > 0 && !FalsyValues.Contains(value);
}

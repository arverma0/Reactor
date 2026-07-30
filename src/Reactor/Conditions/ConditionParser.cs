using System.Text;
using System.Text.RegularExpressions;

namespace Reactor.Conditions;

/// <summary>Comparison operator of a condition clause.</summary>
public enum ConditionOperator
{
    /// <summary><c>key=value</c>: exact, case-sensitive string equality.</summary>
    Equals,

    /// <summary><c>key!=value</c>.</summary>
    NotEquals,

    /// <summary>Bare <c>key</c>: true when the resolved value is truthy.</summary>
    Truthy,

    /// <summary>Reactor extension: <c>key&lt;number</c> (numeric; false when the value is not a number).</summary>
    LessThan,

    /// <summary>Reactor extension: <c>key&lt;=number</c>.</summary>
    LessOrEqual,

    /// <summary>Reactor extension: <c>key&gt;number</c>.</summary>
    GreaterThan,

    /// <summary>Reactor extension: <c>key&gt;=number</c>.</summary>
    GreaterOrEqual,
}

/// <summary>One clause of a condition: <c>Key Operator Literal</c> (or a bare key).</summary>
public sealed record ConditionClause(string Key, ConditionOperator Operator, string Value)
{
    /// <summary>True for the numeric comparison operators (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>).</summary>
    public bool IsNumeric => Operator is ConditionOperator.LessThan or ConditionOperator.LessOrEqual or ConditionOperator.GreaterThan or ConditionOperator.GreaterOrEqual;

    public override string ToString() => Operator switch
    {
        ConditionOperator.Equals => $"{Key}={Value}",
        ConditionOperator.NotEquals => $"{Key}!={Value}",
        ConditionOperator.LessThan => $"{Key}<{Value}",
        ConditionOperator.LessOrEqual => $"{Key}<={Value}",
        ConditionOperator.GreaterThan => $"{Key}>{Value}",
        ConditionOperator.GreaterOrEqual => $"{Key}>={Value}",
        _ => Key,
    };
}

/// <summary>A parsed condition: clauses AND-combined left to right. An empty condition always passes.</summary>
public sealed record ConditionExpression(IReadOnlyList<ConditionClause> Clauses, string Source)
{
    public static ConditionExpression Empty { get; } = new([], "");

    public bool IsEmpty => Clauses.Count == 0;
}

/// <summary>A malformed condition expression.</summary>
public sealed class ConditionSyntaxException(string message) : Exception(message);

/// <summary>
/// Parser for the edge condition language (spec section 10):
/// <c>ConditionExpr ::= Clause ('&amp;&amp;' Clause)*</c>, <c>Clause ::= Key ('=' | '!=') Literal</c> or a bare key.
/// Reactor extension: numeric comparisons <c>Key ('&lt;' | '&lt;=' | '&gt;' | '&gt;=') Number</c>, e.g.
/// <c>visits.polish&lt;3</c> or <c>metrics.qa_failed&gt;0</c>.
/// </summary>
public static partial class ConditionParser
{
    /// <summary>Parses <paramref name="condition"/>.</summary>
    /// <exception cref="ConditionSyntaxException">The expression is malformed.</exception>
    public static ConditionExpression Parse(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return ConditionExpression.Empty;
        }

        var clauses = new List<ConditionClause>();
        var parts = SplitClauses(condition);
        for (var i = 0; i < parts.Count; i++)
        {
            var text = parts[i].Trim();
            if (text.Length == 0)
            {
                throw new ConditionSyntaxException(
                    parts.Count == 1 ? "Condition is empty." : $"Clause {i + 1} of '{condition}' is empty (dangling '&&').");
            }

            clauses.Add(ParseClause(text, condition));
        }

        return new ConditionExpression(clauses, condition);
    }

    public static bool TryParse(string? condition, out ConditionExpression expression, out string? error)
    {
        try
        {
            expression = Parse(condition);
            error = null;
            return true;
        }
        catch (ConditionSyntaxException ex)
        {
            expression = ConditionExpression.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static ConditionClause ParseClause(string clause, string source)
    {
        if (clause.Contains("||", StringComparison.Ordinal) || ContainsWordOutsideQuotes(clause, " or "))
        {
            throw new ConditionSyntaxException($"'{clause}': OR is not supported; use separate edges or '&&'.");
        }

        var (opIndex, op, opLength) = FindOperator(clause);
        if (opIndex < 0)
        {
            if (clause.StartsWith('!'))
            {
                throw new ConditionSyntaxException($"'{clause}': negation is not supported; use 'key!=value'.");
            }

            var bareKey = ValidateKey(clause, clause);
            return new ConditionClause(bareKey, ConditionOperator.Truthy, "");
        }

        var key = ValidateKey(clause[..opIndex].Trim(), clause);
        var rawValue = clause[(opIndex + opLength)..].Trim();
        if (rawValue.StartsWith('=') || rawValue.StartsWith('!') || rawValue.StartsWith('<') || rawValue.StartsWith('>'))
        {
            throw new ConditionSyntaxException($"'{clause}': invalid operator; use '=', '!=', '<', '<=', '>', or '>=' (not '==' or '=>').");
        }

        _ = source;
        var value = ParseLiteral(rawValue, clause);
        var parsed = new ConditionClause(key, op, value);
        if (parsed.IsNumeric && !TryParseNumber(value, out _))
        {
            throw new ConditionSyntaxException($"'{clause}': '{op switch
            {
                ConditionOperator.LessThan => "<",
                ConditionOperator.LessOrEqual => "<=",
                ConditionOperator.GreaterThan => ">",
                _ => ">=",
            }}' compares numbers, but '{value}' is not a number.");
        }

        return parsed;
    }

    /// <summary>Parses a numeric literal or value (invariant culture; finite numbers only).</summary>
    public static bool TryParseNumber(string? text, out double number) =>
        double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    /// <summary>The first operator outside quotes; two-character operators (<c>!=</c>, <c>&lt;=</c>, <c>&gt;=</c>) win at a position.</summary>
    private static (int Index, ConditionOperator Operator, int Length) FindOperator(string clause)
    {
        char? quote = null;
        for (var i = 0; i < clause.Length; i++)
        {
            var c = clause[i];
            if (quote is not null)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            var next = i + 1 < clause.Length ? clause[i + 1] : '\0';
            switch (c)
            {
                case '!' when next == '=':
                    return (i, ConditionOperator.NotEquals, 2);
                case '<' when next == '=':
                    return (i, ConditionOperator.LessOrEqual, 2);
                case '>' when next == '=':
                    return (i, ConditionOperator.GreaterOrEqual, 2);
                case '=':
                    return (i, ConditionOperator.Equals, 1);
                case '<':
                    return (i, ConditionOperator.LessThan, 1);
                case '>':
                    return (i, ConditionOperator.GreaterThan, 1);
            }
        }

        return (-1, ConditionOperator.Truthy, 0);
    }

    private static string ValidateKey(string key, string clause)
    {
        if (key.Length == 0)
        {
            throw new ConditionSyntaxException($"'{clause}': missing key before the operator.");
        }

        if (!KeyPattern().IsMatch(key))
        {
            throw new ConditionSyntaxException($"'{clause}': invalid key '{key}'. Use 'outcome', 'preferred_label', 'context.<path>', 'visits.<node>', or 'metrics.<name>'.");
        }

        return key;
    }

    private static string ParseLiteral(string raw, string clause)
    {
        if (raw.Length == 0)
        {
            return "";
        }

        if (raw[0] is '"' or '\'')
        {
            var quote = raw[0];
            if (raw.Length < 2 || raw[^1] != quote)
            {
                throw new ConditionSyntaxException($"'{clause}': unterminated string literal.");
            }

            return Unescape(raw[1..^1]);
        }

        if (!BareLiteralPattern().IsMatch(raw))
        {
            throw new ConditionSyntaxException($"'{clause}': invalid literal '{raw}'. Quote values containing spaces or special characters.");
        }

        return raw;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                sb.Append(text[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => text[i],
                });
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }

    private static List<string> SplitClauses(string condition)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < condition.Length; i++)
        {
            var c = condition[i];
            if (quote is not null)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < condition.Length)
                {
                    current.Append(condition[++i]);
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c == '&' && i + 1 < condition.Length && condition[i + 1] == '&')
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
        }

        if (quote is not null)
        {
            throw new ConditionSyntaxException($"'{condition}': unterminated string literal.");
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static int IndexOfOutsideQuotes(string text, string token)
    {
        char? quote = null;
        for (var i = 0; i <= text.Length - token.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool ContainsWordOutsideQuotes(string text, string word) =>
        IndexOfOutsideQuotes(text.ToLowerInvariant(), word) >= 0;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_][A-Za-z0-9_\-]*)*$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[^\s""'&|=!<>]+$")]
    private static partial Regex BareLiteralPattern();
}

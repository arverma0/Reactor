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
}

/// <summary>One clause of a condition: <c>Key Operator Literal</c> (or a bare key).</summary>
public sealed record ConditionClause(string Key, ConditionOperator Operator, string Value)
{
    public override string ToString() => Operator switch
    {
        ConditionOperator.Equals => $"{Key}={Value}",
        ConditionOperator.NotEquals => $"{Key}!={Value}",
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

        var opIndex = IndexOfOutsideQuotes(clause, "!=");
        var op = ConditionOperator.NotEquals;
        var opLength = 2;
        if (opIndex < 0)
        {
            opIndex = IndexOfOutsideQuotes(clause, "=");
            op = ConditionOperator.Equals;
            opLength = 1;
        }

        if (opIndex < 0)
        {
            foreach (var bad in new[] { ">=", "<=", ">", "<" })
            {
                if (IndexOfOutsideQuotes(clause, bad) >= 0)
                {
                    throw new ConditionSyntaxException($"'{clause}': comparison operator '{bad}' is not supported; only '=' and '!=' are.");
                }
            }

            if (clause.StartsWith('!'))
            {
                throw new ConditionSyntaxException($"'{clause}': negation is not supported; use 'key!=value'.");
            }

            var bareKey = ValidateKey(clause, clause);
            return new ConditionClause(bareKey, ConditionOperator.Truthy, "");
        }

        var key = ValidateKey(clause[..opIndex].Trim(), clause);
        var rawValue = clause[(opIndex + opLength)..].Trim();
        if (rawValue.StartsWith('=') || rawValue.StartsWith('!'))
        {
            throw new ConditionSyntaxException($"'{clause}': invalid operator; use '=' or '!=' (not '==').");
        }

        _ = source;
        return new ConditionClause(key, op, ParseLiteral(rawValue, clause));
    }

    private static string ValidateKey(string key, string clause)
    {
        if (key.Length == 0)
        {
            throw new ConditionSyntaxException($"'{clause}': missing key before the operator.");
        }

        if (!KeyPattern().IsMatch(key))
        {
            throw new ConditionSyntaxException($"'{clause}': invalid key '{key}'. Use 'outcome', 'preferred_label', or 'context.<path>'.");
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

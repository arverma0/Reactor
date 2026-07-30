using System.Text;
using System.Text.RegularExpressions;
using Reactor.Graphs;

namespace Reactor.Stylesheets;

/// <summary>Kind of stylesheet selector, in increasing specificity.</summary>
public enum SelectorKind
{
    /// <summary><c>*</c> — all nodes (specificity 0).</summary>
    Universal = 0,

    /// <summary><c>box</c> — nodes with that shape (specificity 1).</summary>
    Shape = 1,

    /// <summary><c>.name</c> — nodes with that class (specificity 2).</summary>
    Class = 2,

    /// <summary><c>#id</c> — a specific node (specificity 3).</summary>
    Id = 3,
}

/// <summary>A selector such as <c>*</c>, <c>box</c>, <c>.code</c>, or <c>#review</c>.</summary>
public sealed record StyleSelector(SelectorKind Kind, string Value)
{
    public int Specificity => (int)Kind;

    public bool Matches(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Kind switch
        {
            SelectorKind.Universal => true,
            SelectorKind.Shape => string.Equals(node.Shape, Value, StringComparison.Ordinal),
            SelectorKind.Class => node.Classes.Contains(Value, StringComparer.Ordinal),
            SelectorKind.Id => string.Equals(node.Id, Value, StringComparison.Ordinal),
            _ => false,
        };
    }

    public override string ToString() => Kind switch
    {
        SelectorKind.Universal => "*",
        SelectorKind.Class => "." + Value,
        SelectorKind.Id => "#" + Value,
        _ => Value,
    };
}

/// <summary><c>property: value</c>.</summary>
public sealed record StyleDeclaration(string Property, string Value);

/// <summary>A selector with its declarations. <see cref="Order"/> is the rule's position in the stylesheet.</summary>
public sealed record StyleRule(StyleSelector Selector, IReadOnlyList<StyleDeclaration> Declarations, int Order);

/// <summary>A malformed model stylesheet.</summary>
public sealed class StylesheetParseException(string message, int position) : Exception($"{message} (at offset {position})")
{
    public int Position { get; } = position;
}

/// <summary>
/// CSS-like rules that set default LLM configuration on nodes (spec section 8). Resolution: highest specificity wins;
/// among equal specificity, the later rule wins. Explicit node attributes always take precedence (applied by
/// <see cref="Transforms.StylesheetTransform"/>).
/// </summary>
public sealed partial class ModelStylesheet
{
    /// <summary>
    /// The recognized properties: the spec's <c>llm_model</c>, <c>llm_provider</c>, <c>reasoning_effort</c>, and the Reactor
    /// extension <c>instructions</c> (standing instructions for the matched stages; quote the value).
    /// </summary>
    public static IReadOnlySet<string> Properties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AttrKeys.LlmModel, AttrKeys.LlmProvider, AttrKeys.ReasoningEffort, AttrKeys.Instructions,
    };

    public ModelStylesheet(IReadOnlyList<StyleRule> rules)
    {
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public IReadOnlyList<StyleRule> Rules { get; }

    public static ModelStylesheet Empty { get; } = new([]);

    /// <summary>Parses stylesheet text. Empty/whitespace text yields an empty stylesheet.</summary>
    /// <exception cref="StylesheetParseException">The text is malformed.</exception>
    public static ModelStylesheet Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        return new Parser(StripComments(text)).Parse();
    }

    public static bool TryParse(string? text, out ModelStylesheet stylesheet, out string? error)
    {
        try
        {
            stylesheet = Parse(text);
            error = null;
            return true;
        }
        catch (StylesheetParseException ex)
        {
            stylesheet = Empty;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The winning value for each property that any rule sets for <paramref name="node"/>.</summary>
    public IReadOnlyDictionary<string, string> Resolve(Node node) =>
        ResolveWithRules(node).ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);

    /// <summary>Like <see cref="Resolve"/>, but also reports the rule that supplied each winning value.</summary>
    public IReadOnlyDictionary<string, (string Value, StyleRule Rule)> ResolveWithRules(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var winners = new Dictionary<string, (string Value, StyleRule Rule)>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            if (!rule.Selector.Matches(node))
            {
                continue;
            }

            foreach (var declaration in rule.Declarations)
            {
                if (!winners.TryGetValue(declaration.Property, out var current) ||
                    rule.Selector.Specificity > current.Rule.Selector.Specificity ||
                    (rule.Selector.Specificity == current.Rule.Selector.Specificity && rule.Order >= current.Rule.Order))
                {
                    winners[declaration.Property] = (declaration.Value, rule);
                }
            }
        }

        return winners;
    }

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        char? quote = null;
        while (i < text.Length)
        {
            var c = text[i];
            if (quote is not null)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == quote)
                {
                    quote = null;
                }

                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new StylesheetParseException("Unterminated comment", i);
                }

                // Preserve offsets for error messages.
                sb.Append(' ', end + 2 - i);
                i = end + 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ClassPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.:/@+\-]+$")]
    private static partial Regex BareValuePattern();

    private sealed class Parser(string text)
    {
        private int _i;

        public ModelStylesheet Parse()
        {
            var rules = new List<StyleRule>();
            SkipWhitespace();
            while (_i < text.Length)
            {
                rules.Add(ParseRule(rules.Count));
                SkipWhitespace();
            }

            return new ModelStylesheet(rules);
        }

        private StyleRule ParseRule(int order)
        {
            var selector = ParseSelector();
            SkipWhitespace();
            Expect('{');
            var declarations = new List<StyleDeclaration>();
            while (true)
            {
                SkipWhitespace();
                if (_i >= text.Length)
                {
                    throw new StylesheetParseException($"Unterminated rule for selector '{selector}'; expected '}}'", _i);
                }

                if (text[_i] == '}')
                {
                    _i++;
                    break;
                }

                if (text[_i] == ';')
                {
                    _i++;
                    continue;
                }

                declarations.Add(ParseDeclaration());
                SkipWhitespace();
                if (_i < text.Length && text[_i] is not (';' or '}'))
                {
                    throw new StylesheetParseException($"Expected ';' or '}}' after declaration in rule '{selector}'", _i);
                }
            }

            if (declarations.Count == 0)
            {
                throw new StylesheetParseException($"Rule '{selector}' has no declarations", _i);
            }

            return new StyleRule(selector, declarations, order);
        }

        private StyleSelector ParseSelector()
        {
            var start = _i;
            if (text[_i] == '*')
            {
                _i++;
                return new StyleSelector(SelectorKind.Universal, "*");
            }

            var prefix = text[_i];
            if (prefix is '.' or '#')
            {
                _i++;
            }

            var name = ReadWhile(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
            switch (prefix)
            {
                case '.':
                    if (!ClassPattern().IsMatch(name))
                    {
                        throw new StylesheetParseException($"Invalid class selector '.{name}'", start);
                    }

                    return new StyleSelector(SelectorKind.Class, name);
                case '#':
                    if (!IdentifierPattern().IsMatch(name))
                    {
                        throw new StylesheetParseException($"Invalid id selector '#{name}'", start);
                    }

                    return new StyleSelector(SelectorKind.Id, name);
                default:
                    if (!IdentifierPattern().IsMatch(name))
                    {
                        throw new StylesheetParseException(
                            name.Length == 0 ? $"Expected a selector ('*', shape, '.class', or '#id') but found '{text[start]}'" : $"Invalid shape selector '{name}'",
                            start);
                    }

                    return new StyleSelector(SelectorKind.Shape, name);
            }
        }

        private StyleDeclaration ParseDeclaration()
        {
            var start = _i;
            var property = ReadWhile(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
            if (property.Length == 0)
            {
                throw new StylesheetParseException($"Expected a property name but found '{text[_i]}'", _i);
            }

            if (!Properties.Contains(property))
            {
                throw new StylesheetParseException($"Unknown property '{property}'; supported: {string.Join(", ", Properties)}", start);
            }

            SkipWhitespace();
            Expect(':');
            SkipWhitespace();
            if (_i >= text.Length)
            {
                throw new StylesheetParseException($"Missing value for '{property}'", _i);
            }

            string value;
            if (text[_i] is '"' or '\'')
            {
                var quote = text[_i++];
                var sb = new StringBuilder();
                while (_i < text.Length && text[_i] != quote)
                {
                    if (text[_i] == '\\' && _i + 1 < text.Length)
                    {
                        _i++;
                    }

                    sb.Append(text[_i++]);
                }

                if (_i >= text.Length)
                {
                    throw new StylesheetParseException($"Unterminated string value for '{property}'", start);
                }

                _i++;
                value = sb.ToString();
            }
            else
            {
                value = ReadWhile(c => c is not (';' or '}') && !char.IsWhiteSpace(c));
                if (!BareValuePattern().IsMatch(value))
                {
                    throw new StylesheetParseException($"Invalid value '{value}' for '{property}'; quote it", start);
                }
            }

            return new StyleDeclaration(property, value);
        }

        private string ReadWhile(Func<char, bool> predicate)
        {
            var start = _i;
            while (_i < text.Length && predicate(text[_i]))
            {
                _i++;
            }

            return text[start.._i];
        }

        private void SkipWhitespace()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i]))
            {
                _i++;
            }
        }

        private void Expect(char c)
        {
            if (_i >= text.Length || text[_i] != c)
            {
                throw new StylesheetParseException($"Expected '{c}'", _i);
            }

            _i++;
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Llm.Utilities;

/// <summary>
/// Incremental JSON parsing: completes a possibly-truncated JSON document (closing open strings, arrays, and objects and
/// dropping incomplete trailing tokens) so partial structured output can be observed while it streams.
/// </summary>
public static class PartialJson
{
    private enum Expect
    {
        Value,
        KeyOrEnd,
        Key,
        Colon,
        CommaOrEnd,
    }

    /// <summary>
    /// Parses the longest valid prefix of <paramref name="text"/> as JSON. Unterminated string values are kept (closed);
    /// incomplete keys, literals, and numbers are dropped. Returns null when no value can be recovered.
    /// </summary>
    public static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var complete = JsonHelpers.ParseOrNull(text);
        if (complete is not null)
        {
            return complete;
        }

        var candidate = Complete(text);
        return candidate is null ? null : JsonHelpers.ParseOrNull(candidate);
    }

    /// <summary>Returns a syntactically closed version of the truncated document, or null.</summary>
    public static string? Complete(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stack = new Stack<char>();
        var expect = Expect.Value;
        var inString = false;
        var stringIsKey = false;
        var escape = false;
        var safeLength = -1;
        string? safeClosers = null;
        var started = false;

        void MarkSafe(int length)
        {
            safeLength = length;
            safeClosers = Closers(stack);
        }

        var i = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                    if (stringIsKey)
                    {
                        expect = Expect.Colon;
                    }
                    else
                    {
                        expect = Expect.CommaOrEnd;
                        MarkSafe(i + 1);
                        if (stack.Count == 0)
                        {
                            return text[..(i + 1)];
                        }
                    }
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            switch (c)
            {
                case '{':
                    if (expect != Expect.Value)
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    started = true;
                    stack.Push('}');
                    expect = Expect.KeyOrEnd;
                    MarkSafe(i + 1);
                    break;
                case '[':
                    if (expect != Expect.Value)
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    started = true;
                    stack.Push(']');
                    expect = Expect.Value;
                    MarkSafe(i + 1);
                    break;
                case '}':
                case ']':
                    if (stack.Count == 0 || stack.Peek() != c)
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    stack.Pop();
                    expect = Expect.CommaOrEnd;
                    MarkSafe(i + 1);
                    if (stack.Count == 0)
                    {
                        return text[..(i + 1)];
                    }

                    break;
                case ',':
                    if (expect != Expect.CommaOrEnd || stack.Count == 0)
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    expect = stack.Peek() == '}' ? Expect.Key : Expect.Value;
                    break;
                case ':':
                    if (expect != Expect.Colon)
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    expect = Expect.Value;
                    break;
                case '"':
                    started = true;
                    inString = true;
                    stringIsKey = expect is Expect.Key or Expect.KeyOrEnd;
                    if (!stringIsKey && expect != Expect.Value)
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    break;
                default:
                    if (expect != Expect.Value)
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    started = true;
                    var start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or '}' or ']' or ':'))
                    {
                        i++;
                    }

                    var token = text[start..i];
                    var atEnd = i >= text.Length;
                    i--;
                    if (!IsCompleteScalar(token, atEnd))
                    {
                        return Fallback(text, safeLength, safeClosers);
                    }

                    expect = Expect.CommaOrEnd;
                    MarkSafe(start + token.Length);
                    if (stack.Count == 0)
                    {
                        return token;
                    }

                    break;
            }
        }

        if (!started)
        {
            return null;
        }

        if (inString && !stringIsKey)
        {
            var sb = new StringBuilder(text);
            if (escape)
            {
                sb.Length--;
            }

            // Drop a dangling partial \uXXXX escape.
            var tail = sb.ToString();
            var lastEscape = tail.LastIndexOf("\\u", StringComparison.Ordinal);
            if (lastEscape >= 0 && tail.Length - lastEscape < 6)
            {
                sb.Length = lastEscape;
            }

            sb.Append('"');
            sb.Append(Closers(stack));
            return sb.ToString();
        }

        return Fallback(text, safeLength, safeClosers);
    }

    private static string? Fallback(string text, int safeLength, string? safeClosers)
    {
        if (safeLength < 0 || safeClosers is null)
        {
            return null;
        }

        var prefix = text[..safeLength].TrimEnd();
        if (prefix.EndsWith(','))
        {
            prefix = prefix[..^1];
        }

        return prefix + safeClosers;
    }

    private static bool IsCompleteScalar(string token, bool atEnd)
    {
        if (token is "true" or "false" or "null")
        {
            return true;
        }

        if (token.Length == 0)
        {
            return false;
        }

        if (!(char.IsAsciiDigit(token[0]) || token[0] == '-'))
        {
            return false;
        }

        // A number at the very end of the buffer may still be growing; accept it only if it parses as complete.
        if (atEnd && (token.EndsWith('.') || token.EndsWith('-') || token.EndsWith('e') || token.EndsWith('E') || token.EndsWith('+')))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(token);
            return doc.RootElement.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Closers(Stack<char> stack) => new(stack.ToArray());
}

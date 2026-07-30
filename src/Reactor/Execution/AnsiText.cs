using System.Text;

namespace Reactor.Execution;

/// <summary>
/// Turns terminal output into plain text, as a terminal would show it:
/// <list type="bullet">
/// <item>ANSI/VT escape sequences are removed: CSI sequences (colors, cursor movement, erase; private and intermediate
/// bytes included), OSC strings (hyperlinks, window titles) terminated by BEL or ST, DCS/SOS/PM/APC strings, and other
/// escape sequences (<c>ESC 7</c>, <c>ESC ( B</c>, …), in their 7-bit and 8-bit (C1) forms;</item>
/// <item>a carriage return that is not part of a line ending starts the line over, so progress output keeps only its
/// last state (<c>"10%\r50%\r100%"</c> becomes <c>"100%"</c>), and a backspace removes the character before it;</item>
/// <item>CRLF (and CR runs before LF) become LF; other control characters are dropped. Tabs and newlines are kept.</item>
/// </list>
/// </summary>
public static class AnsiText
{
    private const char Esc = '\u001b';

    /// <summary>Returns <paramref name="text"/> without escape sequences and control characters (see <see cref="AnsiText"/>).</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        if (!HasControlCharacters(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var lineStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '\n':
                    sb.Append('\n');
                    lineStart = sb.Length;
                    i++;
                    break;
                case '\r':
                    var next = SkipCarriageReturns(text, i);
                    if (!EndsLine(text, next))
                    {
                        // A carriage return inside a line: what follows overwrites the line.
                        sb.Length = lineStart;
                    }

                    i = next;
                    break;
                case '\b':
                    if (sb.Length > lineStart)
                    {
                        sb.Length--;
                    }

                    i++;
                    break;
                case '\t':
                    sb.Append(c);
                    i++;
                    break;
                case Esc:
                    i = SkipEscape(text, i + 1);
                    break;
                case '\u009b':
                    i = SkipControlSequence(text, i + 1);
                    break;
                case '\u0090' or '\u0098' or '\u009d' or '\u009e' or '\u009f':
                    i = SkipControlString(text, i + 1);
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        sb.Append(c);
                    }

                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when <see cref="Strip"/> changes <paramref name="text"/> beyond normalizing line endings: it contains escape
    /// sequences, carriage-return overwrites, backspaces, or other control characters.
    /// </summary>
    public static bool NeedsStripping(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\t' || !char.IsControl(c))
            {
                continue;
            }

            if (c != '\r')
            {
                return true;
            }

            var next = SkipCarriageReturns(text, i);
            if (next < text.Length && text[next] != '\n')
            {
                return true;
            }

            i = next - 1;
        }

        return false;
    }

    private static bool HasControlCharacters(string text)
    {
        foreach (var c in text)
        {
            if (c is not ('\n' or '\t') && char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static int SkipCarriageReturns(string text, int i)
    {
        while (i < text.Length && text[i] == '\r')
        {
            i++;
        }

        return i;
    }

    // True when only carriage returns and escape sequences (which print nothing) stand between i and the end of the line,
    // so a carriage return before i is part of a line ending (e.g. "text\r\u001b[0m\r\n") rather than an overwrite.
    private static bool EndsLine(string text, int i)
    {
        while (i < text.Length)
        {
            switch (text[i])
            {
                case '\n':
                    return true;
                case '\r':
                    i++;
                    break;
                case Esc:
                    i = SkipEscape(text, i + 1);
                    break;
                case '\u009b':
                    i = SkipControlSequence(text, i + 1);
                    break;
                case '\u0090' or '\u0098' or '\u009d' or '\u009e' or '\u009f':
                    i = SkipControlString(text, i + 1);
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    // After ESC: CSI and control strings, nF sequences (intermediate bytes, then a final byte), and two-character escapes.
    private static int SkipEscape(string text, int i)
    {
        if (i >= text.Length)
        {
            return i;
        }

        var c = text[i];
        if (c == '[')
        {
            return SkipControlSequence(text, i + 1);
        }

        if (c is ']' or 'P' or 'X' or '^' or '_')
        {
            return SkipControlString(text, i + 1);
        }

        if (c is >= ' ' and <= '/')
        {
            while (i < text.Length && text[i] is >= ' ' and <= '/')
            {
                i++;
            }

            return i < text.Length && text[i] is >= '0' and <= '~' ? i + 1 : i;
        }

        // A lone ESC (followed by a control or non-ASCII character) is dropped by itself.
        return c is >= '0' and <= '~' ? i + 1 : i;
    }

    // CSI: parameter bytes 0x30-0x3F (digits, ';', ':', and the private markers '<=>?'), intermediate bytes 0x20-0x2F,
    // then a final byte 0x40-0x7E. A malformed or truncated sequence ends at the first byte that does not fit.
    private static int SkipControlSequence(string text, int i)
    {
        while (i < text.Length && text[i] is >= '0' and <= '?')
        {
            i++;
        }

        while (i < text.Length && text[i] is >= ' ' and <= '/')
        {
            i++;
        }

        return i < text.Length && text[i] is >= '@' and <= '~' ? i + 1 : i;
    }

    // OSC, DCS, SOS, PM, and APC strings end at BEL or ST (ESC \ or 0x9C). An unterminated string ends at the end of the
    // line, or at the next escape sequence, so a stray introducer cannot swallow the rest of the output.
    private static int SkipControlString(string text, int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\u0007' or '\u009c')
            {
                return i + 1;
            }

            if (c == Esc)
            {
                return i + 1 < text.Length && text[i + 1] == '\\' ? i + 2 : i;
            }

            if (c == '\n')
            {
                return i;
            }

            i++;
        }

        return i;
    }
}

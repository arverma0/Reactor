using System.Text;

namespace Reactor.Agent.Environments;

/// <summary>Decoded text plus what is needed to write it back faithfully.</summary>
/// <param name="Text">The decoded text (without any byte order mark).</param>
/// <param name="HasUtf8Bom">True when the bytes started with a UTF-8 byte order mark.</param>
internal readonly record struct DecodedText(string Text, bool HasUtf8Bom);

/// <summary>
/// Shared text-file helpers: byte order mark detection, binary detection, decoding, and line splitting. Lines are split on
/// <c>\n</c> with a preceding <c>\r</c> removed, and a trailing newline does not start an extra line, so line numbers agree
/// with ripgrep and editors.
/// </summary>
internal static class TextFileDecoding
{
    /// <summary>Number of leading bytes inspected for NUL bytes when deciding whether a file is binary.</summary>
    public const int BinaryProbeLength = 8192;

    /// <summary>UTF-8 without a byte order mark (the encoding used for all writes).</summary>
    public static UTF8Encoding Utf8 { get; } = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The byte order mark character; writing it first with <see cref="Utf8"/> produces a UTF-8 BOM.</summary>
    public const char ByteOrderMark = '\uFEFF';

    /// <summary>Detects a byte order mark and returns the matching encoding and BOM length.</summary>
    public static (Encoding Encoding, int BomLength)? DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Utf8, 3);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4);
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: false), 4);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);
        }

        return null;
    }

    /// <summary>True when the content looks binary: a NUL byte in the first 8 KB (UTF-16/32 text with a BOM is not binary).</summary>
    public static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (DetectBom(bytes) is { BomLength: not 3 })
        {
            return false;
        }

        return bytes[..Math.Min(bytes.Length, BinaryProbeLength)].IndexOf((byte)0) >= 0;
    }

    /// <summary>Decodes bytes as text, honoring a byte order mark and defaulting to UTF-8.</summary>
    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        if (DetectBom(bytes) is { } bom)
        {
            return new DecodedText(bom.Encoding.GetString(bytes[bom.BomLength..]), bom.BomLength == 3);
        }

        return new DecodedText(Utf8.GetString(bytes), false);
    }

    /// <summary>Splits text into lines on <c>\n</c>, dropping the <c>\r</c> of CRLF terminators.</summary>
    public static List<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(text[start..]);
                break;
            }

            var end = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(text[start..end]);
            start = newline + 1;
        }

        return lines;
    }

    /// <summary>True when <paramref name="text"/> ends with a line terminator.</summary>
    public static bool EndsWithNewline(string text) => text.Length > 0 && text[^1] == '\n';

    /// <summary>The dominant line terminator of <paramref name="text"/>: <c>\r\n</c> when CRLF terminators outnumber bare LFs.</summary>
    public static string DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return crlf > lf ? "\r\n" : "\n";
    }

    /// <summary>Converts all line terminators in <paramref name="text"/> to <paramref name="lineEnding"/>.</summary>
    public static string NormalizeLineEndings(string text, string lineEnding)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return lineEnding == "\n" ? normalized : normalized.Replace("\n", lineEnding, StringComparison.Ordinal);
    }
}

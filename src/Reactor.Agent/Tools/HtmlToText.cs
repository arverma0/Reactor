using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Reactor.Agent.Tools;

/// <summary>
/// Converts HTML to readable plain text: drops scripts, styles, and other non-content elements, turns block elements and
/// line breaks into newlines (list items become "- " lines), strips the remaining tags, decodes entities, collapses
/// runs of spaces and blank lines, and keeps the whitespace of <c>&lt;pre&gt;</c> blocks.
/// </summary>
internal static partial class HtmlToText
{
    public static string Convert(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = html.Replace("\0", "", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        text = Comments().Replace(text, "");
        text = NonContentElements().Replace(text, "");

        var preformatted = new List<string>();
        text = Preformatted().Replace(text, match =>
        {
            preformatted.Add(WebUtility.HtmlDecode(Tag().Replace(match.Groups["body"].Value, "")));
            return "\n\0" + (preformatted.Count - 1).ToString(CultureInfo.InvariantCulture) + "\0\n";
        });

        text = LineBreak().Replace(text, "\n");
        text = ListItem().Replace(text, "\n- ");
        text = CellEnd().Replace(text, " ");
        text = BlockElement().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);

        var lines = text.Split('\n').Select(line => HorizontalWhitespace().Replace(line, " ").Trim());
        text = BlankLines().Replace(string.Join("\n", lines), "\n\n").Trim();

        if (preformatted.Count > 0)
        {
            text = Placeholder().Replace(text, match =>
                preformatted[int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)].Trim('\n', '\r'));
        }

        return text;
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<(?<tag>script|style|noscript|template|svg|iframe|object|canvas)\b[^>]*>.*?</\k<tag>\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonContentElements();

    [GeneratedRegex(@"<pre\b[^>]*>(?<body>.*?)</pre\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Preformatted();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CellEnd();

    [GeneratedRegex(
        @"</?(?:p|div|h[1-6]|ul|ol|dl|dt|dd|table|thead|tbody|tfoot|tr|caption|section|article|header|footer|nav|aside|main|blockquote|figure|figcaption|form|fieldset|address|details|summary|hr|title|body|html)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockElement();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\f\v\u00A0\u2000-\u200A\u202F\u205F\u3000]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\0(?<index>\d+)\0", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

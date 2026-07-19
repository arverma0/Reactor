using System.Globalization;
using System.Text.RegularExpressions;

namespace Reactor.Graphs;

/// <summary>Parsing helpers for typed DOT attribute values (strings, integers, floats, booleans, durations).</summary>
public static partial class AttributeValues
{
    /// <summary>Parses <c>true</c>/<c>false</c> (case-insensitive; also <c>yes</c>/<c>no</c>, <c>1</c>/<c>0</c>).</summary>
    public static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => null,
    };

    public static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result) ? result : null;

    public static double? ParseDouble(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>
    /// Parses a duration: an integer (or decimal) followed by <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c>, or <c>d</c>
    /// (e.g. <c>900s</c>, <c>15m</c>, <c>250ms</c>). A bare number is interpreted as seconds.
    /// </summary>
    public static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = DurationPattern().Match(value.Trim());
        if (!match.Success)
        {
            return null;
        }

        var number = double.Parse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return match.Groups["u"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(number),
            "s" or "" => TimeSpan.FromSeconds(number),
            "m" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            "d" => TimeSpan.FromDays(number),
            _ => null,
        };
    }

    /// <summary>Formats a duration in the most natural single unit (e.g. <c>15m</c>).</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds % 1000 != 0)
        {
            return ((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";
        }

        var seconds = (long)duration.TotalSeconds;
        if (seconds % 86400 == 0 && seconds > 0)
        {
            return (seconds / 86400).ToString(CultureInfo.InvariantCulture) + "d";
        }

        if (seconds % 3600 == 0 && seconds > 0)
        {
            return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + "h";
        }

        if (seconds % 60 == 0 && seconds > 0)
        {
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + "m";
        }

        return seconds.ToString(CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>True for a valid DOT identifier: <c>[A-Za-z_][A-Za-z0-9_]*</c>.</summary>
    public static bool IsIdentifier(string? value) => value is not null && IdentifierPattern().IsMatch(value);

    /// <summary>True for an attribute key: an identifier or dot-qualified identifier.</summary>
    public static bool IsAttributeKey(string? value) => value is not null && KeyPattern().IsMatch(value);

    /// <summary>True for an unquoted value: integer, float, duration, boolean, or bare value.</summary>
    public static bool IsUnquotedValue(string? value) => value is not null && UnquotedValuePattern().IsMatch(value);

    [GeneratedRegex(@"^(?<n>\d+(?:\.\d+)?)\s*(?<u>ms|s|m|h|d)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^(?:-?\d+|-?\d*\.\d+|\d+(?:ms|s|m|h|d)|[A-Za-z_][A-Za-z0-9_.:\-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnquotedValuePattern();
}

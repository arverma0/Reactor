using System.Globalization;
using System.Text;

namespace Reactor.Agent;

/// <summary>
/// Tool output truncation (spec section 5): character-based truncation always runs first (it handles pathological
/// cases like a 10 MB single-line file), then line-based truncation for readability. Truncation markers tell the model
/// what was removed; the full output remains available in the TOOL_CALL_END event.
/// </summary>
public static class ToolOutputTruncation
{
    public const string HeadTail = "head_tail";
    public const string Tail = "tail";

    /// <summary>Character limit for tools without an explicit default.</summary>
    public const int FallbackCharLimit = 30_000;

    public static IReadOnlyDictionary<string, int> DefaultCharLimits { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["read_file"] = 50_000,
        ["read_many_files"] = 50_000,
        ["shell"] = 30_000,
        ["grep"] = 20_000,
        ["glob"] = 20_000,
        ["list_dir"] = 20_000,
        ["edit_file"] = 10_000,
        ["apply_patch"] = 10_000,
        ["write_file"] = 1_000,
        ["spawn_agent"] = 20_000,
        ["wait"] = 20_000,
        ["web_fetch"] = 30_000,
    };

    public static IReadOnlyDictionary<string, string> DefaultModes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["read_file"] = HeadTail,
        ["read_many_files"] = HeadTail,
        ["shell"] = HeadTail,
        ["grep"] = Tail,
        ["glob"] = Tail,
        ["list_dir"] = Tail,
        ["edit_file"] = Tail,
        ["apply_patch"] = Tail,
        ["write_file"] = Tail,
        ["spawn_agent"] = HeadTail,
        ["wait"] = HeadTail,
        ["web_fetch"] = HeadTail,
    };

    /// <summary>Line limits applied after character truncation (tools not listed have no line limit).</summary>
    public static IReadOnlyDictionary<string, int> DefaultLineLimits { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["shell"] = 256,
        ["grep"] = 200,
        ["glob"] = 500,
    };

    /// <summary>Character-based truncation with a <c>head_tail</c> or <c>tail</c> strategy.</summary>
    public static string TruncateOutput(string output, int maxChars, string mode)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maxChars <= 0 || output.Length <= maxChars)
        {
            return output;
        }

        var removed = output.Length - maxChars;
        if (string.Equals(mode, Tail, StringComparison.Ordinal))
        {
            var start = AdjustStart(output, output.Length - maxChars);
            return "[WARNING: Tool output was truncated. First " + removed.ToString(CultureInfo.InvariantCulture) +
                   " characters were removed. The full output is available in the event stream.]\n\n" +
                   output[start..];
        }

        var half = maxChars / 2;
        var headEnd = AdjustEnd(output, half);
        var tailStart = AdjustStart(output, output.Length - half);
        return output[..headEnd] +
               "\n\n[WARNING: Tool output was truncated. " + removed.ToString(CultureInfo.InvariantCulture) +
               " characters were removed from the middle. The full output is available in the event stream. " +
               "If you need to see specific parts, re-run the tool with more targeted parameters.]\n\n" +
               output[tailStart..];
    }

    /// <summary>Line-based head/tail truncation.</summary>
    public static string TruncateLines(string output, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maxLines <= 0)
        {
            return output;
        }

        var lines = output.Split('\n');
        if (lines.Length <= maxLines)
        {
            return output;
        }

        var headCount = maxLines / 2;
        var tailCount = maxLines - headCount;
        var omitted = lines.Length - headCount - tailCount;
        var sb = new StringBuilder();
        sb.AppendJoin('\n', lines.Take(headCount));
        sb.Append("\n[... ").Append(omitted.ToString(CultureInfo.InvariantCulture)).Append(" lines omitted ...]\n");
        sb.AppendJoin('\n', lines.Skip(lines.Length - tailCount));
        return sb.ToString();
    }

    /// <summary>The full truncation pipeline for a tool's output: characters first, then lines.</summary>
    public static string TruncateToolOutput(string output, string toolName, SessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(config);

        var maxChars = config.ToolOutputLimits.TryGetValue(toolName, out var configured)
            ? configured
            : DefaultCharLimits.GetValueOrDefault(toolName, FallbackCharLimit);
        var mode = DefaultModes.GetValueOrDefault(toolName, HeadTail);
        var result = TruncateOutput(output, maxChars, mode);

        int? maxLines = config.ToolLineLimits.TryGetValue(toolName, out var configuredLines)
            ? configuredLines
            : DefaultLineLimits.TryGetValue(toolName, out var defaultLines) ? defaultLines : null;
        if (maxLines is int lineLimit)
        {
            result = TruncateLines(result, lineLimit);
        }

        return result;
    }

    // Avoid splitting a UTF-16 surrogate pair at a cut point.
    private static int AdjustEnd(string s, int end) =>
        end > 0 && end < s.Length && char.IsHighSurrogate(s[end - 1]) ? end - 1 : end;

    private static int AdjustStart(string s, int start) =>
        start > 0 && start < s.Length && char.IsLowSurrogate(s[start]) ? start + 1 : start;
}

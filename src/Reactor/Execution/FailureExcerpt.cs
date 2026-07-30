using System.Text;
using System.Text.RegularExpressions;

namespace Reactor.Execution;

/// <summary>
/// Picks the lines that explain a failure out of a command's output, for failure reasons. Output that fits is returned
/// whole. Longer output is reduced to the lines that look like failures — failing tests, compiler errors, exceptions,
/// tracebacks, panics, <c>npm ERR!</c>, non-zero failure counts — each with a little context, de-duplicated and in their
/// original order, ahead of the end of the output; skipped stretches are marked with <c>…</c> lines. Lines reporting
/// success (<c>✓ …</c>, <c>PASS …</c>) and zero counts (<c>0 failed</c>, <c>Errors: 0</c>) are not failure lines.
/// </summary>
public static partial class FailureExcerpt
{
    /// <summary>The default size of an excerpt, in characters.</summary>
    public const int DefaultMaxChars = 1500;

    private const int ContextBefore = 1;
    private const int ContextAfter = 2;
    private const int MaxLineChars = 300;
    private const int MaxTailReserve = 400;
    private const int MaxCandidates = 200;
    private const string Gap = "…";

    /// <summary>
    /// An excerpt of a failed command's output: <paramref name="stdout"/> followed by <paramref name="stderr"/> (test
    /// runners report failures on stdout while stderr may hold only a wrapper's noise, and vice versa), within
    /// <paramref name="maxChars"/>. Pass text that has been through <see cref="AnsiText.Strip"/>.
    /// </summary>
    public static string Extract(string? stdout, string? stderr, int maxChars = DefaultMaxChars)
    {
        var output = (stdout ?? "").Trim();
        var errors = (stderr ?? "").Trim();
        return Extract(output.Length == 0 ? errors : errors.Length == 0 ? output : output + "\n" + errors, maxChars);
    }

    /// <summary>An excerpt of <paramref name="text"/> within <paramref name="maxChars"/> (see <see cref="FailureExcerpt"/>).</summary>
    public static string Extract(string? text, int maxChars = DefaultMaxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 16);
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        var lines = trimmed.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = Clip(lines[i].TrimEnd('\r', ' ', '\t'), MaxLineChars);
        }

        var selected = new SortedSet<int>();
        var tailReserve = Math.Min(maxChars / 3, MaxTailReserve);
        SelectFailureLines(lines, selected, maxChars - tailReserve);

        // The end of the output (the summary, the last error), joining a failure excerpt that already reaches the end.
        // It is kept short next to failure lines; without them it is all there is to show.
        var tailBudget = selected.Count > 0 ? tailReserve : maxChars;
        var tailChars = 0;
        var end = lines.Length - 1;
        while (end >= 0 && selected.Contains(end))
        {
            end--;
        }

        for (var i = end; i >= 0 && !selected.Contains(i); i--)
        {
            tailChars += lines[i].Length + 1;
            selected.Add(i);
            if ((tailChars > tailBudget && i < end) || Render(lines, selected).Length > maxChars)
            {
                selected.Remove(i);
                break;
            }
        }

        var excerpt = Render(lines, selected);
        if (selected.Count == 0 || excerpt.Length > maxChars)
        {
            // Not even the last line fits: keep the end of the output.
            return Gap + trimmed[^(maxChars - Gap.Length)..];
        }

        return excerpt;
    }

    /// <summary>How strongly a line suggests a failure: 0 not at all, 1 weakly (a bare "error" or "failed"), 2 strongly.</summary>
    internal static int Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || PassLine().IsMatch(line))
        {
            return 0;
        }

        if (StrongMarker().IsMatch(line) || StrongMarkerIgnoreCase().IsMatch(line))
        {
            return 2;
        }

        return WeakMarker().IsMatch(line) ? 1 : 0;
    }

    private static void SelectFailureLines(string[] lines, SortedSet<int> selected, int budget)
    {
        var markers = new List<(int Index, int Strength)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var strength = Classify(lines[i]);
            if (strength > 0)
            {
                markers.Add((i, strength));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = 0;
        foreach (var (index, _) in markers.Where(m => m.Strength == 2).Concat(markers.Where(m => m.Strength == 1)))
        {
            // The same failure is often reported twice (as it happens and again in a summary): keep the first. A failure
            // line already shown as context of the previous one still adds its own context.
            if (!seen.Add(lines[index].Trim()))
            {
                continue;
            }

            if (++candidates > MaxCandidates || Render(lines, selected).Length >= budget - Gap.Length)
            {
                break;
            }

            var window = new List<int>();
            for (var j = Math.Max(0, index - ContextBefore); j <= Math.Min(lines.Length - 1, index + ContextAfter); j++)
            {
                if (selected.Add(j))
                {
                    window.Add(j);
                }
            }

            if (Render(lines, selected).Length <= budget)
            {
                continue;
            }

            // Too long with its context: try the failure line by itself.
            foreach (var j in window.Where(j => j != index))
            {
                selected.Remove(j);
            }

            if (window.Contains(index) && Render(lines, selected).Length > budget)
            {
                selected.Remove(index);
            }
        }
    }

    private static string Render(string[] lines, SortedSet<int> selected)
    {
        var sb = new StringBuilder();
        var previous = -1;
        foreach (var index in selected)
        {
            if (previous < 0)
            {
                if (index > 0)
                {
                    sb.Append(Gap).Append('\n');
                }
            }
            else
            {
                sb.Append('\n');
                if (index > previous + 1)
                {
                    sb.Append(Gap).Append('\n');
                }
            }

            sb.Append(lines[index]);
            previous = index;
        }

        if (previous >= 0 && previous < lines.Length - 1)
        {
            sb.Append('\n').Append(Gap);
        }

        return sb.ToString();
    }

    private static string Clip(string line, int max) => line.Length <= max ? line : line[..(max - 1)] + Gap;

    // Lines that report success, even when a test name mentions errors: "✓ handles Error: x", "PASS src/error.test.ts".
    [GeneratedRegex(@"^\s*(?:[✓✔√]|(?:PASS(?:ED)?|[Pp]assed|ok)(?![\w-])|\[PASS\])")]
    private static partial Regex PassLine();

    [GeneratedRegex(
        @"(?<![\w-])FAIL(?:ED|URES?|S)?(?![\w-])(?!\s*[:=]\s*0\b)" + // FAIL src/x.test.ts, --- FAIL: TestX, BUILD FAILED
        @"|(?<![\w-])ERRORS?(?![\w-])(?!\s*[:=]\s*0\b)" + // [ERROR] ..., ERROR in ./src
        @"|\b(?:[A-Z][\w.]*)?(?:Error|Exception)(?:\s*\[[^\]\n]*\])?:" + // TypeError: x, AssertionError [ERR_ASSERTION]: x
        @"|\b[A-Z][\w.]*(?:Error|Exception)\s*$" + // a bare exception name ending a traceback
        @"|\berror(?:\[[A-Za-z]*\d+\])?:" + // gcc/clang/rustc "error:", "error[E0308]:"
        @"|\berror [A-Z]{1,4}\d{1,5}\b" + // tsc/csc/msbuild "error TS2322", "error CS1002", "error MSB3073"
        @"|Traceback \(most recent call last\)" + // Python
        @"|\bpanicked at\b|^panic:" + // Rust, Go
        @"|^\s*npm (?:ERR!|error)\s" + // npm 6-9, npm 10+
        @"|^\s*[✗✘✖✕×❌]\s" + // failed-test markers (Vitest ×, Playwright ✘, Jest ✕)
        @"|^\s*Failed\b(?!\s*[:=]\s*0\b)" + // dotnet test "Failed Name [12 ms]", "Failed to compile."
        @"|(?<![\w-])FATAL(?![\w-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex StrongMarker();

    [GeneratedRegex(
        @"(?<![\w.,])[1-9]\d*\s+(?:failed|failing|failures?|errors?)\b" + // 1 failed, 3 failing, 2 errors
        @"|\b(?:failed|failures?|errors?)\s*[:=]\s*[1-9]" + // Failures: 1, failed=2
        @"|\bunhandled (?:exception|rejection|error)\b" +
        @"|\bfatal(?: error)?:" + // git "fatal:", MSVC "fatal error C1083:"
        @"|\bsegmentation fault\b|\bcore dumped\b" +
        @"|\btimed out\b|\btimeout of \d+\s*ms exceeded\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongMarkerIgnoreCase();

    // A bare "error", "failed", "failure" (not "0 errors", "no failures", "failed: 0"), or a pytest assertion line.
    [GeneratedRegex(
        @"(?<!\b(?:0|no|zero|without)\s+)\b(?:fail(?:ed|s|ing|ures?)?|errors?)\b(?!\s*(?:\(s\))?\s*[:=]\s*0\b)" +
        @"|^E\s{2,}\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeakMarker();
}

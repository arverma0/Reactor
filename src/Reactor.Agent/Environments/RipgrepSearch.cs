using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Reactor.Agent.Environments;

/// <summary>
/// Runs ripgrep with <c>--json</c> output and feeds its events into a <see cref="GrepCollector"/>, so results are formatted
/// exactly like the managed implementation. ripgrep is stopped as soon as the result limit is exceeded.
/// </summary>
internal static class RipgrepSearch
{
    /// <summary>
    /// Runs the search. Returns null when ripgrep rejected the pattern (its regex dialect differs from .NET's), so the caller
    /// can fall back to the managed implementation.
    /// </summary>
    public static async Task<string?> RunAsync(
        string ripgrepPath, string pattern, string fullPath, GrepOptions options, string workingDirectory, CancellationToken cancellationToken)
    {
        var mode = GrepModes.Parse(options.OutputMode);
        var collector = new GrepCollector(mode, options.MaxResults, options.ContextBefore, options.ContextAfter);
        var startInfo = new ProcessStartInfo(ripgrepPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = TextFileDecoding.Utf8,
            StandardErrorEncoding = TextFileDecoding.Utf8,
        };
        foreach (var argument in BuildArguments(pattern, fullPath, options, mode))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }

        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stoppedEarly = false;
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                ProcessJsonLine(line, collector, workingDirectory);
                if (collector.IsComplete)
                {
                    stoppedEarly = true;
                    break;
                }
            }
        }
        finally
        {
            if (stoppedEarly || cancellationToken.IsCancellationRequested)
            {
                Kill(process);
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        if (!stoppedEarly && process.ExitCode == 2 && !collector.HasResults)
        {
            if (LooksLikeRegexError(errors))
            {
                return null;
            }

            throw new InvalidOperationException("ripgrep failed: " + errors.Trim());
        }

        return collector.Build();
    }

    internal static IReadOnlyList<string> BuildArguments(string pattern, string fullPath, GrepOptions options, GrepOutputMode mode)
    {
        var args = new List<string>
        {
            "--json", "--hidden", "--crlf", "--sort", "path", "--max-filesize", "10M",
        };
        if (options.CaseInsensitive)
        {
            args.Add("--ignore-case");
        }

        if (!string.IsNullOrWhiteSpace(options.GlobFilter))
        {
            args.Add("--glob");
            args.Add(options.GlobFilter);
        }

        foreach (var directory in ManagedGrep.SkippedDirectoryNames)
        {
            args.Add("--glob");
            args.Add("!" + directory);
        }

        if (mode == GrepOutputMode.Content)
        {
            if (options.ContextBefore > 0)
            {
                args.Add("--before-context");
                args.Add(options.ContextBefore.ToString(CultureInfo.InvariantCulture));
            }

            if (options.ContextAfter > 0)
            {
                args.Add("--after-context");
                args.Add(options.ContextAfter.ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (mode == GrepOutputMode.FilesWithMatches)
        {
            args.Add("--max-count");
            args.Add("1");
        }

        args.Add("--regexp");
        args.Add(pattern);
        args.Add("--");
        args.Add(fullPath);
        return args;
    }

    /// <summary>Feeds one line of <c>rg --json</c> output into <paramref name="collector"/>.</summary>
    internal static void ProcessJsonLine(string line, GrepCollector collector, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement) || !root.TryGetProperty("data", out var data))
        {
            return;
        }

        var type = typeElement.GetString();
        switch (type)
        {
            case "match" or "context" when collector.Mode == GrepOutputMode.Content:
                var file = DisplayPath(data, workingDirectory);
                var lineNumber = data.TryGetProperty("line_number", out var number) && number.ValueKind == JsonValueKind.Number
                    ? number.GetInt32()
                    : 0;
                var text = GrepCollector.Clip(TrimLineEnding(TextOf(data, "lines")));
                if (type == "match")
                {
                    collector.AddMatch(file, lineNumber, text);
                }
                else
                {
                    collector.AddContext(file, lineNumber, text);
                }

                break;

            case "end" when collector.Mode != GrepOutputMode.Content:
                if (data.TryGetProperty("stats", out var stats) && stats.TryGetProperty("matched_lines", out var matched) &&
                    matched.ValueKind == JsonValueKind.Number && matched.GetInt64() > 0)
                {
                    var path = DisplayPath(data, workingDirectory);
                    if (collector.Mode == GrepOutputMode.FilesWithMatches)
                    {
                        collector.AddFile(path);
                    }
                    else
                    {
                        collector.AddCount(path, matched.GetInt64());
                    }
                }

                break;
        }
    }

    private static string DisplayPath(JsonElement data, string workingDirectory)
    {
        var path = TextOf(data, "path");
        try
        {
            return PathDisplay.ForDisplay(Path.GetFullPath(path, workingDirectory), workingDirectory);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string TextOf(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? "";
        }

        if (element.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.String)
        {
            try
            {
                return TextFileDecoding.Utf8.GetString(Convert.FromBase64String(bytes.GetString() ?? ""));
            }
            catch (FormatException)
            {
                return "";
            }
        }

        return "";
    }

    private static string TrimLineEnding(string text)
    {
        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private static bool LooksLikeRegexError(string stderr) =>
        stderr.Contains("regex parse error", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("error parsing regex", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("look-around", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("backreferences", StringComparison.OrdinalIgnoreCase);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Backends.Cli;

/// <summary>
/// Reads the GitHub Copilot CLI's on-disk session state (<c>&lt;COPILOT_HOME&gt;/session-state/&lt;session id&gt;/events.jsonl</c>),
/// whose <c>session.usage_record</c> events record every model call as it completes (tokens, the call's AI units, and its
/// premium-request cost). The CLI's JSON output reports usage only at the end of a run, so this is how a run that was
/// killed (timeout, cancel) is still accounted. Best effort: the format is the CLI's, and anything unreadable yields null.
/// </summary>
internal static class CopilotSessionState
{
    /// <summary>Usage summed over a session's records.</summary>
    /// <param name="Fields">Per-stage <c>backend.json</c> fields: <c>model_calls</c>, tokens, and, when every record carries it, <c>ai_units</c>, plus <c>premium_requests</c>.</param>
    public sealed record Usage(IReadOnlyDictionary<string, double> Fields);

    /// <summary>
    /// Sums the usage records of <paramref name="sessionId"/> written at or after <paramref name="since"/> (less a small
    /// clock allowance), or returns null when there are none or the state cannot be read.
    /// </summary>
    public static Usage? ReadUsage(string sessionStateRoot, string sessionId, DateTimeOffset since)
    {
        if (string.IsNullOrWhiteSpace(sessionStateRoot) || string.IsNullOrWhiteSpace(sessionId) || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var path = Path.Combine(sessionStateRoot, sessionId, "events.jsonl");
        if (!File.Exists(path))
        {
            return null;
        }

        var cutoff = since - TimeSpan.FromSeconds(2);
        double calls = 0, input = 0, output = 0, cacheRead = 0, cacheWrite = 0, reasoning = 0, nanoAiu = 0, premium = 0;
        var aiUnitsComplete = true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"session.usage_record\"", StringComparison.Ordinal))
                {
                    continue;
                }

                JsonObject? evt;
                try
                {
                    evt = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue; // A record cut off by the kill.
                }

                if (Str(evt, "type") != "session.usage_record" ||
                    !DateTimeOffset.TryParse(Str(evt, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ||
                    at < cutoff ||
                    (evt!["data"] as JsonObject)?["usage"] is not JsonObject usage)
                {
                    continue;
                }

                calls++;
                input += Number(usage["inputTokens"]);
                output += Number(usage["outputTokens"]);
                cacheRead += Number(usage["cacheReadTokens"]);
                cacheWrite += Number(usage["cacheWriteTokens"]);
                reasoning += Number(usage["reasoningTokens"]);
                if ((usage["copilotUsage"] as JsonObject)?["totalNanoAiu"] is JsonValue nano && TryNumber(nano, out var callNano))
                {
                    nanoAiu += callNano;
                }
                else
                {
                    aiUnitsComplete = false;
                }

                // Premium requests are charged per prompt: the user-initiated call carries the model's multiplier.
                if (string.Equals(Str(usage, "initiator"), "user", StringComparison.Ordinal))
                {
                    premium += Number(usage["cost"]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return null;
        }

        if (calls == 0)
        {
            return null;
        }

        var fields = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["model_calls"] = calls,
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["cache_read_tokens"] = cacheRead,
            ["cache_write_tokens"] = cacheWrite,
            ["reasoning_tokens"] = reasoning,
            ["premium_requests"] = premium,
        };
        if (aiUnitsComplete)
        {
            fields["ai_units"] = nanoAiu / 1e9;
        }

        return new Usage(fields);
    }

    private static double Number(JsonNode? node) => node is JsonValue value && TryNumber(value, out var number) ? number : 0;

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool TryNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue(out number))
        {
            return double.IsFinite(number);
        }

        if (value.TryGetValue<long>(out var l))
        {
            number = l;
            return true;
        }

        return false;
    }
}

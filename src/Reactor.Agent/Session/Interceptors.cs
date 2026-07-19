using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Reactor.Agent;

/// <summary>Information about a tool call, passed to interceptors.</summary>
public sealed record ToolCallInfo(string SessionId, string ToolCallId, string ToolName, JsonObject Arguments, string WorkingDirectory);

/// <summary>An interceptor's decision for a pending tool call.</summary>
public sealed record ToolInterceptResult(bool Allowed, string? Reason = null)
{
    public static ToolInterceptResult Allow { get; } = new(true);

    public static ToolInterceptResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Middleware around tool execution (between VALIDATE and EXECUTE): approval gates, auditing, pre/post hooks.
/// </summary>
public interface IToolInterceptor
{
    Task<ToolInterceptResult> BeforeAsync(ToolCallInfo call, CancellationToken cancellationToken) => Task.FromResult(ToolInterceptResult.Allow);

    Task AfterAsync(ToolCallInfo call, string output, bool isError, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Detects repeating tool-call patterns (spec 2.10).</summary>
public static class LoopDetector
{
    /// <summary>A stable signature for a tool call: name plus a hash of its canonical arguments.</summary>
    public static string Signature(string name, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(arguments)));
        return name + ":" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// True when the last <paramref name="window"/> signatures consist of a repeating pattern of length 1, 2, or 3.
    /// </summary>
    public static bool IsLooping(IReadOnlyList<string> signatures, int window)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        if (window <= 0 || signatures.Count < window)
        {
            return false;
        }

        var recent = signatures.Skip(signatures.Count - window).ToList();
        foreach (var patternLength in new[] { 1, 2, 3 })
        {
            if (window % patternLength != 0)
            {
                continue;
            }

            var matches = true;
            for (var i = patternLength; i < window && matches; i++)
            {
                matches = recent[i] == recent[i % patternLength];
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static string Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"\"{kv.Key}\":{Canonicalize(kv.Value)}")) + "}",
        JsonArray arr => "[" + string.Join(",", arr.Select(Canonicalize)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };
}

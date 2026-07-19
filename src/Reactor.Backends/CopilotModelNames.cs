using System.Text.RegularExpressions;

namespace Reactor.Backends;

/// <summary>Model naming and reasoning-effort conventions of GitHub Copilot (CLI and SDK).</summary>
public static partial class CopilotModelNames
{
    private static readonly HashSet<string> CliEfforts = new(StringComparer.OrdinalIgnoreCase) { "none", "minimal", "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    /// Copilot names Claude models with a dotted version (<c>claude-sonnet-4.5</c>); Anthropic-style dashed and dated ids
    /// (<c>claude-sonnet-4-5</c>, <c>claude-opus-4-6-20260101</c>) are converted. Other ids pass through; blank → null.
    /// </summary>
    public static string? Normalize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var match = ClaudeDashedVersion().Match(model.Trim());
        return match.Success ? $"{match.Groups["family"].Value}-{match.Groups["major"].Value}.{match.Groups["minor"].Value}" : model.Trim();
    }

    /// <summary>Reasoning effort accepted by <c>copilot --reasoning-effort</c> (unknown values → null).</summary>
    public static string? CliReasoningEffort(string? effort) =>
        effort is not null && CliEfforts.Contains(effort) ? effort.ToLowerInvariant() : null;

    /// <summary>Reasoning effort accepted by the SDK's session config (<c>low</c>…<c>max</c>; none/minimal → low).</summary>
    public static string? SdkReasoningEffort(string? effort) => effort?.ToLowerInvariant() switch
    {
        null => null,
        "none" or "minimal" or "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "xhigh",
        "max" => "max",
        _ => null,
    };

    [GeneratedRegex(@"^(?<family>claude-(?:opus|sonnet|haiku))-(?<major>\d+)-(?<minor>\d{1,2})(?:-\d{8})?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeDashedVersion();
}

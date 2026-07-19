namespace Reactor.Execution;

/// <summary>Context fidelity modes (spec 5.4).</summary>
public static class FidelityModes
{
    public const string Full = "full";
    public const string Truncate = "truncate";
    public const string Compact = "compact";
    public const string SummaryLow = "summary:low";
    public const string SummaryMedium = "summary:medium";
    public const string SummaryHigh = "summary:high";

    /// <summary>The runtime fallback when nothing is configured.</summary>
    public const string Default = Compact;

    public static IReadOnlyList<string> All { get; } = [Full, Truncate, Compact, SummaryLow, SummaryMedium, SummaryHigh];

    public static bool IsValid(string? mode) => mode is not null && All.Contains(mode, StringComparer.Ordinal);
}

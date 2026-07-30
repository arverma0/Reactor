using System.Globalization;
using System.Text.RegularExpressions;
using Reactor.Llm;

namespace Reactor.Backends;

/// <summary>
/// Detects tool calls that the model cut off at its output-token limit ("Tool call arguments truncated by the model
/// output token limit"): the whole generation of that call is lost, which is expensive and avoidable (the house rules
/// tell agents to write large files in parts).
/// </summary>
internal static class OutputLimit
{
    /// <summary><c>backend.json</c> field counting truncated tool calls in a stage.</summary>
    public const string MetadataKey = "output_limit_truncations";

    private const string Marker = "truncated by the model output token limit";

    public static bool IsTruncation(string? error) => error?.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The stage warning for a truncated call of <paramref name="tool"/>.</summary>
    public static string Warning(string tool) =>
        $"output-token limit: the {tool} call was longer than the model can generate in one response, so its arguments were " +
        "cut off and that generation was discarded (time and output tokens lost). Large files should be written in parts: " +
        "create the file with its first section, then append the rest.";
}

/// <summary>
/// Normalizes stage cost in <c>backend.json</c>: <c>cost_usd</c> (list price in USD), <c>cost_basis</c> (what it was
/// computed from: <c>ai_units</c>, <c>premium_requests</c>, or <c>tokens</c>), and <c>cost_estimated</c> (true when
/// Reactor derived the quantity itself, e.g. tokens × a price table, rather than taking a figure the runtime reported).
/// The raw fields (<c>ai_units</c>, <c>premium_requests</c>, tokens) are kept.
/// </summary>
internal static partial class StageCost
{
    public const string UsdKey = "cost_usd";
    public const string BasisKey = "cost_basis";
    public const string EstimatedKey = "cost_estimated";

    /// <summary>
    /// Copilot backends (SDK and CLI): AI units the runtime reported (× the AI credit price), else premium requests
    /// (× the premium request price), else <paramref name="tokenEstimateUsd"/> or tokens × the price table.
    /// </summary>
    public static void AddCopilot(IDictionary<string, object?> metadata, string? model, PriceTable? table = null, bool premiumRequestsEstimated = false, double? tokenEstimateUsd = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        table ??= PriceTable.Copilot;
        if (Number(metadata, "ai_units") is { } units)
        {
            Set(metadata, units * table.AiCreditUsd, "ai_units", estimated: false);
        }
        else if (Number(metadata, "premium_requests") is { } requests && table.PremiumRequestUsd is { } requestUsd)
        {
            Set(metadata, requests * requestUsd, "premium_requests", premiumRequestsEstimated);
        }
        else if ((tokenEstimateUsd ?? (Tokens(metadata, inputIncludesCache: true) is { } usage ? table.Find(model)?.EstimateUsd(usage) : null)) is { } usd)
        {
            Set(metadata, usd, "tokens", estimated: true);
        }
    }

    /// <summary>
    /// API-priced backends (agent, direct, Claude Code): a <c>cost_usd</c> the backend already reported is kept (basis
    /// <c>tokens</c>); otherwise tokens × the model catalog's prices, marked estimated.
    /// </summary>
    public static void AddApi(IDictionary<string, object?> metadata, string? model, bool inputIncludesCache)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (Number(metadata, UsdKey) is { } reported)
        {
            metadata[UsdKey] = Round(reported);
            metadata[BasisKey] = "tokens";
            if (!metadata.TryGetValue(EstimatedKey, out var estimated) || estimated is not bool)
            {
                metadata[EstimatedKey] = false;
            }

            return;
        }

        if (Tokens(metadata, inputIncludesCache) is { } usage && CatalogPrice(model)?.EstimateUsd(usage) is { } usd)
        {
            Set(metadata, usd, "tokens", estimated: true);
        }
    }

    /// <summary>The model catalog's prices for <paramref name="model"/> (dated Anthropic ids are matched without the date).</summary>
    public static ModelPrice? CatalogPrice(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return ModelPrice.FromCatalog(ModelCatalog.GetModelInfo(model))
               ?? ModelPrice.FromCatalog(ModelCatalog.GetModelInfo(DateSuffix().Replace(model, "")))
               ?? ModelPrice.FromCatalog(ModelCatalog.GetModelInfo(model.Replace('.', '-')));
    }

    /// <summary>The token usage recorded in <paramref name="metadata"/>, or null when no tokens were recorded.</summary>
    public static TokenUsage? Tokens(IDictionary<string, object?> metadata, bool inputIncludesCache)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var input = Number(metadata, "input_tokens");
        var output = Number(metadata, "output_tokens");
        if (input is null && output is null)
        {
            return null;
        }

        return new TokenUsage(
            (long)(input ?? 0),
            (long)(output ?? 0),
            (long)(Number(metadata, "cache_read_tokens") ?? 0),
            (long)(Number(metadata, "cache_write_tokens") ?? 0),
            inputIncludesCache);
    }

    /// <summary>A numeric metadata value (any CLR number or a numeric JSON value), or null.</summary>
    public static double? Number(IDictionary<string, object?> metadata, string key)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return metadata.TryGetValue(key, out var value) ? ToDouble(value) : null;
    }

    public static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        System.Text.Json.Nodes.JsonValue json when json.TryGetValue<double>(out var d) && double.IsFinite(d) => d,
        System.Text.Json.Nodes.JsonValue json when json.TryGetValue<long>(out var l) => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) => parsed,
        _ => null,
    };

    private static void Set(IDictionary<string, object?> metadata, double usd, string basis, bool estimated)
    {
        metadata[UsdKey] = Round(usd);
        metadata[BasisKey] = basis;
        metadata[EstimatedKey] = estimated;
    }

    private static double Round(double usd) => Math.Round(usd, 6);

    [GeneratedRegex(@"-\d{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();
}

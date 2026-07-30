using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reactor.Llm;

/// <summary>Token prices in USD per 1 million tokens.</summary>
public sealed record TokenRates
{
    /// <summary>Uncached input tokens.</summary>
    [JsonPropertyName("input")]
    public double? Input { get; init; }

    /// <summary>Input tokens read from the prompt cache (null: billed as input).</summary>
    [JsonPropertyName("cached_input")]
    public double? CachedInput { get; init; }

    /// <summary>Input tokens written to the prompt cache (null: the model has no cache-write price; billed as input).</summary>
    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; init; }

    [JsonPropertyName("output")]
    public double? Output { get; init; }

    /// <summary>True when both the input and output prices are known.</summary>
    [JsonIgnore]
    public bool IsComplete => Input is not null && Output is not null;
}

/// <summary>A long-context price tier, applied when one request's input exceeds <see cref="AboveInputTokens"/>.</summary>
public sealed record LongContextRates
{
    [JsonPropertyName("above_input_tokens")]
    public long AboveInputTokens { get; init; }

    [JsonPropertyName("input")]
    public double? Input { get; init; }

    [JsonPropertyName("cached_input")]
    public double? CachedInput { get; init; }

    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; init; }

    [JsonPropertyName("output")]
    public double? Output { get; init; }

    [JsonIgnore]
    public TokenRates Rates => new() { Input = Input, CachedInput = CachedInput, CacheWrite = CacheWrite, Output = Output };
}

/// <summary>
/// Token usage to price. <see cref="Input"/> counts all input tokens, including cache reads and writes, when
/// <see cref="InputIncludesCache"/> is true (OpenAI and Copilot report it that way); Anthropic's API reports cache
/// reads and writes separately from (not included in) its input tokens.
/// </summary>
public readonly record struct TokenUsage(long Input, long Output, long CacheRead = 0, long CacheWrite = 0, bool InputIncludesCache = true)
{
    /// <summary>Input tokens billed at the uncached input rate.</summary>
    public long UncachedInput => InputIncludesCache ? Math.Max(0, Input - CacheRead - CacheWrite) : Input;

    /// <summary>All input tokens of the request(s), cached or not.</summary>
    public long TotalInput => InputIncludesCache ? Input : Input + CacheRead + CacheWrite;
}

/// <summary>A model's prices: token rates (with an optional long-context tier) and a legacy premium-request multiplier.</summary>
public sealed record ModelPrice
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("input")]
    public double? Input { get; init; }

    [JsonPropertyName("cached_input")]
    public double? CachedInput { get; init; }

    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; init; }

    [JsonPropertyName("output")]
    public double? Output { get; init; }

    [JsonPropertyName("long_context")]
    public LongContextRates? LongContext { get; init; }

    /// <summary>Premium requests per prompt under the legacy request-based billing model (null: unknown).</summary>
    [JsonPropertyName("legacy_premium_multiplier")]
    public double? LegacyPremiumMultiplier { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    /// <summary>The default-tier token rates.</summary>
    [JsonIgnore]
    public TokenRates Rates => new() { Input = Input, CachedInput = CachedInput, CacheWrite = CacheWrite, Output = Output };

    /// <summary>The rates for a request with <paramref name="requestInputTokens"/> input tokens (null: the default tier).</summary>
    public TokenRates RatesFor(long? requestInputTokens) =>
        LongContext is { } tier && requestInputTokens is { } tokens && tokens > tier.AboveInputTokens && tier.Rates.IsComplete ? tier.Rates : Rates;

    /// <summary>
    /// The list price of <paramref name="usage"/> in USD, or null when the input or output rate is unknown. Pass
    /// <paramref name="requestInputTokens"/> for a single request so the long-context tier applies when it should; usage
    /// summed over many requests is priced at the default tier.
    /// </summary>
    public double? EstimateUsd(TokenUsage usage, long? requestInputTokens = null)
    {
        var rates = RatesFor(requestInputTokens);
        if (!rates.IsComplete)
        {
            return null;
        }

        var input = rates.Input!.Value;
        var dollars = (usage.UncachedInput * input)
                      + (usage.CacheRead * (rates.CachedInput ?? input))
                      + (usage.CacheWrite * (rates.CacheWrite ?? input))
                      + (usage.Output * rates.Output!.Value);
        return dollars / 1_000_000d;
    }

    /// <summary>Prices from a model catalog entry (input/output only; cache tokens fall back to the input rate).</summary>
    public static ModelPrice? FromCatalog(ModelInfo? info) =>
        info is { InputCostPerMillion: { } input, OutputCostPerMillion: { } output }
            ? new ModelPrice
            {
                Id = info.Id,
                DisplayName = info.DisplayName,
                Input = input,
                Output = output,
                CachedInput = info.CachedInputCostPerMillion,
                CacheWrite = info.CacheWriteCostPerMillion,
            }
            : null;

    /// <summary>Compact rate text, e.g. <c>$4/$20</c> (input/output per 1M tokens), or null when unknown.</summary>
    public string? RateText() =>
        Input is { } input && Output is { } output ? $"${FormatRate(input)}/${FormatRate(output)}" : null;

    private static string FormatRate(double value) => value.ToString(value >= 1 ? "0.##" : "0.00#", CultureInfo.InvariantCulture);
}

/// <summary>Where the price table's numbers come from.</summary>
public sealed record PriceSource
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("retrieved")]
    public string? Retrieved { get; init; }

    [JsonPropertyName("covers")]
    public string? Covers { get; init; }
}

/// <summary>
/// The GitHub Copilot price table: per-model token prices (usage-based billing, in AI credits) and legacy
/// premium-request multipliers, used when a Copilot runtime reports no billing data and to turn AI units, premium
/// requests, and tokens into one comparable <c>cost_usd</c>. Ships as an embedded JSON file (with its sources);
/// <c>REACTOR_PRICES=&lt;path&gt;</c> adds or replaces entries (same shape; entries with a built-in id replace it, and
/// <c>"replace": true</c> drops the built-in entries). An unreadable override is reported in <see cref="LoadError"/>
/// and the built-in table is used.
/// </summary>
public sealed class PriceTable
{
    /// <summary>Environment variable naming a price file that extends or overrides the built-in table.</summary>
    public const string OverrideVariable = "REACTOR_PRICES";

    private static readonly object Gate = new();
    private static PriceTable? _copilot;
    private readonly Dictionary<string, ModelPrice> _byKey;

    public PriceTable(IEnumerable<ModelPrice> models, double aiCreditUsd = 0.01, double? premiumRequestUsd = null, IReadOnlyList<PriceSource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        Models = models.ToList();
        AiCreditUsd = aiCreditUsd;
        PremiumRequestUsd = premiumRequestUsd;
        Sources = sources ?? [];
        _byKey = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in Models)
        {
            _byKey[model.Id] = model;
        }

        foreach (var model in Models)
        {
            if (model.DisplayName is { Length: > 0 } name)
            {
                _byKey.TryAdd(Key(name), model);
            }
        }
    }

    /// <summary>The built-in Copilot table merged with <c>REACTOR_PRICES</c> (loaded once).</summary>
    public static PriceTable Copilot
    {
        get
        {
            lock (Gate)
            {
                return _copilot ??= LoadCopilot(Environment.GetEnvironmentVariable(OverrideVariable));
            }
        }
    }

    /// <summary>Why <c>REACTOR_PRICES</c> could not be applied to <see cref="Copilot"/>, if it could not.</summary>
    public string? LoadError { get; private init; }

    public IReadOnlyList<ModelPrice> Models { get; }

    /// <summary>USD per AI credit (one AI unit; 1e9 nano AI units). GitHub: $0.01.</summary>
    public double AiCreditUsd { get; }

    /// <summary>USD per premium request (legacy request-based billing), when known.</summary>
    public double? PremiumRequestUsd { get; }

    public IReadOnlyList<PriceSource> Sources { get; }

    /// <summary>Replaces the Copilot table (e.g. in tests; null reloads it on next use).</summary>
    public static void SetCopilot(PriceTable? table)
    {
        lock (Gate)
        {
            _copilot = table;
        }
    }

    /// <summary>The prices of <paramref name="model"/> by id or display name (case-insensitive), or null.</summary>
    public ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return _byKey.TryGetValue(model.Trim(), out var price) || _byKey.TryGetValue(Key(model), out price) ? price : null;
    }

    /// <summary>The built-in table merged with the file at <paramref name="overridePath"/> (when set).</summary>
    public static PriceTable LoadCopilot(string? overridePath)
    {
        var builtIn = LoadBuiltIn();
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            return builtIn;
        }

        try
        {
            using var stream = File.OpenRead(overridePath);
            return Merge(builtIn, Parse(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return new PriceTable(builtIn.Models, builtIn.AiCreditUsd, builtIn.PremiumRequestUsd, builtIn.Sources)
            {
                LoadError = $"{OverrideVariable}={overridePath} was not applied: {ex.Message}",
            };
        }
    }

    /// <summary>Parses a price file.</summary>
    public static PriceFile Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return JsonSerializer.Deserialize<PriceFile>(stream, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
               ?? throw new InvalidOperationException("The price file is empty.");
    }

    /// <summary>Applies <paramref name="file"/> to <paramref name="baseTable"/>: its entries win by id.</summary>
    public static PriceTable Merge(PriceTable baseTable, PriceFile file)
    {
        ArgumentNullException.ThrowIfNull(baseTable);
        ArgumentNullException.ThrowIfNull(file);
        var models = file.Replace ? new List<ModelPrice>() : baseTable.Models.ToList();
        foreach (var entry in file.Models ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                continue;
            }

            models.RemoveAll(m => string.Equals(m.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            models.Add(entry);
        }

        var sources = baseTable.Sources.Concat(file.Sources ?? []).ToList();
        return new PriceTable(models, file.AiCreditUsd ?? baseTable.AiCreditUsd, file.PremiumRequestUsd ?? baseTable.PremiumRequestUsd, sources);
    }

    private static PriceTable LoadBuiltIn()
    {
        using var stream = typeof(PriceTable).Assembly.GetManifestResourceStream("Reactor.Llm.copilot-prices.json")
                           ?? throw new InvalidOperationException("Embedded price table 'Reactor.Llm.copilot-prices.json' is missing.");
        var file = Parse(stream);
        return new PriceTable(file.Models ?? [], file.AiCreditUsd ?? 0.01, file.PremiumRequestUsd, file.Sources);
    }

    private static string Key(string name) => string.Join('-', name.Trim().ToLowerInvariant().Split([' ', '_'], StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>The JSON shape of a price file.</summary>
public sealed record PriceFile
{
    [JsonPropertyName("sources")]
    public IReadOnlyList<PriceSource>? Sources { get; init; }

    [JsonPropertyName("ai_credit_usd")]
    public double? AiCreditUsd { get; init; }

    [JsonPropertyName("premium_request_usd")]
    public double? PremiumRequestUsd { get; init; }

    /// <summary>True: the file's entries replace the built-in table instead of extending it.</summary>
    [JsonPropertyName("replace")]
    public bool Replace { get; init; }

    [JsonPropertyName("models")]
    public IReadOnlyList<ModelPrice>? Models { get; init; }
}

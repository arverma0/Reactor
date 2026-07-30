using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reactor.Llm;

/// <summary>Catalog entry describing a known model.</summary>
public sealed record ModelInfo
{
    /// <summary>The model's API identifier, e.g. <c>claude-opus-4-6</c>.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = "";

    /// <summary>Maximum total tokens (input + output).</summary>
    [JsonPropertyName("context_window")]
    public int ContextWindow { get; init; }

    [JsonPropertyName("max_output")]
    public int? MaxOutput { get; init; }

    [JsonPropertyName("supports_tools")]
    public bool SupportsTools { get; init; }

    [JsonPropertyName("supports_vision")]
    public bool SupportsVision { get; init; }

    [JsonPropertyName("supports_reasoning")]
    public bool SupportsReasoning { get; init; }

    [JsonPropertyName("input_cost_per_million")]
    public double? InputCostPerMillion { get; init; }

    [JsonPropertyName("output_cost_per_million")]
    public double? OutputCostPerMillion { get; init; }

    /// <summary>USD per million input tokens read from the prompt cache (null: priced at the input rate).</summary>
    [JsonPropertyName("cached_input_cost_per_million")]
    public double? CachedInputCostPerMillion { get; init; }

    /// <summary>USD per million input tokens written to the prompt cache (null: priced at the input rate).</summary>
    [JsonPropertyName("cache_write_cost_per_million")]
    public double? CacheWriteCostPerMillion { get; init; }

    [JsonPropertyName("knowledge_cutoff")]
    public string? KnowledgeCutoff { get; init; }

    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>
/// Advisory catalog of known models, shipped as an embedded JSON data file. Unknown model ids are still passed through
/// to providers. Set <c>REACTOR_MODEL_CATALOG</c> to a JSON file path (same shape) to replace the built-in data.
/// </summary>
public static class ModelCatalog
{
    private static readonly object Gate = new();
    private static IReadOnlyList<ModelInfo>? _models;

    /// <summary>All known models, newest first within each provider.</summary>
    public static IReadOnlyList<ModelInfo> Models
    {
        get
        {
            lock (Gate)
            {
                return _models ??= LoadDefault();
            }
        }
    }

    /// <summary>Replaces the catalog contents (e.g. with data loaded from an updated file).</summary>
    public static void SetModels(IEnumerable<ModelInfo> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        lock (Gate)
        {
            _models = models.ToList();
        }
    }

    /// <summary>Loads a catalog file (<c>{"models": [...]}</c> or a bare array).</summary>
    public static IReadOnlyList<ModelInfo> LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    /// <summary>Returns the catalog entry for an id or alias (case-insensitive), or null.</summary>
    public static ModelInfo? GetModelInfo(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var models = Models;
        return models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(m => m.Aliases.Any(a => string.Equals(a, modelId, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>All known models, optionally filtered by provider.</summary>
    public static IReadOnlyList<ModelInfo> ListModels(string? provider = null) =>
        provider is null
            ? Models
            : Models.Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The newest/best model for <paramref name="provider"/>, optionally requiring a capability:
    /// <c>reasoning</c>, <c>vision</c>, or <c>tools</c>.
    /// </summary>
    public static ModelInfo? GetLatestModel(string provider, string? capability = null) =>
        ListModels(provider).FirstOrDefault(m => capability?.ToLowerInvariant() switch
        {
            null or "" => true,
            "reasoning" => m.SupportsReasoning,
            "vision" => m.SupportsVision,
            "tools" => m.SupportsTools,
            _ => false,
        });

    /// <summary>
    /// Infers the provider for a model id: catalog lookup first, then well-known prefixes
    /// (<c>gpt-</c>/<c>o1</c>/<c>o3</c>/<c>o4</c>/<c>codex</c> → openai, <c>claude</c> → anthropic, <c>gemini</c> → gemini).
    /// </summary>
    public static string? InferProvider(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        if (GetModelInfo(modelId) is { } info)
        {
            return info.Provider;
        }

        var id = modelId.ToLowerInvariant();
        if (id.StartsWith("claude", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (id.StartsWith("gemini", StringComparison.Ordinal))
        {
            return "gemini";
        }

        if (id.StartsWith("gpt-", StringComparison.Ordinal) || id.StartsWith("o1", StringComparison.Ordinal) ||
            id.StartsWith("o3", StringComparison.Ordinal) || id.StartsWith("o4", StringComparison.Ordinal) ||
            id.Contains("codex", StringComparison.Ordinal) || id.StartsWith("chatgpt", StringComparison.Ordinal))
        {
            return "openai";
        }

        return null;
    }

    private static IReadOnlyList<ModelInfo> LoadDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("REACTOR_MODEL_CATALOG");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return LoadFile(overridePath);
        }

        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("Reactor.Llm.models.json")
                           ?? throw new InvalidOperationException("Embedded model catalog 'Reactor.Llm.models.json' is missing.");
        return Parse(stream);
    }

    private static IReadOnlyList<ModelInfo> Parse(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var array = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("models");
        return array.Deserialize<List<ModelInfo>>() ?? [];
    }
}

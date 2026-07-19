using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>The single input type for both <see cref="Client.CompleteAsync"/> and <see cref="Client.StreamAsync"/>.</summary>
public sealed record Request
{
    /// <summary>The provider's native model id, e.g. <c>claude-opus-4-6</c> or <c>gpt-5.2</c>.</summary>
    public required string Model { get; init; }

    /// <summary>The conversation.</summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>Provider key to route to; the client's default provider is used when null.</summary>
    public string? Provider { get; init; }

    public IReadOnlyList<ToolDefinition>? Tools { get; init; }

    /// <summary>Defaults to <c>auto</c> when tools are present.</summary>
    public ToolChoice? ToolChoice { get; init; }

    public ResponseFormat? ResponseFormat { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? MaxTokens { get; init; }

    public IReadOnlyList<string>? StopSequences { get; init; }

    /// <summary><c>low</c>, <c>medium</c>, <c>high</c> (some providers accept more); null means provider default.</summary>
    public string? ReasoningEffort { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Escape hatch for provider-specific parameters, keyed by provider name:
    /// <c>{"anthropic": {"beta_headers": [...], "thinking": {...}}}</c>. Adapters read only their own key.
    /// </summary>
    public JsonObject? ProviderOptions { get; init; }

    /// <summary>Returns the options object for <paramref name="provider"/>, or null.</summary>
    public JsonObject? GetProviderOptions(string provider) => ProviderOptions?[provider] as JsonObject;

    /// <summary>The effective tool choice: explicit, or <c>auto</c> when tools are present.</summary>
    public ToolChoice? EffectiveToolChoice => ToolChoice ?? (Tools is { Count: > 0 } ? global::Reactor.Llm.ToolChoice.Auto : null);
}

/// <summary>Requested output format: plain text, any JSON, or JSON conforming to a schema.</summary>
public sealed record ResponseFormat
{
    public const string TextType = "text";
    public const string JsonType = "json";
    public const string JsonSchemaType = "json_schema";

    /// <summary><c>text</c>, <c>json</c>, or <c>json_schema</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Required when <see cref="Type"/> is <c>json_schema</c>.</summary>
    public JsonObject? JsonSchema { get; init; }

    /// <summary>When true the provider enforces the schema strictly.</summary>
    public bool Strict { get; init; }

    /// <summary>Schema name for providers that require one (OpenAI). Defaults to <c>response</c>.</summary>
    public string SchemaName { get; init; } = "response";

    public static ResponseFormat Text { get; } = new() { Type = TextType };

    public static ResponseFormat Json { get; } = new() { Type = JsonType };

    public static ResponseFormat ForSchema(JsonObject schema, bool strict = false, string name = "response") =>
        new() { Type = JsonSchemaType, JsonSchema = schema, Strict = strict, SchemaName = name };
}

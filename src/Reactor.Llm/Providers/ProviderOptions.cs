namespace Reactor.Llm.Providers;

/// <summary>Options for <see cref="OpenAIAdapter"/> (Responses API).</summary>
public sealed record OpenAIAdapterOptions : ProviderAdapterOptions
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    /// <summary>Sent as the <c>OpenAI-Organization</c> header.</summary>
    public string? Organization { get; init; }

    /// <summary>Sent as the <c>OpenAI-Project</c> header.</summary>
    public string? Project { get; init; }
}

/// <summary>Options for <see cref="AnthropicAdapter"/> (Messages API).</summary>
public sealed record AnthropicAdapterOptions : ProviderAdapterOptions
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>Sent as the <c>anthropic-version</c> header.</summary>
    public string ApiVersion { get; init; } = "2023-06-01";

    /// <summary>
    /// When true (default), <c>cache_control</c> breakpoints are injected automatically. Per-request override:
    /// <c>provider_options.anthropic.auto_cache = false</c>.
    /// </summary>
    public bool AutoCache { get; init; } = true;

    /// <summary>Anthropic requires <c>max_tokens</c>; this is used when the request does not set one.</summary>
    public int DefaultMaxTokens { get; init; } = 4096;
}

/// <summary>Options for <see cref="GeminiAdapter"/> (native Gemini API).</summary>
public sealed record GeminiAdapterOptions : ProviderAdapterOptions
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    /// <summary>API version path segment.</summary>
    public string ApiVersion { get; init; } = "v1beta";
}

/// <summary>Options for <see cref="OpenAICompatibleAdapter"/> (Chat Completions for third-party endpoints).</summary>
public sealed record OpenAICompatibleAdapterOptions : ProviderAdapterOptions
{
    /// <summary>The provider key this adapter registers under.</summary>
    public string Name { get; init; } = "openai-compatible";
}
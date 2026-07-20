using Reactor.Llm;

namespace Reactor.Backends;

/// <summary>Resolves which provider and model a stage uses for the native (Unified LLM) backends.</summary>
public static class ModelResolution
{
    /// <summary>
    /// Provider: node <c>llm_provider</c> → provider inferred from the model id → <paramref name="defaultProvider"/> →
    /// the client's default. Model: node <c>llm_model</c> → <paramref name="defaultModel"/> (when compatible with the
    /// provider) → the catalog's latest tool-capable model for the provider.
    /// </summary>
    /// <exception cref="ConfigurationError">No provider or model can be determined.</exception>
    public static (string Provider, string Model) Resolve(StageSettings stage, Client client, string? defaultProvider, string? defaultModel)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(client);
        var provider = stage.Provider;
        var model = stage.Model;
        if (model is null && provider is null)
        {
            model = defaultModel;
        }

        provider ??= ModelCatalog.InferProvider(model) ?? defaultProvider ?? client.DefaultProvider
            ?? throw new ConfigurationError($"Node '{stage.NodeId}': no llm_provider is set and no default provider is configured.");

        if (model is null)
        {
            var inferred = defaultModel is null ? null : ModelCatalog.InferProvider(defaultModel);
            model = defaultModel is not null && (inferred is null || inferred == provider)
                ? defaultModel
                : ModelCatalog.GetLatestModel(provider, "tools")?.Id;
        }

        return model is null
            ? throw new ConfigurationError($"Node '{stage.NodeId}': no llm_model is set and no default model is known for provider '{provider}'.")
            : (provider, model);
    }

    /// <summary>
    /// The reasoning effort to request: none for models the catalog marks as non-reasoning; otherwise the node's explicit
    /// value (attribute or stylesheet); otherwise the spec default (<c>high</c>) for models known to reason, and nothing for
    /// unknown models, since OpenAI-compatible servers commonly reject the parameter.
    /// </summary>
    public static string? ReasoningEffort(StageSettings stage, string model)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var info = ModelCatalog.GetModelInfo(model);
        if (info is { SupportsReasoning: false })
        {
            return null;
        }

        return stage.ExplicitReasoningEffort ?? (info is { SupportsReasoning: true } ? stage.ReasoningEffort : null);
    }
}

using Reactor.Copilot;
using Reactor.Llm;
using Reactor.Server;

namespace Reactor.Cli;

/// <summary>Builds the model list the Studio offers in its model pickers: Copilot account models and the LLM catalog.</summary>
internal static class StudioModels
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    public static Func<CancellationToken, Task<IReadOnlyList<ModelDescriptor>>> Lister(BackendSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var gate = new SemaphoreSlim(1, 1);
        IReadOnlyList<ModelDescriptor>? cached = null;
        var expires = DateTimeOffset.MinValue;
        return async cancellationToken =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cached is not null && DateTimeOffset.UtcNow < expires)
                {
                    return cached;
                }

                var models = new List<ModelDescriptor>();
                var copilotListed = false;
                if (selection.Copilot is { } copilot && selection.Routes.Contains("copilot"))
                {
                    try
                    {
                        foreach (var model in await copilot.ListModelsAsync(cancellationToken).ConfigureAwait(false))
                        {
                            models.Add(new ModelDescriptor
                            {
                                Id = model.Id,
                                Provider = "copilot",
                                Source = "copilot",
                                DisplayName = model.Name,
                                ContextWindow = model.ContextWindow > 0 ? model.ContextWindow : null,
                                ReasoningEfforts = model.ReasoningEfforts,
                                BillingMultiplier = model.BillingMultiplier,
                                PolicyState = model.PolicyState,
                                Available = model.Enabled,
                            });
                        }

                        copilotListed = true;
                    }
                    catch (Exception ex) when (ex is CopilotStageException or IOException or InvalidOperationException or TimeoutException)
                    {
                        // Not signed in or no runtime: the catalog still lists the native providers' models.
                    }
                }

                var providers = selection.Client?.ProviderNames ?? [];
                foreach (var model in ModelCatalog.ListModels())
                {
                    models.Add(new ModelDescriptor
                    {
                        Id = model.Id,
                        Provider = model.Provider,
                        Source = "catalog",
                        DisplayName = model.DisplayName.Length > 0 ? model.DisplayName : null,
                        ContextWindow = model.ContextWindow > 0 ? model.ContextWindow : null,
                        SupportsTools = model.SupportsTools,
                        SupportsReasoning = model.SupportsReasoning,
                        ReasoningEfforts = model.SupportsReasoning ? ["low", "medium", "high"] : [],
                        Available = providers.Contains(model.Provider, StringComparer.Ordinal),
                    });
                }

                cached = models;
                expires = DateTimeOffset.UtcNow + (copilotListed || selection.Copilot is null ? CacheFor : TimeSpan.FromSeconds(30));
                return cached;
            }
            finally
            {
                gate.Release();
            }
        };
    }
}

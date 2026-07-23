using System.Runtime.InteropServices;
using Reactor.Agent;
using Reactor.Backends;
using Reactor.Backends.Cli;
using Reactor.Copilot;
using Reactor.Handlers;
using Reactor.Llm;

namespace Reactor.Cli;

/// <summary>Backend choices from the command line.</summary>
internal sealed record BackendSettings
{
    public string Kind { get; init; } = "auto";

    public string? Model { get; init; }

    public string? Provider { get; init; }

    /// <summary>Output token limit per LLM response for the native backends (null: the provider's default).</summary>
    public int? MaxTokens { get; init; }

    public string? CopilotRuntime { get; init; }

    public string? CopilotCli { get; init; }

    public string? ClaudeCli { get; init; }
}

/// <summary>The composed backend for a run or server, plus what was chosen and why.</summary>
internal sealed class BackendSelection : IAsyncDisposable
{
    /// <summary>The backend (a router), or null for simulation mode.</summary>
    public ICodergenBackend? Backend { get; init; }

    public required string Kind { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Routes { get; init; } = [];

    public Client? Client { get; init; }

    /// <summary>The Copilot SDK backend when one was configured (used to list the account's models).</summary>
    public CopilotSdkBackend? Copilot { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (Backend is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        if (Client is not null)
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Composes codergen backends from the environment and command-line settings.</summary>
internal static class BackendFactory
{
    public static readonly string[] Kinds = ["auto", "simulate", "agent", "direct", "copilot", "copilot-cli", "claude-code"];

    /// <summary>Timeouts for agent workloads: long reasoning phases can be silent for minutes.</summary>
    public static readonly AdapterTimeout AgentTimeouts = new()
    {
        Connect = TimeSpan.FromSeconds(30),
        Request = TimeSpan.FromMinutes(15),
        StreamRead = TimeSpan.FromMinutes(5),
    };

    /// <exception cref="ConfigurationError">The requested backend is not available.</exception>
    public static BackendSelection Create(BackendSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var kind = settings.Kind.ToLowerInvariant();
        if (kind == "simulate")
        {
            return new BackendSelection { Kind = "simulate", Description = "simulation (no LLM calls)" };
        }

        var client = Client.FromEnv(defaultProvider: NativeProvider(settings.Provider), timeout: AgentTimeouts);
        var hasNative = client.ProviderNames.Count > 0;
        var agent = hasNative
            ? new AgentLoopBackend(new AgentLoopBackendOptions
            {
                Client = client,
                DefaultProvider = settings.Provider,
                DefaultModel = settings.Model,
                SessionConfig = new SessionConfig { MaxTokens = settings.MaxTokens },
            })
            : null;
        var direct = hasNative
            ? new DirectLlmBackend(new DirectLlmBackendOptions
            {
                Client = client,
                DefaultProvider = settings.Provider,
                DefaultModel = settings.Model,
                MaxTokens = settings.MaxTokens,
            })
            : null;
        var copilotRuntime = settings.CopilotRuntime ?? FindCopilotRuntime();
        var copilot = new CopilotSdkBackend(new CopilotSdkBackendOptions { DefaultModel = settings.Model, RuntimePath = settings.CopilotRuntime });
        var copilotCli = new CopilotCliBackend(new CopilotCliOptions { Executable = settings.CopilotCli, DefaultModel = settings.Model });
        var claude = new ClaudeCodeBackend(new ClaudeCodeOptions { Executable = settings.ClaudeCli, DefaultModel = settings.Model });
        var copilotCliFound = TryResolve(copilotCli);
        var claudeFound = TryResolve(claude);

        if (kind == "auto")
        {
            kind = hasNative ? "agent"
                : copilotRuntime is not null ? "copilot"
                : copilotCliFound ? "copilot-cli"
                : claudeFound ? "claude-code"
                : "simulate";
            if (kind == "simulate")
            {
                return new BackendSelection
                {
                    Kind = "simulate",
                    Description = "simulation — no backend found (set an LLM API key, or install the GitHub Copilot or Claude Code CLI)",
                    Client = client,
                };
            }
        }

        ICodergenBackend primary = kind switch
        {
            "agent" => agent ?? throw new ConfigurationError(NoKeys("agent")),
            "direct" => direct ?? throw new ConfigurationError(NoKeys("direct")),
            "copilot" => copilot,
            "copilot-cli" => copilotCliFound ? copilotCli : throw new ConfigurationError(Describe(copilotCli)),
            "claude-code" => claudeFound ? claude : throw new ConfigurationError(Describe(claude)),
            _ => throw new ConfigurationError($"Unknown backend '{settings.Kind}'. Choose one of: {string.Join(", ", Kinds)}."),
        };

        var router = new BackendRouter(primary, kind);
        var routes = new List<string> { kind };
        void Add(string name, ICodergenBackend? backend)
        {
            if (backend is not null && !router.Routes.ContainsKey(name))
            {
                router.Add(name, backend);
                routes.Add(name);
            }
        }

        Add("agent", agent);
        Add("direct", direct);
        foreach (var provider in client.ProviderNames)
        {
            Add(provider, agent);
        }

        Add("copilot", copilot);
        Add("copilot-sdk", copilot);
        Add("copilot-cli", copilotCliFound ? copilotCli : null);
        Add("claude-code", claudeFound ? claude : null);
        Add("claude", claudeFound ? claude : null);

        var description = kind switch
        {
            "agent" => $"coding agent loop ({string.Join(", ", client.ProviderNames)})",
            "direct" => $"direct LLM calls ({string.Join(", ", client.ProviderNames)})",
            "copilot" => "GitHub Copilot SDK" + (copilotRuntime is null ? "" : $" (runtime: {copilotRuntime})"),
            "copilot-cli" => $"GitHub Copilot CLI ({copilotCli.ResolveCommand()})",
            _ => $"Claude Code ({claude.ResolveCommand()})",
        };
        if (settings.Model is not null)
        {
            description += $", default model {settings.Model}";
        }

        return new BackendSelection { Backend = router, Kind = kind, Description = description, Routes = routes, Client = client, Copilot = copilot };
    }

    /// <summary>The Copilot runtime bundled next to the CLI (or <c>COPILOT_CLI_PATH</c>), if any.</summary>
    public static string? FindCopilotRuntime()
    {
        if (Environment.GetEnvironmentVariable("COPILOT_CLI_PATH") is { Length: > 0 } explicitPath && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        foreach (var name in new[] { "copilot.exe", "copilot" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? NativeProvider(string? provider) =>
        provider?.ToLowerInvariant() is "openai" or "anthropic" or "gemini" or "openai-compatible" ? provider.ToLowerInvariant() : null;

    private static bool TryResolve(CliAgentBackend backend)
    {
        try
        {
            backend.ResolveCommand();
            return true;
        }
        catch (CliAgentException)
        {
            return false;
        }
    }

    private static string Describe(CliAgentBackend backend)
    {
        try
        {
            backend.ResolveCommand();
            return $"{backend.Name} is available.";
        }
        catch (CliAgentException ex)
        {
            return ex.Message;
        }
    }

    private static string NoKeys(string kind) =>
        $"The '{kind}' backend needs an LLM API key: set OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY (or GOOGLE_API_KEY), " +
        "or OPENAI_COMPATIBLE_BASE_URL.";
}

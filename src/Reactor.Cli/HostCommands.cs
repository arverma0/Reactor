using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Reactor.Agent;
using Reactor.Agent.Environments;
using Reactor.Backends;
using Reactor.Backends.Cli;
using Reactor.Llm;
using Reactor.Server;
using Reactor.Server.Studio;

namespace Reactor.Cli;

internal static class HostCommands
{
    public static Command Serve() => CreateServer("serve", "Run the HTTP server, the Studio web app, and the console", openBrowser: false);

    public static Command Studio() => CreateServer("studio", "Open Reactor Studio: design, simulate, run, and review pipelines in the browser", openBrowser: true);

    private static Command CreateServer(string name, string description, bool openBrowser)
    {
        var urls = new Option<string>("--urls") { Description = "Listen address(es)", DefaultValueFactory = _ => Environment.GetEnvironmentVariable("REACTOR_URLS") ?? "http://127.0.0.1:5180" };
        var token = new Option<string?>("--token") { Description = "Require this bearer token for API calls (default: $REACTOR_SERVER_TOKEN)" };
        var maxRuns = new Option<int>("--max-concurrent") { Description = "Runs executing at once (others queue)", DefaultValueFactory = _ => 4 };
        var allowWorkdir = new Option<bool>("--allow-client-workdir") { Description = "Let API clients choose a run's working directory" };
        var noBrowser = new Option<bool>("--no-browser") { Description = "Do not open the Studio in a browser" };
        var maxSteps = new Option<int>("--max-steps")
        {
            Description = "Fail a run after this many stage executions, a guard against runaway loops (0 = unlimited)",
            DefaultValueFactory = _ => new ReactorServerOptions().MaxStepsPerRun,
        };
        var options = new RunOptionSet();
        var command = new Command(name, description) { urls, token, maxRuns, allowWorkdir };
        options.MaxCost.Description = "Default cost budget per run in US dollars: a run stops, resumably, before its next stage once its recorded cost reaches it (requests may set their own max_cost)";
        if (openBrowser)
        {
            command.Options.Add(noBrowser);
        }

        // The options `reactor run` shares with the server, so scripts can pass the same flags to both.
        foreach (var option in new Option[] { options.Backend, options.Model, options.Provider, options.MaxTokens, options.WorkingDirectory, maxSteps, options.MaxCost, options.NoColor, options.CopilotRuntime, options.CopilotCli, options.ClaudeCli })
        {
            command.Options.Add(option);
        }

        options.AddValidators();
        command.SetAction(async (result, cancellationToken) =>
        {
            var workDir = options.WorkDir(result);
            var authToken = result.GetValue(token) ?? Environment.GetEnvironmentVariable("REACTOR_SERVER_TOKEN");
            var listen = result.GetValue(urls)!;
            var color = ConsoleReporter.ColorSupported(result.GetValue(options.NoColor));
            if (string.IsNullOrEmpty(authToken) && !IsLoopbackOnly(listen))
            {
                await Console.Error.WriteLineAsync(
                    "⚠ Listening on a non-loopback address without --token: anyone who can reach this port can run pipelines (and therefore shell commands). " +
                    "Without a token, only requests addressed to an IP address or localhost are served.")
                    .ConfigureAwait(false);
            }

            BackendSelection selection;
            try
            {
                selection = BackendFactory.Create(options.Backends(result));
            }
            catch (ConfigurationError ex)
            {
                await Console.Error.WriteLineAsync("✖ " + ex.Message).ConfigureAwait(false);
                return ExitCodes.Configuration;
            }

            await using (selection.ConfigureAwait(false))
            {
                PipelineCommands.EnsureRunsDirectory(workDir);
                var app = ReactorServer.Build(
                    new ReactorServerOptions
                    {
                        Backend = selection.Backend,
                        WorkingDirectory = workDir,
                        LogsBaseDirectory = PipelineCommands.RunsDirectory(workDir),
                        AuthToken = string.IsNullOrEmpty(authToken) ? null : authToken,
                        MaxConcurrentRuns = result.GetValue(maxRuns),
                        MaxStepsPerRun = Math.Max(0, result.GetValue(maxSteps)),
                        MaxCostPerRunUsd = result.GetValue(options.MaxCost),
                        AllowClientWorkingDirectory = result.GetValue(allowWorkdir),
                        BackendKind = selection.Kind,
                        BackendDescription = selection.Description,
                        BackendRoutes = selection.Routes,
                        ListModels = StudioModels.Lister(selection),
                    },
                    configure: builder =>
                    {
                        builder.WebHost.UseUrls(listen.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        if (!color)
                        {
                            builder.Logging.AddSimpleConsole(console => console.ColorBehavior = LoggerColorBehavior.Disabled);
                        }
                    });
                await using (app.ConfigureAwait(false))
                {
                    var consoleUrl = listen.Split([';', ','])[0].Trim();
                    Console.WriteLine($"⚛ Reactor server — backend: {selection.Description}");
                    Console.WriteLine($"  studio:  {consoleUrl}/" + (StudioAssets.IsAvailable ? "" : "  (not built: install Node.js 20.19+ and rebuild)"));
                    Console.WriteLine($"  console: {consoleUrl}/console");
                    Console.WriteLine($"  workdir: {workDir}");
                    if (!string.IsNullOrEmpty(authToken))
                    {
                        Console.WriteLine("  auth:    bearer token required");
                    }

                    await app.StartAsync(cancellationToken).ConfigureAwait(false);
                    if (openBrowser && !result.GetValue(noBrowser))
                    {
                        OpenBrowser(BrowserUrl(consoleUrl));
                    }

                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    Console.WriteLine("Shutting down…");
                    await app.Services.GetRequiredServiceAsyncDisposable<PipelineRunManager>().ConfigureAwait(false);
                    await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    public static Command Agent()
    {
        var prompt = new Argument<string?>("prompt") { Description = "Task for the agent (omit for an interactive session)", Arity = ArgumentArity.ZeroOrOne };
        var provider = new Option<string?>("--provider") { Description = "LLM provider (openai, anthropic, gemini, openai-compatible)" };
        var model = new Option<string?>("--model", "-m") { Description = "Model id (default: the provider's latest)" };
        var workdir = new Option<string?>("--workdir", "-C") { Description = "Working directory (default: current directory)" };
        var effort = new Option<string?>("--reasoning-effort") { Description = "low, medium, or high" };
        var maxRounds = new Option<int>("--max-tool-rounds") { Description = "Tool rounds per input (0 = unlimited)", DefaultValueFactory = _ => 0 };
        var maxTokens = new Option<int?>("--max-tokens") { Description = "Output token limit per LLM response (default: the provider's)" };
        maxTokens.Validators.Add(RunOptionSet.PositiveIfSet);
        var noColor = new Option<bool>("--no-color") { Description = "Disable colored output" };
        var command = new Command("agent", "Run the coding agent loop directly (interactive or one-shot)") { prompt, provider, model, workdir, effort, maxRounds, maxTokens, noColor };
        command.SetAction(async (result, cancellationToken) =>
        {
            await using var client = Client.FromEnv(timeout: BackendFactory.AgentTimeouts);
            if (client.ProviderNames.Count == 0)
            {
                await Console.Error.WriteLineAsync("✖ Set OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY, or OPENAI_COMPATIBLE_BASE_URL.").ConfigureAwait(false);
                return ExitCodes.Configuration;
            }

            var dir = Path.GetFullPath(result.GetValue(workdir) ?? Directory.GetCurrentDirectory());
            (string Provider, string Model) resolved;
            try
            {
                resolved = ModelResolution.Resolve(
                    new StageSettings { NodeId = "agent", Provider = result.GetValue(provider), Model = result.GetValue(model), WorkingDirectory = dir },
                    client,
                    null,
                    null);
            }
            catch (ConfigurationError ex)
            {
                await Console.Error.WriteLineAsync("✖ " + ex.Message).ConfigureAwait(false);
                return ExitCodes.Configuration;
            }

            var profile = ProviderProfiles.Create(resolved.Provider, resolved.Model);
            var config = new SessionConfig
            {
                ReasoningEffort = result.GetValue(effort),
                MaxToolRoundsPerInput = result.GetValue(maxRounds),
                MaxTokens = result.GetValue(maxTokens),
            };
            await using var session = new Session(profile, new LocalExecutionEnvironment(new LocalEnvironmentOptions { WorkingDirectory = dir }), client, config);
            var color = ConsoleReporter.ColorSupported(result.GetValue(noColor));
            session.Events.Subscribe(evt => RenderAgentEvent(evt, color));
            Console.WriteLine($"⚛ agent: {resolved.Provider}/{resolved.Model} in {dir}");

            try
            {
                if (result.GetValue(prompt) is { Length: > 0 } task)
                {
                    await session.SubmitAsync(task, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine();
                    return ExitCodes.Success;
                }

                Console.WriteLine("Type a request (empty line or 'exit' to quit).");
                while (!cancellationToken.IsCancellationRequested)
                {
                    Console.Write("\n> ");
                    var line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line) || line.Trim() is "exit" or "quit")
                    {
                        break;
                    }

                    await session.SubmitAsync(line, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine();
                }

                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is AbortError or OperationCanceledException)
            {
                return ExitCodes.Cancelled;
            }
            catch (SdkError ex)
            {
                await Console.Error.WriteLineAsync($"\n✖ {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
                return ExitCodes.PipelineFailed;
            }
            finally
            {
                var usage = session.TotalUsage;
                Console.Error.WriteLine($"[tokens: {usage.InputTokens} in / {usage.OutputTokens} out]");
            }
        });
        return command;
    }

    public static Command Models()
    {
        var provider = new Option<string?>("--provider") { Description = "Only this provider; 'copilot' lists the models your Copilot account can use" };
        var json = new Option<bool>("--json") { Description = "Print as JSON" };
        var command = new Command("models", "List models in the catalog (or available through GitHub Copilot)") { provider, json };
        command.SetAction(async (result, cancellationToken) =>
        {
            if (string.Equals(result.GetValue(provider), "copilot", StringComparison.OrdinalIgnoreCase))
            {
                return await ListCopilotModelsAsync(result.GetValue(json), cancellationToken).ConfigureAwait(false);
            }

            var models = ModelCatalog.ListModels(result.GetValue(provider));
            if (result.GetValue(json))
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(models, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return ExitCodes.Success;
            }

            Console.WriteLine($"{"MODEL",-34} {"PROVIDER",-18} {"CONTEXT",9}  {"COST",-12} CAPABILITIES");
            foreach (var model in models)
            {
                var caps = string.Join(",", new[] { model.SupportsTools ? "tools" : null, model.SupportsVision ? "vision" : null, model.SupportsReasoning ? "reasoning" : null }.Where(c => c is not null));
                var cost = Llm.ModelPrice.FromCatalog(model)?.RateText() ?? "-";
                Console.WriteLine($"{model.Id,-34} {model.Provider,-18} {model.ContextWindow,9}  {cost,-12} {caps}");
            }

            Console.WriteLine();
            Console.WriteLine("COST $in/$out: USD per 1M input / output tokens, from the model catalog (REACTOR_MODEL_CATALOG overrides it).");
            return ExitCodes.Success;
        });
        return command;
    }

    private static async Task<int> ListCopilotModelsAsync(bool json, CancellationToken cancellationToken)
    {
        await using var backend = new Copilot.CopilotSdkBackend();
        IReadOnlyList<Copilot.CopilotModelInfo> models;
        try
        {
            models = await backend.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Copilot.CopilotStageException or IOException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync("✖ " + ex.Message).ConfigureAwait(false);
            return ExitCodes.Configuration;
        }

        var prices = Llm.PriceTable.Copilot;
        if (json)
        {
            var listing = models.Select(m => new
            {
                m.Id,
                m.Name,
                m.PolicyState,
                m.BillingMultiplier,
                m.ContextWindow,
                m.ReasoningEfforts,
                m.Enabled,
                Price = prices.Find(m.Id),
            });
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(listing, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return ExitCodes.Success;
        }

        // COST: the runtime's billing multiplier when it reports one, else the price table's USD per 1M input/output tokens.
        Console.WriteLine($"{"MODEL",-28} {"COST",-14} {"CONTEXT",9}  {"POLICY",-9} REASONING EFFORTS");
        var priced = false;
        foreach (var model in models.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var rates = prices.Find(model.Id)?.RateText();
            priced |= rates is not null;
            var cost = model.BillingMultiplier is { } multiplier
                ? multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x" + (rates is null ? "" : " " + rates)
                : rates ?? "-";
            Console.WriteLine($"{model.Id,-28} {cost,-14} {model.ContextWindow,9}  {model.PolicyState ?? "-",-9} {string.Join(",", model.ReasoningEfforts)}");
        }

        if (priced)
        {
            var source = prices.Sources.FirstOrDefault();
            Console.WriteLine();
            Console.WriteLine("COST $in/$out: USD per 1M uncached input / output tokens; cached input is cheaper (--json shows all rates).");
            Console.WriteLine($"Usage is billed in AI credits (${prices.AiCreditUsd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} each); override the table with {Llm.PriceTable.OverrideVariable}.");
            if (source is not null)
            {
                Console.WriteLine($"Source: {source.Url}" + (source.Retrieved is null ? "" : $" (retrieved {source.Retrieved})"));
            }
        }

        if (prices.LoadError is { } error)
        {
            await Console.Error.WriteLineAsync("⚠ " + error).ConfigureAwait(false);
        }

        return ExitCodes.Success;
    }

    public static Command Doctor()
    {
        var command = new Command("doctor", "Check which backends and tools are available");
        command.SetAction(async (_, cancellationToken) =>
        {
            Console.WriteLine($"Reactor {typeof(HostCommands).Assembly.GetName().Version} on .NET {Environment.Version} ({RuntimeInformation.RuntimeIdentifier})");
            Console.WriteLine();
            Console.WriteLine("Native LLM providers (agent / direct backends):");
            var client = Client.FromEnv();
            await using (client.ConfigureAwait(false))
            {
                Check("OpenAI", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")), "OPENAI_API_KEY");
                Check("Anthropic", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")), "ANTHROPIC_API_KEY");
                Check("Gemini", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")), "GEMINI_API_KEY / GOOGLE_API_KEY");
                Check("OpenAI-compatible", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_COMPATIBLE_BASE_URL")), "OPENAI_COMPATIBLE_BASE_URL");
                Console.WriteLine();
                Console.WriteLine("Agent backends:");
                var runtime = BackendFactory.FindCopilotRuntime();
                Check("GitHub Copilot SDK runtime", runtime is not null, runtime ?? "not bundled; set COPILOT_CLI_PATH or use --copilot-runtime");
                await CheckCliAsync("GitHub Copilot CLI", new CopilotCliBackend(), "--version", cancellationToken).ConfigureAwait(false);
                await CheckCliAsync("Claude Code", new ClaudeCodeBackend(), "--version", cancellationToken).ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine("Tools:");
                var dot = await VersionAsync("dot", "-V", cancellationToken).ConfigureAwait(false);
                Check("Graphviz (dot)", dot is not null, dot ?? "not found — graph rendering disabled");
                var rg = await VersionAsync("rg", "--version", cancellationToken).ConfigureAwait(false);
                Check("ripgrep (rg)", rg is not null, rg ?? "not found — grep uses the built-in search");
                var git = await VersionAsync("git", "--version", cancellationToken).ConfigureAwait(false);
                Check("git", git is not null, git ?? "not found");
                Console.WriteLine();
                var auto = client.ProviderNames.Count > 0 ? "agent" : runtime is not null ? "copilot" : TryResolve(new CopilotCliBackend()) ? "copilot-cli" : TryResolve(new ClaudeCodeBackend()) ? "claude-code" : "simulate";
                Console.WriteLine($"--backend auto → {auto}");
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static void RenderAgentEvent(SessionEvent evt, bool color)
    {
        string Paint(string text, string code) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
        switch (evt.Kind)
        {
            case EventKind.AssistantTextDelta:
                Console.Write(evt.GetString("delta"));
                break;
            case EventKind.AssistantTextEnd when !string.IsNullOrEmpty(evt.GetString("text")) && !evt.Data.ContainsKey("streamed"):
                break;
            case EventKind.ToolCallStart:
                Console.WriteLine(Paint($"\n→ {evt.GetString("tool_name")} {Clip(evt.GetString("arguments"), 160)}", "36"));
                break;
            case EventKind.ToolCallEnd when evt.Data.TryGetValue("error", out var error) && error is not null:
                Console.WriteLine(Paint($"  ✗ {Clip(error.ToString(), 300)}", "31"));
                break;
            case EventKind.ToolCallEnd:
                Console.WriteLine(Paint($"  ✓ {Clip(evt.GetString("output"), 160)}", "2"));
                break;
            case EventKind.Warning or EventKind.LoopDetection or EventKind.TurnLimit:
                Console.WriteLine(Paint($"\n⚠ {evt.GetString("message") ?? evt.GetString("limit")}", "33"));
                break;
            case EventKind.Error:
                Console.WriteLine(Paint($"\n✖ {evt.GetString("error")}", "31"));
                break;
        }
    }

    private static string Clip(string? text, int max)
    {
        var single = (text ?? "").ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    private static void Check(string name, bool ok, string detail) => Console.WriteLine($"  {(ok ? "✔" : "✖")} {name,-28} {detail}");

    private static async Task CheckCliAsync(string name, CliAgentBackend backend, string versionArg, CancellationToken cancellationToken)
    {
        try
        {
            var command = backend.ResolveCommand();
            var startInfo = CommandResolver.CreateStartInfo(command, [versionArg]);
            var version = await RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
            Check(name, true, $"{command} {(version is null ? "" : "— " + version)}");
        }
        catch (CliAgentException ex)
        {
            Check(name, false, ex.Message);
        }
    }

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

    private static Task<string?> VersionAsync(string executable, string argument, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable);
        startInfo.ArgumentList.Add(argument);
        return RunAsync(startInfo, cancellationToken);
    }

    private static async Task<string?> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false)).Trim();
            if (text.Length == 0)
            {
                text = (await stderr.ConfigureAwait(false)).Trim();
            }

            return text.Split('\n')[0].Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsLoopbackOnly(string urls) =>
        urls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(url =>
            Uri.TryCreate(url.Replace("://*", "://0.0.0.0", StringComparison.Ordinal).Replace("://+", "://0.0.0.0", StringComparison.Ordinal), UriKind.Absolute, out var uri) &&
            (uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))));

    /// <summary>A browsable URL for a listen address (wildcard hosts become localhost).</summary>
    private static string BrowserUrl(string listen) =>
        listen.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal).TrimEnd('/') + "/";

    private static void OpenBrowser(string url)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", url)
                : new ProcessStartInfo("xdg-open", url);
            using var _ = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.WriteLine($"  open {url} in a browser");
        }
    }
}

internal static class ServiceProviderExtensions
{
    /// <summary>Disposes a singleton service early (cancels runs before the host stops).</summary>
    public static ValueTask GetRequiredServiceAsyncDisposable<T>(this IServiceProvider services)
        where T : IAsyncDisposable =>
        ((T)(services.GetService(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered."))).DisposeAsync();
}

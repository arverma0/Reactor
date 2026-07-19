using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Reactor.Agent;
using Reactor.Backends;

namespace Reactor.Copilot;

/// <summary>
/// <see cref="ICopilotRuntime"/> over the GitHub Copilot SDK. Starts one runtime process lazily (the bundled runtime,
/// <c>COPILOT_CLI_PATH</c>, or <see cref="CopilotSdkBackendOptions.RuntimePath"/>) and creates sessions on it.
/// </summary>
internal sealed class SdkCopilotRuntime(CopilotSdkBackendOptions options) : ICopilotRuntime
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CopilotClient? _client;

    public async Task<ICopilotConversation> CreateConversationAsync(CopilotConversationOptions conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var hookRunner = new ToolHookInterceptor(conversation.Hooks);
        var config = new GitHub.Copilot.SessionConfig
        {
            SessionId = conversation.SessionId,
            Model = conversation.Model,
            ReasoningEffort = conversation.ReasoningEffort,
            WorkingDirectory = conversation.WorkingDirectory,
            Streaming = false,

            // Pipeline stages run unattended: approve tool use. tool_hooks still veto individual calls (OnPreToolUse below).
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Hooks = new SessionHooks
            {
                OnPreToolUse = async (input, _) =>
                {
                    var settings = conversation.Hooks();
                    if (settings?.PreCommand is null)
                    {
                        return null!;
                    }

                    var decision = await hookRunner.BeforeAsync(ToCall(conversation, input.ToolName, input.ToolArgs, input.WorkingDirectory), CancellationToken.None).ConfigureAwait(false);
                    return decision.Allowed
                        ? new PreToolUseHookOutput { PermissionDecision = "allow" }
                        : new PreToolUseHookOutput { PermissionDecision = "deny", PermissionDecisionReason = $"Tool call blocked: {decision.Reason}" };
                },
                OnPostToolUse = async (input, _) =>
                {
                    if (conversation.Hooks()?.PostCommand is not null)
                    {
                        var output = input.ToolResult is { } result ? result.ToString() : "";
                        await hookRunner.AfterAsync(ToCall(conversation, input.ToolName, input.ToolArgs, input.WorkingDirectory), output, isError: false, CancellationToken.None).ConfigureAwait(false);
                    }

                    return null!;
                },
                OnPostToolUseFailure = async (input, _) =>
                {
                    if (conversation.Hooks()?.PostCommand is not null)
                    {
                        await hookRunner.AfterAsync(ToCall(conversation, input.ToolName, input.ToolArgs, input.WorkingDirectory), input.Error ?? "", isError: true, CancellationToken.None).ConfigureAwait(false);
                    }

                    return null!;
                },
            },
        };

        if (conversation.ExcludedTools is { Count: > 0 } excluded)
        {
            config.ExcludedTools = excluded.ToList();
        }

        var session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        try
        {
            // The session may use its working directory; other directories the stage needs join its allow-list.
            foreach (var directory in conversation.AdditionalDirectories)
            {
                var added = await session.Rpc.Permissions.Paths.AddAsync(directory, cancellationToken).ConfigureAwait(false);
                if (added?.Success != true)
                {
                    throw new CopilotStageException($"The Copilot runtime did not allow access to '{directory}'.", retryable: false);
                }
            }
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SdkConversation(session);
    }

    public async Task<IReadOnlyList<CopilotModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(m => new CopilotModelInfo(
                m.Id,
                string.IsNullOrEmpty(m.Name) ? m.Id : m.Name,
                m.Policy?.State,
                m.Billing?.Multiplier,
                m.Capabilities?.Limits?.MaxContextWindowTokens ?? 0,
                m.SupportedReasoningEfforts?.ToList() ?? (m.Capabilities?.Supports?.ReasoningEffort == true ? ["low", "medium", "high"] : [])))
            .ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        try
        {
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or AggregateException)
        {
            try
            {
                await client.ForceStopAsync().ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is IOException or InvalidOperationException)
            {
            }
        }

        await client.DisposeAsync().ConfigureAwait(false);
        _client = null;
    }

    private async Task<CopilotClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_client is { } existing)
        {
            return existing;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } raced)
            {
                return raced;
            }

            var clientOptions = new CopilotClientOptions();
            if (options.RuntimePath is { Length: > 0 } path)
            {
                clientOptions.Connection = RuntimeConnection.ForStdio(path, null!);
            }

            if (options.GitHubToken is { Length: > 0 } token)
            {
                clientOptions.GitHubToken = token;
            }

            if (options.BaseDirectory is { Length: > 0 } baseDirectory)
            {
                clientOptions.BaseDirectory = baseDirectory;
            }

            var client = new CopilotClient(clientOptions);
            try
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new CopilotStageException(
                    $"Could not start the GitHub Copilot runtime: {ex.Message}. Install the reactor CLI with its bundled runtime, set " +
                    "COPILOT_CLI_PATH, or pass --copilot-runtime.",
                    retryable: false,
                    ex);
            }

            _client = client;
            return client;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static ToolCallInfo ToCall(CopilotConversationOptions options, string? toolName, JsonElement? arguments, string? workingDirectory)
    {
        var args = arguments is { ValueKind: JsonValueKind.Object } element ? JsonNode.Parse(element.GetRawText()) as JsonObject ?? [] : [];
        return new ToolCallInfo(options.SessionId, "", toolName ?? "tool", args, string.IsNullOrEmpty(workingDirectory) ? options.WorkingDirectory : workingDirectory);
    }

    private sealed class SdkConversation(CopilotSession session) : ICopilotConversation
    {
        public string SessionId => session.SessionId;

        public IDisposable Subscribe(Action<CopilotActivity> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return session.On<GitHub.Copilot.SessionEvent>(evt =>
            {
                if (Map(evt) is { } activity)
                {
                    handler(activity);
                }
            });
        }

        public async Task<string?> SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt }, timeout, cancellationToken).ConfigureAwait(false);
            return reply?.Data?.Content;
        }

        public Task AbortAsync() => session.AbortAsync(CancellationToken.None);

        public Task SetModelAsync(string model, string? reasoningEffort, CancellationToken cancellationToken) =>
            session.SetModelAsync(model, reasoningEffort!, null!, cancellationToken);

        public ValueTask DisposeAsync() => session.DisposeAsync();

        private static CopilotActivity? Map(GitHub.Copilot.SessionEvent evt) => evt switch
        {
            AssistantMessageEvent m => new CopilotActivity.AssistantMessage(m.Data.Content ?? "", m.Data.Model) { FromSubagent = IsSub(evt.AgentId) },
            ToolExecutionStartEvent s => new CopilotActivity.ToolStarted(
                s.Data.ToolCallId ?? "",
                s.Data.ToolName ?? "tool",
                s.Data.Arguments is { } args ? JsonNode.Parse(args.GetRawText()) : null) { FromSubagent = IsSub(evt.AgentId) },
            ToolExecutionCompleteEvent c => new CopilotActivity.ToolCompleted(c.Data.ToolCallId ?? "", c.Data.Success, c.Data.Error?.Message) { FromSubagent = IsSub(evt.AgentId) },
            AssistantUsageEvent u => new CopilotActivity.Usage(
                u.Data.Model,
                u.Data.InputTokens ?? 0,
                u.Data.OutputTokens ?? 0,
                u.Data.CacheReadTokens ?? 0,
                u.Data.ReasoningTokens ?? 0) { FromSubagent = IsSub(evt.AgentId) },
            SessionErrorEvent e => new CopilotActivity.Error(e.Data.Message ?? "unknown error", e.Data.ErrorType, e.Data.StatusCode),
            SessionWarningEvent w => new CopilotActivity.Warning(w.Data.Message ?? ""),
            SessionUsageCheckpointEvent k => new CopilotActivity.UsageCheckpoint(k.Data.TotalNanoAiu),
            _ => null,
        };

        private static bool IsSub(string? agentId) => !string.IsNullOrEmpty(agentId);
    }
}

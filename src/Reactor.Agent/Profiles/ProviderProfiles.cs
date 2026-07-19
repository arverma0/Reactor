using System.Text.Json.Nodes;
using Reactor.Agent.Prompts;
using Reactor.Agent.Tools;
using Reactor.Llm;

namespace Reactor.Agent;

/// <summary>
/// A provider-aligned agent profile (spec 3.2): the tool set and system prompt tuned for one model family, plus capability
/// flags. Custom tools can be registered on <see cref="ToolRegistry"/> (latest registration wins).
/// </summary>
public interface IProviderProfile
{
    /// <summary>Profile family: <c>openai</c>, <c>anthropic</c>, <c>gemini</c> (or a custom id).</summary>
    string Id { get; }

    /// <summary>Client provider key used for routing (defaults to <see cref="Id"/>; e.g. <c>openai-compatible</c>).</summary>
    string ProviderName { get; set; }

    /// <summary>The model id; may be changed between turns.</summary>
    string Model { get; set; }

    ToolRegistry ToolRegistry { get; }

    bool SupportsReasoning { get; }

    bool SupportsStreaming { get; }

    bool SupportsParallelToolCalls { get; }

    /// <summary>Context window in tokens (used for the 80% usage warning).</summary>
    int ContextWindowSize { get; }

    /// <summary>Profile-specific default shell timeout, overriding the session default (e.g. 120 s for Anthropic).</summary>
    int? DefaultShellTimeoutMs { get; }

    /// <summary>Project instruction files this profile loads (AGENTS.md is always included).</summary>
    IReadOnlyList<string> ProjectDocFileNames { get; }

    /// <summary>Provider-specific base instructions (layer 1 of the system prompt).</summary>
    string BaseInstructions { get; }

    IReadOnlyList<ToolDefinition> Tools();

    string BuildSystemPrompt(EnvironmentSnapshot? environment, string? projectDocs, SessionConfig config, IReadOnlyList<ToolDefinition>? extraTools = null);

    JsonObject? ProviderOptions(SessionConfig config);

    /// <summary>An independent copy (own tool registry), optionally with a different model (used for subagents).</summary>
    IProviderProfile Clone(string? model = null);
}

/// <summary>Shared implementation for provider profiles.</summary>
public class ProviderProfile : IProviderProfile
{
    public ProviderProfile(
        string id,
        string model,
        ToolRegistry toolRegistry,
        string baseInstructions,
        IReadOnlyList<string>? projectDocFileNames = null,
        int? contextWindowSize = null,
        int? defaultShellTimeoutMs = null,
        bool supportsReasoning = true,
        bool supportsStreaming = true,
        bool supportsParallelToolCalls = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        Id = id;
        ProviderName = id;
        Model = model;
        ToolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        BaseInstructions = baseInstructions ?? "";
        ProjectDocFileNames = projectDocFileNames ?? ["AGENTS.md"];
        ContextWindowSize = contextWindowSize ?? (ModelCatalog.GetModelInfo(model)?.ContextWindow is int window and > 0 ? window : 200_000);
        DefaultShellTimeoutMs = defaultShellTimeoutMs;
        SupportsReasoning = supportsReasoning;
        SupportsStreaming = supportsStreaming;
        SupportsParallelToolCalls = supportsParallelToolCalls;
    }

    public string Id { get; }

    public string ProviderName { get; set; }

    public string Model { get; set; }

    public ToolRegistry ToolRegistry { get; }

    public bool SupportsReasoning { get; }

    public bool SupportsStreaming { get; }

    public bool SupportsParallelToolCalls { get; }

    public int ContextWindowSize { get; }

    public int? DefaultShellTimeoutMs { get; }

    public IReadOnlyList<string> ProjectDocFileNames { get; }

    public string BaseInstructions { get; }

    /// <summary>Extra provider options merged into every request (escape hatch), keyed by provider name.</summary>
    public JsonObject? ExtraProviderOptions { get; set; }

    public IReadOnlyList<ToolDefinition> Tools() => ToolRegistry.Definitions();

    public virtual string BuildSystemPrompt(EnvironmentSnapshot? environment, string? projectDocs, SessionConfig config, IReadOnlyList<ToolDefinition>? extraTools = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var tools = Tools().Concat(extraTools ?? []).GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
        return SystemPromptBuilder.Build(BaseInstructions, environment, Model, tools, projectDocs, config.UserInstructions);
    }

    public virtual JsonObject? ProviderOptions(SessionConfig config) => ExtraProviderOptions?.DeepClone() as JsonObject;

    public virtual IProviderProfile Clone(string? model = null) => new ProviderProfile(
        Id,
        model ?? Model,
        ToolRegistry.Clone(),
        BaseInstructions,
        ProjectDocFileNames,
        model is null ? ContextWindowSize : null,
        DefaultShellTimeoutMs,
        SupportsReasoning,
        SupportsStreaming,
        SupportsParallelToolCalls)
    {
        ProviderName = ProviderName,
        ExtraProviderOptions = ExtraProviderOptions?.DeepClone() as JsonObject,
    };
}

/// <summary>Creates the built-in provider-aligned profiles.</summary>
public static class ProviderProfiles
{
    public const string OpenAI = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";

    /// <summary>
    /// codex-rs-aligned profile for GPT-5+ models: <c>apply_patch</c> (v4a) for edits, <c>write_file</c> for new files,
    /// <c>shell</c> (10 s default timeout), <c>read_file</c>, <c>grep</c>, <c>glob</c>.
    /// </summary>
    public static ProviderProfile CreateOpenAI(string model = "gpt-5.2-codex") => new(
        OpenAI,
        model,
        new ToolRegistry([CoreTools.ReadFile(), CoreTools.ApplyPatch(), CoreTools.WriteFile(), CoreTools.Shell(), CoreTools.Grep(), CoreTools.Glob()]),
        SystemPrompts.OpenAI,
        ["AGENTS.md", ".codex/instructions.md"]);

    /// <summary>
    /// Claude Code-aligned profile: <c>edit_file</c> (old_string/new_string) as the native editing format, <c>write_file</c>,
    /// <c>read_file</c>, <c>shell</c> (120 s default timeout), <c>grep</c> with output modes, <c>glob</c>.
    /// </summary>
    public static ProviderProfile CreateAnthropic(string model = "claude-opus-4-6") => new(
        Anthropic,
        model,
        new ToolRegistry([CoreTools.ReadFile(), CoreTools.WriteFile(), CoreTools.EditFile(), CoreTools.Shell(), CoreTools.Grep(includeOutputModes: true), CoreTools.Glob()]),
        SystemPrompts.Anthropic,
        ["AGENTS.md", "CLAUDE.md"],
        defaultShellTimeoutMs: 120_000);

    /// <summary>
    /// gemini-cli-aligned profile: <c>read_file</c>, <c>read_many_files</c>, <c>write_file</c>, <c>edit_file</c>
    /// (replace with expected_replacements), <c>shell</c>, <c>grep</c>, <c>glob</c>, <c>list_dir</c>, <c>web_fetch</c>.
    /// </summary>
    public static ProviderProfile CreateGemini(string model = "gemini-3.1-pro-preview") => new(
        Gemini,
        model,
        new ToolRegistry([
            CoreTools.ReadFile(), CoreTools.ReadManyFiles(), CoreTools.WriteFile(), CoreTools.EditFileGemini(), CoreTools.Shell(),
            CoreTools.Grep(), CoreTools.Glob(), CoreTools.ListDir(), CoreTools.WebFetch(),
        ]),
        SystemPrompts.Gemini,
        ["AGENTS.md", "GEMINI.md"]);

    /// <summary>
    /// Creates the profile for <paramref name="provider"/> (or the provider inferred from <paramref name="model"/>).
    /// Unknown providers (e.g. OpenAI-compatible endpoints) get the OpenAI-style tool set with the provider name preserved.
    /// </summary>
    public static ProviderProfile Create(string? provider, string? model)
    {
        var family = (provider ?? ModelCatalog.InferProvider(model) ?? OpenAI).ToLowerInvariant();
        ProviderProfile profile = family switch
        {
            Anthropic => model is null ? CreateAnthropic() : CreateAnthropic(model),
            Gemini => model is null ? CreateGemini() : CreateGemini(model),
            _ => model is null ? CreateOpenAI() : CreateOpenAI(model),
        };

        if (provider is not null)
        {
            profile.ProviderName = provider;
        }

        return profile;
    }
}

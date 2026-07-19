using System.Text.Json.Nodes;
using Reactor.Llm;
using static Reactor.Agent.Tests.Session.FakeLlm;
using static Reactor.Agent.Tests.Session.SessionHarness;

namespace Reactor.Agent.Tests.Session;

public sealed class SubagentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool IsChild(Request request, string task) =>
        request.Messages.Any(m => m.Role == Role.User && m.Text == task);

    [Fact]
    public async Task Parent_spawns_waits_for_and_uses_subagent_output()
    {
        var (session, llm, _, events) = Create();
        llm.Responder = request =>
        {
            if (IsChild(request, "write the parser"))
            {
                return Text("parser written: 42 lines");
            }

            return ToolResults(request).Count switch
            {
                0 => ToolCalls(("s1", "spawn_agent", """{"task":"write the parser"}""")),
                1 => ToolCalls(("w1", "wait", """{"agent_id":"agent-1"}""")),
                _ => Text("parent done"),
            };
        };

        await session.SubmitAsync("build it", Ct);

        Assert.Equal("parent done", session.LastAssistantText);
        var parentFinal = llm.Requests.Last(r => !IsChild(r, "write the parser"));
        var results = ToolResults(parentFinal);
        Assert.Contains("Spawned subagent agent-1", results[0].ContentText);
        Assert.Contains("success: true", results[1].ContentText);
        Assert.Contains("parser written: 42 lines", results[1].ContentText);

        // Child events are forwarded to the parent's stream, tagged with the agent id.
        Assert.Contains(events, e => e.Kind == EventKind.AssistantTextEnd && e.GetString("agent_id") == "agent-1");
        Assert.Contains(events, e => e.SessionId == session.Id + "/agent-1");
    }

    [Fact]
    public async Task Subagents_cannot_spawn_further_subagents_at_max_depth()
    {
        var (session, llm, _, _) = Create();
        Request? childRequest = null;
        llm.Responder = request =>
        {
            if (IsChild(request, "child task"))
            {
                childRequest ??= request;
                return ToolResults(request).Count == 0
                    ? ToolCalls(("x", "spawn_agent", """{"task":"grandchild"}"""))
                    : Text("child finished");
            }

            return ToolResults(request).Count switch
            {
                0 => ToolCalls(("s1", "spawn_agent", """{"task":"child task"}""")),
                1 => ToolCalls(("w1", "wait", """{"agent_id":"agent-1"}""")),
                _ => Text("done"),
            };
        };

        await session.SubmitAsync("go", Ct);

        Assert.NotNull(childRequest);
        Assert.DoesNotContain(childRequest.Tools!, t => t.Name == "spawn_agent");
        var childToolResult = llm.Requests.Where(r => IsChild(r, "child task")).SelectMany(ToolResults).Single();
        Assert.True(childToolResult.IsError);
        Assert.Equal("Unknown tool: spawn_agent", childToolResult.ContentText);
    }

    [Fact]
    public async Task Subagent_model_override_and_turn_limit_apply()
    {
        var (session, llm, _, _) = Create();
        llm.Responder = request =>
        {
            if (IsChild(request, "busy work"))
            {
                return ToolCalls(("e" + Guid.NewGuid().ToString("N")[..6], "echo", """{"text":"again"}"""));
            }

            return ToolResults(request).Count switch
            {
                0 => ToolCalls(("s1", "spawn_agent", """{"task":"busy work","model":"other-model","max_turns":2}""")),
                1 => ToolCalls(("w1", "wait", """{"agent_id":"agent-1"}""")),
                _ => Text("done"),
            };
        };

        await session.SubmitAsync("go", Ct);

        var childRequests = llm.Requests.Where(r => IsChild(r, "busy work")).ToList();
        Assert.Equal(2, childRequests.Count);
        Assert.All(childRequests, r => Assert.Equal("other-model", r.Model));
        Assert.Contains("turns used: 2", ToolResults(llm.Requests.Last(r => !IsChild(r, "busy work")))[1].ContentText);
    }

    [Fact]
    public async Task Send_input_to_an_idle_subagent_starts_a_new_turn_and_close_terminates_it()
    {
        var (session, llm, env, _) = Create();
        llm.Responder = request =>
        {
            if (IsChild(request, "step one"))
            {
                return Text(request.Messages.Any(m => m.Role == Role.User && m.Text == "step two") ? "did step two" : "did step one");
            }

            return ToolResults(request).Count switch
            {
                0 => ToolCalls(("s1", "spawn_agent", """{"task":"step one"}""")),
                1 => ToolCalls(("w1", "wait", """{"agent_id":"agent-1"}""")),
                2 => ToolCalls(("i1", "send_input", """{"agent_id":"agent-1","message":"step two"}""")),
                3 => ToolCalls(("w2", "wait", """{"agent_id":"agent-1"}""")),
                4 => ToolCalls(("c1", "close_agent", """{"agent_id":"agent-1"}""")),
                _ => Text("all done"),
            };
        };

        await session.SubmitAsync("go", Ct);

        var results = ToolResults(llm.Requests.Last(r => !IsChild(r, "step one")));
        Assert.Contains("did step one", results[1].ContentText);
        Assert.Equal("Message delivered to agent-1.", results[2].ContentText);
        Assert.Contains("did step two", results[3].ContentText);
        Assert.Equal("Subagent agent-1 closed (final status: completed).", results[4].ContentText);

        // Closing the subagent must not clean up the parent's (shared) environment.
        Assert.Equal(0, env.CleanupCount);
    }

    [Fact]
    public async Task Unknown_agent_ids_produce_tool_errors()
    {
        var (session, llm, _, _) = Create();
        llm.ThenTools(("w1", "wait", """{"agent_id":"agent-9"}""")).ThenText("ok");

        await session.SubmitAsync("go", Ct);

        var result = Assert.Single(ToolResults(llm.Requests[1]));
        Assert.True(result.IsError);
        Assert.Contains("Unknown subagent 'agent-9'", result.ContentText);
    }

    [Fact]
    public async Task Failed_subagent_reports_failure()
    {
        var (session, llm, _, _) = Create();
        llm.Responder = request =>
        {
            if (IsChild(request, "doomed"))
            {
                throw new AuthenticationError("child key revoked", "test", 401);
            }

            return ToolResults(request).Count switch
            {
                0 => ToolCalls(("s1", "spawn_agent", """{"task":"doomed"}""")),
                1 => ToolCalls(("w1", "wait", """{"agent_id":"agent-1"}""")),
                _ => Text("handled"),
            };
        };

        await session.SubmitAsync("go", Ct);

        var waitResult = ToolResults(llm.Requests.Last(r => !IsChild(r, "doomed")))[1].ContentText;
        Assert.Contains("success: false", waitResult);
        Assert.Contains("child key revoked", waitResult);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Closing_the_parent_closes_running_subagents()
    {
        var (session, llm, _, _) = Create();
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.BeforeRespond = async (request, ct) =>
        {
            if (IsChild(request, "slow child"))
            {
                childStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        llm.Responder = request => ToolResults(request).Count == 0
            ? ToolCalls(("s1", "spawn_agent", """{"task":"slow child"}"""))
            : Text("parent idle");

        await session.SubmitAsync("go", Ct);
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await session.CloseAsync();

        Assert.Equal(SessionState.Closed, session.State);
    }

    [Fact]
    public async Task Working_dir_scopes_the_subagent_environment()
    {
        var env = new FakeEnvironment();
        string? childPrompt = null;
        var (session, llm, _, _) = Create(env: env);
        llm.Responder = request =>
        {
            if (IsChild(request, "scoped work"))
            {
                childPrompt ??= request.Messages[0].Text;
                return Text("scoped done");
            }

            return ToolResults(request).Count switch
            {
                0 => ToolCalls(("s1", "spawn_agent", """{"task":"scoped work","working_dir":"packages/core"}""")),
                1 => ToolCalls(("w1", "wait", """{"agent_id":"agent-1"}""")),
                _ => Text("done"),
            };
        };

        await session.SubmitAsync("go", Ct);

        Assert.Contains("Working directory: " + Path.GetFullPath(Path.Combine(env.WorkingDirectory, "packages/core")), childPrompt);
    }
}

public sealed class LoopDetectorTests
{
    [Theory]
    [InlineData(new[] { "a", "a", "a", "a" }, 4, true)]
    [InlineData(new[] { "a", "b", "a", "b" }, 4, true)]
    [InlineData(new[] { "a", "b", "c", "a", "b", "c" }, 6, true)]
    [InlineData(new[] { "x", "a", "b", "c", "a", "b", "c" }, 6, true)]
    [InlineData(new[] { "a", "b", "c", "d" }, 4, false)]
    [InlineData(new[] { "a", "a", "a" }, 4, false)]
    [InlineData(new[] { "a", "b", "a", "c" }, 4, false)]
    [InlineData(new[] { "a", "b", "c", "a", "b" }, 5, false)]
    public void Detects_repeating_patterns_of_length_one_to_three(string[] signatures, int window, bool expected) =>
        Assert.Equal(expected, LoopDetector.IsLooping(signatures, window));

    [Fact]
    public void Default_window_of_ten_detects_alternation()
    {
        var calls = Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? "read" : "write").ToList();
        Assert.True(LoopDetector.IsLooping(calls, 10));
    }

    [Fact]
    public void Signature_is_independent_of_argument_key_order()
    {
        var a = LoopDetector.Signature("edit", (JsonObject)JsonNode.Parse("""{"path":"a.cs","old":"x","nested":{"b":1,"a":2}}""")!);
        var b = LoopDetector.Signature("edit", (JsonObject)JsonNode.Parse("""{"nested":{"a":2,"b":1},"old":"x","path":"a.cs"}""")!);
        var c = LoopDetector.Signature("edit", (JsonObject)JsonNode.Parse("""{"path":"b.cs","old":"x","nested":{"b":1,"a":2}}""")!);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("edit:", a);
    }
}

public sealed class ScopedExecutionEnvironmentTests
{
    [Fact]
    public async Task Relative_paths_and_commands_resolve_inside_the_scope()
    {
        var inner = new FakeEnvironment();
        var scopedDir = Path.Combine(inner.WorkingDirectory, "sub");
        var scoped = new ScopedExecutionEnvironment(inner, scopedDir);

        await scoped.WriteFileAsync("a.txt", "hello", TestContext.Current.CancellationToken);

        Assert.True(inner.Files.ContainsKey(Path.Combine(scopedDir, "a.txt")));
        Assert.Equal(Path.GetFullPath(scopedDir), scoped.WorkingDirectory);
        var absolute = Path.GetFullPath(Path.Combine(inner.WorkingDirectory, "other.txt"));
        Assert.Equal(absolute, scoped.ResolvePath(absolute));
        await scoped.CleanupAsync();
        Assert.Equal(0, inner.CleanupCount);
    }
}

public sealed class ProviderProfileTests
{
    [Fact]
    public void OpenAI_profile_uses_apply_patch_and_codex_style_tools()
    {
        var profile = ProviderProfiles.CreateOpenAI("gpt-5.2-codex");
        Assert.Equal(["read_file", "apply_patch", "write_file", "shell", "grep", "glob"], profile.ToolRegistry.Names());
        Assert.Equal("openai", profile.ProviderName);
        Assert.Contains(".codex/instructions.md", profile.ProjectDocFileNames);
        Assert.Null(profile.DefaultShellTimeoutMs);
    }

    [Fact]
    public void Anthropic_profile_uses_edit_file_and_a_120_second_shell_timeout()
    {
        var profile = ProviderProfiles.CreateAnthropic();
        Assert.Equal(["read_file", "write_file", "edit_file", "shell", "grep", "glob"], profile.ToolRegistry.Names());
        Assert.Equal(120_000, profile.DefaultShellTimeoutMs);
        Assert.Contains("CLAUDE.md", profile.ProjectDocFileNames);
        Assert.Contains("old_string", profile.ToolRegistry.Get("edit_file")!.Definition.Parameters.ToJsonString());
    }

    [Fact]
    public void Gemini_profile_includes_gemini_cli_tools()
    {
        var profile = ProviderProfiles.CreateGemini();
        Assert.Equal(
            ["read_file", "read_many_files", "write_file", "edit_file", "shell", "grep", "glob", "list_dir", "web_fetch"],
            profile.ToolRegistry.Names());
        Assert.Contains("GEMINI.md", profile.ProjectDocFileNames);
    }

    [Theory]
    [InlineData(null, "claude-sonnet-4-5", "anthropic", "anthropic")]
    [InlineData(null, "gemini-2.5-pro", "gemini", "gemini")]
    [InlineData(null, "gpt-5.2", "openai", "openai")]
    [InlineData("openai-compatible", "llama-3.3-70b", "openai", "openai-compatible")]
    [InlineData("anthropic", null, "anthropic", "anthropic")]
    public void Create_selects_the_profile_family(string? provider, string? model, string expectedFamily, string expectedProvider)
    {
        var profile = ProviderProfiles.Create(provider, model);
        Assert.Equal(expectedFamily, profile.Id);
        Assert.Equal(expectedProvider, profile.ProviderName);
        if (model is not null)
        {
            Assert.Equal(model, profile.Model);
        }
    }

    [Fact]
    public void Custom_tools_override_profile_tools_by_name()
    {
        var profile = ProviderProfiles.CreateAnthropic();
        var custom = new RegisteredTool(
            new ToolDefinition("shell", "Restricted shell.", Schema("""{"type":"object","properties":{"command":{"type":"string"}}}""")),
            (_, _) => Task.FromResult<ToolOutput>("denied"));
        profile.ToolRegistry.Register(custom);

        Assert.Same(custom, profile.ToolRegistry.Get("shell"));
        Assert.Equal(6, profile.ToolRegistry.Count);
        Assert.Equal("shell", profile.ToolRegistry.Names()[3]);
    }

    [Fact]
    public void Clone_copies_registry_and_overrides_model()
    {
        var profile = ProviderProfiles.CreateOpenAI();
        profile.ProviderName = "openai-compatible";
        var clone = profile.Clone("gpt-5.2-mini");
        clone.ToolRegistry.Unregister("grep");

        Assert.Equal("gpt-5.2-mini", clone.Model);
        Assert.Equal("openai-compatible", clone.ProviderName);
        Assert.True(profile.ToolRegistry.Contains("grep"));
    }

    [Fact]
    public void System_prompts_are_provider_specific()
    {
        var config = new SessionConfig();
        var openai = ProviderProfiles.CreateOpenAI().BuildSystemPrompt(null, null, config);
        var anthropic = ProviderProfiles.CreateAnthropic().BuildSystemPrompt(null, null, config);
        var gemini = ProviderProfiles.CreateGemini().BuildSystemPrompt(null, null, config);

        Assert.Contains("apply_patch", openai);
        Assert.Contains("edit_file", anthropic);
        Assert.NotEqual(openai, anthropic);
        Assert.NotEqual(anthropic, gemini);
    }
}

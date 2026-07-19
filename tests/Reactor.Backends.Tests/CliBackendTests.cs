using System.Text.Json.Nodes;
using Reactor.Backends.Cli;
using Reactor.Backends.Tests.Support;
using Reactor.Events;
using Reactor.Execution;

namespace Reactor.Backends.Tests;

public sealed class CliBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal const string CopilotOutput = """
        {"type":"session.tools_updated","data":{"model":"gpt-5-mini"},"ephemeral":true}
        {"type":"user.message","data":{"content":"prompt"}}
        {"type":"assistant.message_delta","data":{"messageId":"m1","deltaContent":"Creat"},"ephemeral":true}
        {"type":"assistant.message","data":{"messageId":"m1","model":"gpt-5-mini","content":"Creating hello.txt.","toolRequests":[{"toolCallId":"call_1","name":"create"}]}}
        {"type":"tool.execution_start","data":{"toolCallId":"call_1","toolName":"create","arguments":{"path":"hello.txt","file_text":"hi"}}}
        {"type":"tool.execution_complete","data":{"toolCallId":"call_1","success":true,"result":{"content":"Created file hello.txt"}}}
        {"type":"tool.execution_start","data":{"toolCallId":"call_2","toolName":"bash","arguments":{"command":"npm test"}}}
        {"type":"tool.execution_complete","data":{"toolCallId":"call_2","success":false,"error":{"message":"command failed"}}}
        {"type":"assistant.message","data":{"messageId":"m2","model":"gpt-5-mini","content":"DONE.","toolRequests":[]}}
        {"type":"session.usage_checkpoint","data":{"totalPremiumRequests":2}}
        {"type":"result","sessionId":"5f0c1d2e-0000-4000-8000-000000000001","exitCode":0,"usage":{"premiumRequests":2,"totalApiDurationMs":7368,"codeChanges":{"linesAdded":1,"linesRemoved":0,"filesModified":["hello.txt"]}}}
        """;

    internal const string ClaudeOutput = """
        {"type":"system","subtype":"init","cwd":"/work","session_id":"S","tools":["Bash","Write"],"model":"claude-sonnet-4-5-20250929"}
        {"type":"assistant","message":{"id":"m1","model":"claude-sonnet-4-5-20250929","role":"assistant","content":[{"type":"text","text":"I'll create the file."},{"type":"tool_use","id":"toolu_1","name":"Write","input":{"file_path":"claude.txt","content":"yo"}}]}}
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"File created"}]}}
        {"type":"assistant","message":{"id":"m2","model":"claude-sonnet-4-5-20250929","role":"assistant","content":[{"type":"tool_use","id":"toolu_2","name":"Bash","input":{"command":"npm test"}}]}}
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_2","content":"tests failed","is_error":true}]}}
        {"type":"assistant","message":{"id":"m3","model":"claude-sonnet-4-5-20250929","role":"assistant","content":[{"type":"text","text":"DONE"}]}}
        {"type":"result","subtype":"success","is_error":false,"duration_ms":2000,"duration_api_ms":1500,"num_turns":3,"result":"DONE","session_id":"S","total_cost_usd":0.0123,"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":5}}
        """;

    internal const string ClaudeNotLoggedIn = """
        {"type":"system","subtype":"init","cwd":"/work","session_id":"S","tools":[],"model":"claude-sonnet-4-5"}
        {"type":"assistant","message":{"id":"m1","model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"Not logged in · Please run /login"}]}}
        {"type":"result","subtype":"success","is_error":true,"duration_ms":1191,"duration_api_ms":0,"num_turns":1,"result":"Not logged in · Please run /login","session_id":"S","total_cost_usd":0}
        """;

    private const string TwoStages = """
        digraph G {
          graph [goal="Ship it"]
          start [shape=Mdiamond]
          exit [shape=Msquare]
          plan [prompt="Plan for $goal", llm_model="claude-sonnet-4-5", reasoning_effort="low"]
          build [prompt="Build it"]
          start -> plan -> build -> exit
        }
        """;

    private static string SessionIdArg(string invocation, string flag)
    {
        var parts = invocation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(parts, flag);
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "";
    }

    [Fact]
    public async Task Copilot_cli_stage_parses_output_and_passes_arguments()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), CopilotOutput);
        await using var backend = new CopilotCliBackend(new CopilotCliOptions { Executable = cli.Executable });
        var events = new List<PipelineEvent>();

        var result = await Pipelines.Runner(backend).RunAsync(TwoStages, Pipelines.Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        Assert.Equal("DONE.", File.ReadAllText(dir.Combine("run", "plan", "response.md")));
        var invocations = cli.Invocations;
        Assert.Equal(2, invocations.Count);
        var first = invocations[0];
        Assert.Contains("--output-format json", first);
        Assert.Contains("--allow-all-tools", first);
        Assert.Contains("--no-ask-user", first);
        Assert.Contains("--model claude-sonnet-4.5", first);
        Assert.Contains("--reasoning-effort low", first);
        Assert.True(Guid.TryParse(SessionIdArg(first, "--session-id"), out _));
        Assert.DoesNotContain("--model", invocations[1]);
        Assert.DoesNotContain("--reasoning-effort", invocations[1]);
        Assert.NotEqual(SessionIdArg(invocations[0], "--session-id"), SessionIdArg(invocations[1], "--session-id"));

        Assert.Contains("Build it", cli.Stdin);
        Assert.Contains("Reporting the stage outcome", cli.Stdin);
        Assert.Equal(Path.GetFullPath(dir.Path).TrimEnd('\\', '/'), cli.WorkingDirectory.TrimEnd('\\', '/'), ignoreCase: true);

        var metadata = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "plan", "backend.json")))!;
        Assert.Equal("copilot-cli", metadata["backend"]!.GetValue<string>());
        Assert.Equal("gpt-5-mini", metadata["model"]!.GetValue<string>());
        Assert.Equal(2, metadata["tool_calls"]!.GetValue<int>());
        Assert.Equal(1, metadata["tool_errors"]!.GetValue<int>());
        Assert.Equal(2.0, metadata["premium_requests"]!.GetValue<double>());
        var logs = Pipelines.Logs(events, "plan");
        Assert.Contains(logs, l => l.StartsWith("→ create hello.txt", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("✗ bash: command failed", StringComparison.Ordinal));
        var recorded = File.ReadAllText(dir.Combine("run", "plan", "cli-events.jsonl"));
        Assert.Contains("tool.execution_start", recorded);
        Assert.DoesNotContain("assistant.message_delta", recorded);
    }

    [Fact]
    public async Task Copilot_cli_full_fidelity_thread_resumes_the_same_session()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), CopilotOutput.Replace("\"sessionId\":\"5f0c1d2e-0000-4000-8000-000000000001\",", "", StringComparison.Ordinal));
        await using var backend = new CopilotCliBackend(new CopilotCliOptions { Executable = cli.Executable });

        var result = await Pipelines.Runner(backend).RunAsync(TwoStages.Replace("graph [goal=\"Ship it\"]", "graph [goal=\"Ship it\", default_fidelity=\"full\"]", StringComparison.Ordinal), Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        var invocations = cli.Invocations;
        Assert.Equal(SessionIdArg(invocations[0], "--session-id"), SessionIdArg(invocations[1], "--session-id"));
        var second = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "build", "backend.json")))!;
        Assert.True(second["resumed"]!.GetValue<bool>());

        // Copilot reports cumulative premium requests per session; the resumed stage records only its own delta.
        Assert.Equal(0.0, second["premium_requests"]!.GetValue<double>());
    }

    [Fact]
    public async Task Copilot_cli_permanent_errors_fail_without_retries()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), "", exitCode: 1, stderr: "Error: Model \"nope\" from --model flag is not available.");
        await using var backend = new CopilotCliBackend(new CopilotCliOptions { Executable = cli.Executable });

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan", llm_model="nope", max_retries=2]
              start -> plan -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Single(cli.Invocations);
        var outcome = result.NodeOutcomes["plan"];
        Assert.Equal(StageStatus.Fail, outcome.Status);
        Assert.Contains("is not available", outcome.FailureReason);
        Assert.Contains("is not available", File.ReadAllText(dir.Combine("run", "plan", "stderr.log")));
    }

    [Fact]
    public async Task Copilot_cli_transient_errors_are_retried()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), "", exitCode: 1, stderr: "Error: connection reset by peer");
        await using var backend = new CopilotCliBackend(new CopilotCliOptions { Executable = cli.Executable });

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan", max_retries=1]
              start -> plan -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(2, cli.Invocations.Count);
        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["plan"].Status);
    }

    [Fact]
    public async Task Missing_executable_fails_with_guidance()
    {
        using var dir = new TempDir();
        await using var backend = new CopilotCliBackend(new CopilotCliOptions { Executable = dir.Combine("does-not-exist", "copilot") });

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan", max_retries=2]
              start -> plan -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Equal(StageStatus.Fail, result.NodeOutcomes["plan"].Status);
        Assert.Contains("was not found", result.NodeOutcomes["plan"].FailureReason);
        Assert.Contains("REACTOR_COPILOT_CLI", result.NodeOutcomes["plan"].FailureReason);
    }

    [Fact]
    public async Task Claude_code_stage_uses_session_id_then_resume_on_a_thread()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), ClaudeOutput);
        await using var backend = new ClaudeCodeBackend(new ClaudeCodeOptions { Executable = cli.Executable });
        var events = new List<PipelineEvent>();

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              graph [default_fidelity="full"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan", llm_model="claude-sonnet-4.5", reasoning_effort="xhigh"]
              build [prompt="Build", llm_model="gpt-5.2"]
              start -> plan -> build -> exit
            }
            """, Pipelines.Options(dir, events), Ct);

        Assert.Equal(StageStatus.Success, result.Status);
        var invocations = cli.Invocations;
        Assert.Equal(2, invocations.Count);
        Assert.StartsWith("-p --output-format stream-json --verbose --dangerously-skip-permissions --session-id ", invocations[0]);
        Assert.Contains("--model claude-sonnet-4-5", invocations[0]);
        Assert.Contains("--effort high", invocations[0]);
        Assert.Contains("--add-dir", invocations[0]);

        // The CLI reported session id "S", which the thread adopts for the resume.
        Assert.Contains("--resume S", invocations[1]);
        Assert.DoesNotContain("--model", invocations[1]);
        Assert.Contains(Pipelines.Logs(events, "build"), l => l.Contains("is not a Claude model", StringComparison.Ordinal));

        Assert.Equal("DONE", File.ReadAllText(dir.Combine("run", "plan", "response.md")));
        var metadata = JsonNode.Parse(File.ReadAllText(dir.Combine("run", "plan", "backend.json")))!;
        Assert.Equal("claude-code", metadata["backend"]!.GetValue<string>());
        Assert.Equal(0.0123, metadata["cost_usd"]!.GetValue<double>());
        Assert.Equal(100, metadata["input_tokens"]!.GetValue<int>());
        Assert.Equal(2, metadata["tool_calls"]!.GetValue<int>());
        Assert.Equal(1, metadata["tool_errors"]!.GetValue<int>());
        Assert.Contains(Pipelines.Logs(events, "plan"), l => l.StartsWith("→ Write claude.txt", StringComparison.Ordinal));
        Assert.Contains(Pipelines.Logs(events, "plan"), l => l.StartsWith("✗ Bash: tests failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Claude_code_not_logged_in_is_a_permanent_failure()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), ClaudeNotLoggedIn, exitCode: 1);
        await using var backend = new ClaudeCodeBackend(new ClaudeCodeOptions { Executable = cli.Executable });

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan", max_retries=3]
              start -> plan -> exit
            }
            """, Pipelines.Options(dir), Ct);

        Assert.Single(cli.Invocations);
        Assert.Contains("Not logged in", result.NodeOutcomes["plan"].FailureReason);
    }

    [Fact]
    public async Task A_session_created_by_a_failed_run_is_resumed_on_the_thread()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), ClaudeNotLoggedIn, exitCode: 1);
        await using var backend = new ClaudeCodeBackend(new ClaudeCodeOptions { Executable = cli.Executable });

        await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              graph [default_fidelity="full"]
              start [shape=Mdiamond]
              exit [shape=Msquare]
              first [prompt="One"]
              second [prompt="Two"]
              start -> first -> second -> exit
            }
            """, Pipelines.Options(dir), Ct);

        var invocations = cli.Invocations;
        Assert.Equal(2, invocations.Count);
        Assert.Contains("--session-id", invocations[0]);
        Assert.Contains("--resume S", invocations[1]);
    }

    [Fact]
    public async Task Cancellation_kills_the_cli_process_tree()
    {
        using var dir = new TempDir();
        var cli = FakeCli.Create(dir.Combine("cli"), CopilotOutput, sleepSeconds: 60);
        await using var backend = new CopilotCliBackend(new CopilotCliOptions { Executable = cli.Executable });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(1.5));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Pipelines.Runner(backend).RunAsync("""
            digraph G {
              start [shape=Mdiamond]
              exit [shape=Msquare]
              plan [prompt="Plan"]
              start -> plan -> exit
            }
            """, Pipelines.Options(dir), cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }
}

public sealed class CliParserTests
{
    private static readonly List<(string Message, string Level)> Logs = [];

    private static void Log(string message, string level)
    {
        lock (Logs)
        {
            Logs.Add((message, level));
        }
    }

    [Fact]
    public void Copilot_parser_extracts_final_text_tools_usage_and_session()
    {
        var parser = new CopilotOutputParser(logAssistantText: true);
        foreach (var line in CliBackendTests.CopilotOutput.Split('\n'))
        {
            parser.ProcessLine(line.Trim(), Log);
        }

        var summary = parser.Summary;
        Assert.True(summary.Completed);
        Assert.False(summary.IsError);
        Assert.Equal("DONE.", summary.Text);
        Assert.Equal("5f0c1d2e-0000-4000-8000-000000000001", summary.SessionId);
        Assert.Equal("gpt-5-mini", summary.Model);
        Assert.Equal(2, summary.ToolCalls);
        Assert.Equal(1, summary.ToolErrors);
        Assert.Equal(2.0, summary.Metadata["premium_requests_cumulative"]);
        Assert.False(parser.ShouldRecord("""{"type":"assistant.message_delta","data":{}}"""));
        Assert.True(parser.ShouldRecord("""{"type":"assistant.message","data":{}}"""));
    }

    [Fact]
    public void Copilot_parser_flags_session_errors_and_ignores_noise()
    {
        var parser = new CopilotOutputParser(logAssistantText: false);
        parser.ProcessLine("not json", Log);
        parser.ProcessLine("{broken", Log);
        parser.ProcessLine("""{"type":"session.error","data":{"message":"rate limited"}}""", Log);

        Assert.True(parser.Summary.IsError);
        Assert.Equal("rate limited", parser.Summary.ErrorMessage);
        Assert.False(parser.Summary.Completed);
    }

    [Fact]
    public void Claude_parser_extracts_result_and_error_state()
    {
        var parser = new ClaudeOutputParser(logAssistantText: true);
        foreach (var line in CliBackendTests.ClaudeOutput.Split('\n'))
        {
            parser.ProcessLine(line.Trim(), Log);
        }

        Assert.True(parser.Summary.Completed);
        Assert.Equal("DONE", parser.Summary.Text);
        Assert.Equal("S", parser.Summary.SessionId);
        Assert.Equal("claude-sonnet-4-5-20250929", parser.Summary.Model);

        var failed = new ClaudeOutputParser(logAssistantText: true);
        foreach (var line in CliBackendTests.ClaudeNotLoggedIn.Split('\n'))
        {
            failed.ProcessLine(line.Trim(), Log);
        }

        Assert.True(failed.Summary.IsError);
        Assert.Equal("Not logged in · Please run /login", failed.Summary.ErrorMessage);
        Assert.Equal("claude-sonnet-4-5", failed.Summary.Model);
        Assert.False(CliAgentException.IsRetryableMessage(failed.Summary.ErrorMessage));
    }

    [Theory]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4.5")]
    [InlineData("claude-opus-4-6-20260101", "claude-opus-4.6")]
    [InlineData("claude-haiku-4.5", "claude-haiku-4.5")]
    [InlineData("gpt-5.2", "gpt-5.2")]
    [InlineData("", null)]
    public void Copilot_model_names_are_normalized(string input, string? expected) =>
        Assert.Equal(expected, new CopilotCliBackend().NormalizeModel(input, Log));

    [Theory]
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4-5")]
    [InlineData("claude-opus-4-6", "claude-opus-4-6")]
    [InlineData("sonnet", "sonnet")]
    [InlineData("gpt-5.2", null)]
    public void Claude_model_names_are_normalized(string input, string? expected) =>
        Assert.Equal(expected, new ClaudeCodeBackend().NormalizeModel(input, Log));

    [Theory]
    [InlineData("minimal", "minimal", "low")]
    [InlineData("xhigh", "xhigh", "high")]
    [InlineData("medium", "medium", "medium")]
    [InlineData("bogus", null, null)]
    public void Reasoning_effort_is_mapped_per_cli(string input, string? copilot, string? claude)
    {
        Assert.Equal(copilot, new CopilotCliBackend().MapReasoningEffort(input));
        Assert.Equal(claude, new ClaudeCodeBackend().MapReasoningEffort(input));
    }

    [Theory]
    [InlineData("Error: connection reset by peer", true)]
    [InlineData("HTTP 503 Service Unavailable, try again", true)]
    [InlineData("Not logged in · Please run /login", false)]
    [InlineData("Error: Model \"x\" from --model flag is not available.", false)]
    [InlineData("401 Unauthorized", false)]
    [InlineData(null, true)]
    public void Cli_errors_are_classified(string? message, bool retryable) =>
        Assert.Equal(retryable, CliAgentException.IsRetryableMessage(message));
}

public sealed class CommandResolverTests
{
    [Fact]
    public void Npm_shims_are_unwrapped_to_node_and_script()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("npm .cmd shims are Windows-only.");
        }

        using var dir = new TempDir();
        var script = dir.Combine("node_modules", "@scope", "tool", "loader.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "console.log('hi')");
        File.WriteAllText(dir.Combine("node.exe"), "");
        File.WriteAllText(dir.Combine("tool.cmd"), """
            @ECHO off
            endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@scope\tool\loader.js" %*
            """);

        var resolved = CommandResolver.Resolve("tool", dir.Path);

        Assert.NotNull(resolved);
        Assert.Equal(dir.Combine("node.exe"), resolved.FileName, ignoreCase: true);
        Assert.Equal([script], resolved.PrefixArguments);
        Assert.False(resolved.ViaCmd);
    }

    [Fact]
    public void Plain_batch_files_run_through_cmd()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Batch files are Windows-only.");
        }

        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("thing.cmd"), "@echo hi");

        var resolved = CommandResolver.Resolve("thing", dir.Path);

        Assert.NotNull(resolved);
        Assert.True(resolved.ViaCmd);
        var startInfo = CommandResolver.CreateStartInfo(resolved, ["--flag", "a b"]);
        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--flag \"a b\"", startInfo.Arguments);
    }

    [Fact]
    public void Unknown_commands_resolve_to_null()
    {
        using var dir = new TempDir();
        Assert.Null(CommandResolver.Resolve("definitely-not-a-real-command-xyz", dir.Path));
    }

    [Fact]
    public void Executables_resolve_by_absolute_path()
    {
        using var dir = new TempDir();
        var name = OperatingSystem.IsWindows() ? "tool.exe" : "tool";
        File.WriteAllText(dir.Combine(name), "");

        var resolved = CommandResolver.Resolve(dir.Combine(name));

        Assert.NotNull(resolved);
        Assert.Equal(dir.Combine(name), resolved.FileName);
        Assert.Empty(resolved.PrefixArguments);
    }
}

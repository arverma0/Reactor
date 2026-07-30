using System.Text;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Tests.Support;

namespace Reactor.Tests.Handlers;

public class ToolHandlerTests
{
    [Fact]
    public async Task Commands_are_asked_for_plain_output_unless_the_node_allows_color()
    {
        var print = OperatingSystem.IsWindows() ? "echo [%NO_COLOR%][%FORCE_COLOR%][%TERM%]" : "echo \"[$NO_COLOR][$FORCE_COLOR][$TERM]\"";
        var source = $$"""
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                plain   [shape=parallelogram, tool_command="{{Dot(print)}}"]
                colored [shape=parallelogram, tool_command="{{Dot(print)}}", tool_color=true]
                start -> plain -> colored -> exit
            }
            """;
        using var dir = new TempDir();

        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("[1][0][dumb]", result.NodeOutcomes["plain"].ContextUpdates["tool.output"]);
        string Inherited(string name) => Environment.GetEnvironmentVariable(name) ?? (OperatingSystem.IsWindows() ? $"%{name}%" : "");
        Assert.Equal($"[{Inherited("NO_COLOR")}][{Inherited("FORCE_COLOR")}][{Inherited("TERM")}]", result.NodeOutcomes["colored"].ContextUpdates["tool.output"]);
    }

    [Fact]
    public async Task Escape_sequences_are_stripped_from_recorded_output_and_the_raw_output_is_kept()
    {
        var colored = OperatingSystem.IsWindows() ? "echo \u001b[31mred\u001b[0m plain" : "printf '\\033[31mred\\033[0m plain\\n'";
        var source = $$"""
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                colored [shape=parallelogram, tool_command="{{Dot(colored)}}", tool_color=true, outputs="report"]
                plain   [shape=parallelogram, tool_command="echo plain"]
                start -> colored -> plain -> exit
            }
            """;
        using var dir = new TempDir();

        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir));

        Assert.Equal("red plain", result.NodeOutcomes["colored"].ContextUpdates["tool.output"]);
        var report = Assert.IsType<Dictionary<string, object?>>(result.Context[ArtifactPins.ContextKey("report")]);
        Assert.Equal("red plain", report["text"]);
        Assert.Equal("red plain\n", File.ReadAllText(Path.Combine(result.LogsRoot, "colored", "stdout.txt")));
        Assert.Contains("\u001b[31mred\u001b[0m plain", File.ReadAllText(Path.Combine(result.LogsRoot, "colored", "stdout.raw.txt")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(result.LogsRoot, "colored", "stderr.raw.txt")));
        Assert.False(File.Exists(Path.Combine(result.LogsRoot, "plain", "stdout.raw.txt")));
    }

    [Fact]
    public async Task Failure_reasons_quote_the_failing_line_from_the_middle_of_a_long_log()
    {
        var command = OperatingSystem.IsWindows()
            ? "(for /L %i in (1,1,300) do @echo PASS  check %i: layout within limits for every system) & echo FAIL  QA-15@390x844  Composition: lit bbox too large & (for /L %i in (301,1,600) do @echo PASS  check %i: layout within limits for every system) & echo QA FAIL - report: qa/report.json & exit 1"
            : "for i in $(seq 1 300); do echo \"PASS  check $i: layout within limits for every system\"; done; echo 'FAIL  QA-15@390x844  Composition: lit bbox too large'; for i in $(seq 301 600); do echo \"PASS  check $i: layout within limits for every system\"; done; echo 'QA FAIL - report: qa/report.json'; exit 1";
        var source = $$"""
            digraph T {
                start [shape=Mdiamond] exit [shape=Msquare]
                qa [shape=parallelogram, tool_command="{{Dot(command)}}"]
                start -> qa -> exit
            }
            """;
        using var dir = new TempDir();

        var result = await TestPipelines.Runner().RunAsync(source, TestPipelines.Options(dir));

        var reason = result.NodeOutcomes["qa"].FailureReason;
        Assert.StartsWith("Tool command exited with code 1: ", reason, StringComparison.Ordinal);
        Assert.Contains("FAIL  QA-15@390x844  Composition: lit bbox too large", reason, StringComparison.Ordinal);
        Assert.Contains("QA FAIL - report: qa/report.json", reason, StringComparison.Ordinal);
        Assert.True(reason.Length < 1600, $"{reason.Length} characters");
    }

    private static string Dot(string text) => new StringBuilder(text).Replace("\\", "\\\\").Replace("\"", "\\\"").ToString();
}

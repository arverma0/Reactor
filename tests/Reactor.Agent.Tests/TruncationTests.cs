namespace Reactor.Agent.Tests;

public class TruncationTests
{
    private const string HeadTailMarkerTemplate =
        "\n\n[WARNING: Tool output was truncated. {0} characters were removed from the middle. The full output is available in the event stream. If you need to see specific parts, re-run the tool with more targeted parameters.]\n\n";

    private const string TailMarkerTemplate =
        "[WARNING: Tool output was truncated. First {0} characters were removed. The full output is available in the event stream.]\n\n";

    [Fact]
    public void Output_within_limit_is_unchanged()
    {
        Assert.Equal("hello", ToolOutputTruncation.TruncateOutput("hello", 5, ToolOutputTruncation.HeadTail));
        Assert.Equal("hello", ToolOutputTruncation.TruncateOutput("hello", 100, ToolOutputTruncation.Tail));
        Assert.Equal("a\nb", ToolOutputTruncation.TruncateLines("a\nb", 2));
    }

    [Fact]
    public void Head_tail_keeps_both_ends_with_exact_marker()
    {
        var output = new string('a', 60) + new string('b', 40);

        var result = ToolOutputTruncation.TruncateOutput(output, 50, ToolOutputTruncation.HeadTail);

        Assert.Equal(new string('a', 25) + string.Format(HeadTailMarkerTemplate, 50) + new string('b', 25), result);
    }

    [Fact]
    public void Tail_mode_keeps_the_end_with_exact_marker()
    {
        var output = new string('a', 60) + new string('b', 40);

        var result = ToolOutputTruncation.TruncateOutput(output, 50, ToolOutputTruncation.Tail);

        Assert.Equal(string.Format(TailMarkerTemplate, 50) + new string('a', 10) + new string('b', 40), result);
    }

    [Fact]
    public void Line_truncation_keeps_head_and_tail_lines_with_exact_marker()
    {
        var output = string.Join("\n", Enumerable.Range(1, 10).Select(i => "line" + i));

        var result = ToolOutputTruncation.TruncateLines(output, 4);

        Assert.Equal("line1\nline2\n[... 6 lines omitted ...]\nline9\nline10", result);
    }

    [Fact]
    public void Odd_line_limits_give_the_extra_line_to_the_tail()
    {
        var output = string.Join("\n", Enumerable.Range(1, 6).Select(i => i.ToString()));

        Assert.Equal("1\n[... 3 lines omitted ...]\n5\n6", ToolOutputTruncation.TruncateLines(output, 3));
    }

    [Fact]
    public void Two_huge_lines_are_caught_by_character_truncation_first()
    {
        var output = new string('x', 100_000) + "\n" + new string('y', 100_000);

        var result = ToolOutputTruncation.TruncateToolOutput(output, "shell", new SessionConfig());

        Assert.Contains("characters were removed from the middle", result);
        Assert.Contains("170001 characters were removed", result);
        Assert.StartsWith(new string('x', 15_000), result);
        Assert.EndsWith(new string('y', 15_000), result);
        Assert.True(result.Length < 30_000 + 400, $"length was {result.Length}");
        Assert.DoesNotContain("lines omitted", result);
    }

    [Fact]
    public void Line_truncation_runs_after_character_truncation()
    {
        var output = string.Join("\n", Enumerable.Range(1, 1000).Select(i => "l" + i));

        var result = ToolOutputTruncation.TruncateToolOutput(output, "shell", new SessionConfig());

        Assert.DoesNotContain("WARNING", result);
        Assert.Contains("[... 744 lines omitted ...]", result);
        Assert.Equal(257, result.Split('\n').Length);
    }

    [Fact]
    public void Character_marker_lines_are_counted_by_the_line_pass()
    {
        // 40k chars over 2000 lines: character truncation leaves ~30k chars (still > 256 lines) plus the marker,
        // then the line pass trims to 256 lines.
        var output = string.Join("\n", Enumerable.Range(1, 2000).Select(i => i.ToString("D19")));

        var result = ToolOutputTruncation.TruncateToolOutput(output, "shell", new SessionConfig());

        Assert.Contains("lines omitted", result);
        Assert.StartsWith("0000000000000000001\n", result);
        Assert.EndsWith("0000000000000002000", result);
    }

    [Theory]
    [InlineData("read_file", 50_000, ToolOutputTruncation.HeadTail)]
    [InlineData("shell", 30_000, ToolOutputTruncation.HeadTail)]
    [InlineData("grep", 20_000, ToolOutputTruncation.Tail)]
    [InlineData("glob", 20_000, ToolOutputTruncation.Tail)]
    [InlineData("edit_file", 10_000, ToolOutputTruncation.Tail)]
    [InlineData("apply_patch", 10_000, ToolOutputTruncation.Tail)]
    [InlineData("write_file", 1_000, ToolOutputTruncation.Tail)]
    [InlineData("spawn_agent", 20_000, ToolOutputTruncation.HeadTail)]
    public void Default_limits_and_modes_match_the_spec(string tool, int limit, string mode)
    {
        Assert.Equal(limit, ToolOutputTruncation.DefaultCharLimits[tool]);
        Assert.Equal(mode, ToolOutputTruncation.DefaultModes[tool]);
    }

    [Fact]
    public void Default_line_limits_match_the_spec()
    {
        Assert.Equal(256, ToolOutputTruncation.DefaultLineLimits["shell"]);
        Assert.Equal(200, ToolOutputTruncation.DefaultLineLimits["grep"]);
        Assert.Equal(500, ToolOutputTruncation.DefaultLineLimits["glob"]);
        Assert.False(ToolOutputTruncation.DefaultLineLimits.ContainsKey("read_file"));
        Assert.False(ToolOutputTruncation.DefaultLineLimits.ContainsKey("edit_file"));
    }

    [Fact]
    public void Grep_output_uses_tail_mode()
    {
        var output = new string('a', 10_000) + new string('b', 20_000);

        var result = ToolOutputTruncation.TruncateToolOutput(output, "grep", new SessionConfig());

        Assert.Equal(string.Format(TailMarkerTemplate, 10_000) + new string('b', 20_000), result);
    }

    [Fact]
    public void Read_file_has_no_line_limit()
    {
        var output = string.Join("\n", Enumerable.Range(1, 5000).Select(i => "x"));

        Assert.Equal(output, ToolOutputTruncation.TruncateToolOutput(output, "read_file", new SessionConfig()));
    }

    [Fact]
    public void Config_overrides_character_and_line_limits()
    {
        var config = new SessionConfig();
        config.ToolOutputLimits["grep"] = 10;
        config.ToolLineLimits["read_file"] = 2;

        var grep = ToolOutputTruncation.TruncateToolOutput("0123456789ABCDEF", "grep", config);
        var read = ToolOutputTruncation.TruncateToolOutput("1\n2\n3\n4", "read_file", config);

        Assert.Equal(string.Format(TailMarkerTemplate, 6) + "6789ABCDEF", grep);
        Assert.Equal("1\n[... 2 lines omitted ...]\n4", read);
    }

    [Fact]
    public void Unknown_tools_use_the_fallback_limit_in_head_tail_mode()
    {
        var output = new string('z', ToolOutputTruncation.FallbackCharLimit + 10);

        var result = ToolOutputTruncation.TruncateToolOutput(output, "my_custom_tool", new SessionConfig());

        Assert.Contains("10 characters were removed from the middle", result);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var output = string.Concat(Enumerable.Repeat("😀", 50));

        var result = ToolOutputTruncation.TruncateOutput(output, 51, ToolOutputTruncation.HeadTail);

        Assert.False(char.IsHighSurrogate(result[result.IndexOf("\n\n[WARNING", StringComparison.Ordinal) - 1]));
        Assert.False(char.IsLowSurrogate(result[result.LastIndexOf("]\n\n", StringComparison.Ordinal) + 3]));
    }
}

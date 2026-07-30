using Reactor.Execution;

namespace Reactor.Tests.Execution;

public class AnsiTextTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData("\u001b[31mred\u001b[0m", "red")]
    [InlineData("\u001b[1;38;2;255;0;0mbold\u001b[22;39m", "bold")] // truecolor, several parameters
    [InlineData("\u001b[?25lhidden cursor\u001b[?25h", "hidden cursor")] // private marker
    [InlineData("a\u001b[2Kb\u001b[1Ac", "abc")] // erase line, cursor up
    [InlineData("x\u001b[2 qy", "xy")] // intermediate byte (cursor style)
    [InlineData("\u009b31mC1 CSI\u009b0m", "C1 CSI")]
    [InlineData("\u001b]0;window title\u0007text", "text")] // OSC terminated by BEL
    [InlineData("\u001b]8;;https://example.com\u001b\\link\u001b]8;;\u001b\\", "link")] // OSC 8 hyperlink, terminated by ST
    [InlineData("\u009d0;title\u009cafter", "after")] // C1 OSC and ST
    [InlineData("\u001bPq#0;2;0;0;0\u001b\\done", "done")] // DCS
    [InlineData("\u001b(Bascii\u001b)0", "ascii")] // nF: character set selection
    [InlineData("\u001b7saved\u001b8", "saved")] // save/restore cursor
    [InlineData("\u001bcreset", "reset")] // full reset
    [InlineData("trailing\u001b", "trailing")]
    [InlineData("cut \u001b[31", "cut ")] // truncated CSI
    public void Escape_sequences_are_removed(string input, string expected) => Assert.Equal(expected, AnsiText.Strip(input));

    [Theory]
    [InlineData("\u001b]0;never terminated\nnext line", "\nnext line")]
    [InlineData("\u001b]0;title\u001b[31mred", "red")]
    public void Unterminated_control_strings_end_at_the_line_end_or_the_next_escape(string input, string expected) =>
        Assert.Equal(expected, AnsiText.Strip(input));

    [Theory]
    [InlineData("10%\r50%\r100%\n", "100%\n")]
    [InlineData("Downloading\r\u001b[KDone\nnext", "Done\nnext")]
    [InlineData("line one\r\nline two\r\n", "line one\nline two\n")]
    [InlineData("doubled\r\r\nend\r", "doubled\nend")]
    [InlineData("colored\r\u001b[0m\r\nnext", "colored\nnext")] // a CR before a reset and the line end ends the line
    [InlineData("first\nprogress 1\rprogress 2\nlast", "first\nprogress 2\nlast")]
    public void Carriage_returns_keep_the_last_state_of_a_line(string input, string expected) => Assert.Equal(expected, AnsiText.Strip(input));

    [Theory]
    [InlineData("ab\bc", "ac")]
    [InlineData("\bstart", "start")]
    [InlineData("line\n\bnext", "line\nnext")] // a backspace does not cross lines
    [InlineData("bell\u0007 nul\0 del\u007f", "bell nul del")]
    [InlineData("tab\tkept\n", "tab\tkept\n")]
    [InlineData("h\0i\0", "hi")] // UTF-16 output decoded as UTF-8
    public void Other_control_characters_are_applied_or_dropped(string input, string expected) => Assert.Equal(expected, AnsiText.Strip(input));

    [Fact]
    public void Plain_text_is_returned_unchanged()
    {
        const string text = "PASS  src/a.test.ts\n✓ unicode — “quotes” ✗ ×\n";

        Assert.Same(text, AnsiText.Strip(text));
        Assert.Equal("", AnsiText.Strip(null));
        Assert.Equal("", AnsiText.Strip(""));
    }

    [Theory]
    [InlineData("plain\nlines\n", false)]
    [InlineData("windows\r\nline endings\r\n", false)]
    [InlineData("trailing cr\r", false)]
    [InlineData("\u001b[32mgreen\u001b[0m", true)]
    [InlineData("progress\rdone", true)]
    [InlineData("back\bspace", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Needs_stripping_ignores_line_endings(string? text, bool expected) => Assert.Equal(expected, AnsiText.NeedsStripping(text));

    [Fact]
    public void Colored_vitest_output_reads_cleanly()
    {
        var raw = $" {Esc}[31m❯{Esc}[39m src/seeds.test.ts {Esc}[2m({Esc}[22m{Esc}[2m12 tests{Esc}[22m{Esc}[2m | {Esc}[22m{Esc}[31m1 failed{Esc}[39m{Esc}[2m){Esc}[22m\r\n" +
                  $"{Esc}[90m     24|{Esc}[39m       {Esc}[35mconst{Esc}[39m env {Esc}[33m={Esc}[39m envelope(sys{Esc}[33m.{Esc}[39mid){Esc}[33m;{Esc}[39m\r\n";

        Assert.Equal(" ❯ src/seeds.test.ts (12 tests | 1 failed)\n     24|       const env = envelope(sys.id);\n", AnsiText.Strip(raw));
    }
}

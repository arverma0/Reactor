using System.Text;
using System.Text.Json.Nodes;
using Reactor.Agent.Tests.Environments;
using Reactor.Agent.Tools;

namespace Reactor.Agent.Tests.Tools;

public class ReadWriteToolTests
{
    private static string Numbered(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => "line " + i)) + "\n";

    [Fact]
    public async Task Read_file_numbers_lines_right_aligned()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", Numbered(12));

        var output = await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt"}""");

        var lines = output.Text.Split('\n');
        Assert.Equal(12, lines.Length);
        Assert.Equal(" 1 | line 1", lines[0]);
        Assert.Equal(" 9 | line 9", lines[8]);
        Assert.Equal("12 | line 12", lines[11]);
        Assert.Null(output.ImageData);
    }

    [Fact]
    public async Task Read_file_pages_with_offset_and_limit_and_explains_how_to_continue()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", Numbered(12));

        var window = await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","offset":3,"limit":2}""");
        var tail = await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","offset":10}""");

        Assert.Equal("3 | line 3\n4 | line 4\n[... 8 more lines. Use offset=5 to continue reading.]", window.Text);
        Assert.Equal("10 | line 10\n11 | line 11\n12 | line 12", tail.Text);
    }

    [Fact]
    public async Task Read_file_defaults_to_2000_lines()
    {
        await using var ws = new TestWorkspace();
        ws.Write("big.txt", Numbered(2500));

        var output = (await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"big.txt"}""")).Text;

        var lines = output.Split('\n');
        Assert.Equal("   1 | line 1", lines[0]);
        Assert.Equal("2000 | line 2000", lines[1999]);
        Assert.Equal("[... 500 more lines. Use offset=2001 to continue reading.]", lines[2000]);
    }

    [Fact]
    public async Task Read_file_accepts_in_memory_and_string_numbers()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", Numbered(12));
        const string expected = "3 | line 3\n4 | line 4\n[... 8 more lines. Use offset=5 to continue reading.]";

        Assert.Equal(expected, (await ws.RunAsync(CoreTools.ReadFile(), Args.Of(("file_path", "a.txt"), ("offset", 3), ("limit", 2)))).Text);
        Assert.Equal(expected, (await ws.RunAsync(CoreTools.ReadFile(), Args.Of(("file_path", "a.txt"), ("offset", 3L), ("limit", 2.0)))).Text);
        Assert.Equal(expected, (await ws.RunAsync(CoreTools.ReadFile(), Args.Of(("file_path", "a.txt"), ("offset", 3m), ("limit", "2")))).Text);
        Assert.Equal(expected, (await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","offset":3.0,"limit":"2"}""")).Text);
        Assert.Equal(expected, (await ws.RunAsync(CoreTools.ReadFile(), new JsonObject { ["file_path"] = "a.txt", ["offset"] = 3, ["limit"] = 2 })).Text);
    }

    [Fact]
    public async Task Read_file_validates_offset_and_limit()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", Numbered(12));

        var past = await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","offset":13}"""));
        Assert.Contains("12 line(s)", past.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","offset":-1}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","limit":0}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","limit":"lots"}"""));
        Assert.StartsWith("1 | line 1", (await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"a.txt","offset":0,"limit":1}""")).Text.Trim());
    }

    [Fact]
    public async Task Read_file_cuts_very_long_lines()
    {
        await using var ws = new TestWorkspace();
        ws.Write("min.js", new string('a', 2500) + "\nshort\n");

        var output = (await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"min.js"}""")).Text;

        var first = output.Split('\n')[0];
        Assert.Equal("1 | " + new string('a', 2000) + "... [line truncated]", first);
        Assert.EndsWith("2 | short", output);
    }

    [Fact]
    public async Task Read_file_handles_empty_crlf_and_bom_files()
    {
        await using var ws = new TestWorkspace();
        ws.Write("empty.txt", "");
        ws.Write("crlf.txt", "a\r\nb\r\n");
        ws.WriteBytes("bom.txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("x")]);

        Assert.Equal("(empty file)", (await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"empty.txt"}""")).Text);
        Assert.Equal("1 | a\n2 | b", (await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"crlf.txt"}""")).Text);
        Assert.Equal("1 | x", (await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"bom.txt"}""")).Text);
    }

    [Fact]
    public async Task Read_file_returns_images_as_image_data()
    {
        await using var ws = new TestWorkspace();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        var path = ws.WriteBytes("pic.PNG", png);
        ws.WriteBytes("photo.jpeg", [0xFF, 0xD8, 0xFF]);

        var output = await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"pic.PNG"}""");
        var jpeg = await ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"photo.jpeg"}""");

        Assert.Equal($"Image file: {path} (12 bytes)", output.Text);
        Assert.Equal(png, output.ImageData);
        Assert.Equal("image/png", output.ImageMediaType);
        Assert.Equal("image/jpeg", jpeg.ImageMediaType);
    }

    [Fact]
    public async Task Read_file_reports_binary_missing_and_directory_errors()
    {
        await using var ws = new TestWorkspace();
        ws.WriteBytes("prog.exe", [0x4D, 0x5A, 0, 0, 1]);
        ws.MakeDirectory("dir");

        var binary = await Assert.ThrowsAsync<InvalidDataException>(() => ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"prog.exe"}"""));
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"nope.txt"}"""));
        var directory = await Assert.ThrowsAsync<IOException>(() => ws.RunAsync(CoreTools.ReadFile(), """{"file_path":"dir"}"""));
        var noPath = await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ReadFile(), """{}"""));

        Assert.StartsWith("Cannot display binary file", binary.Message);
        Assert.Equal($"File not found: {ws.PathOf("nope.txt")}", missing.Message);
        Assert.Contains("is a directory", directory.Message);
        Assert.Equal("Missing required parameter 'file_path'.", noPath.Message);
    }

    [Fact]
    public async Task Read_file_accepts_absolute_paths()
    {
        await using var ws = new TestWorkspace();
        var path = ws.Write("sub/a.txt", "hello\n");

        var output = await ws.RunAsync(CoreTools.ReadFile(), Args.Of(("file_path", path)));

        Assert.Equal("1 | hello", output.Text);
    }

    [Fact]
    public async Task Write_file_creates_files_and_directories()
    {
        await using var ws = new TestWorkspace();

        var output = await ws.RunAsync(CoreTools.WriteFile(), Args.Of(("file_path", "new/dir/héllo.txt"), ("content", "héllo\n")));

        Assert.Equal($"Wrote 7 bytes to {ws.PathOf("new/dir/héllo.txt")} (created)", output.Text);
        Assert.Equal("héllo\n", ws.ReadText("new/dir/héllo.txt"));
        Assert.Equal(7, ws.ReadBytes("new/dir/héllo.txt").Length);
    }

    [Fact]
    public async Task Write_file_reports_overwrites_and_allows_empty_content()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "old");

        var output = await ws.RunAsync(CoreTools.WriteFile(), """{"file_path":"a.txt","content":""}""");

        Assert.Equal($"Wrote 0 bytes to {ws.PathOf("a.txt")} (overwrote existing file)", output.Text);
        Assert.Equal("", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Write_file_rejects_directories_and_missing_content()
    {
        await using var ws = new TestWorkspace();
        ws.MakeDirectory("dir");

        await Assert.ThrowsAsync<IOException>(() => ws.RunAsync(CoreTools.WriteFile(), """{"file_path":"dir","content":"x"}"""));
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.WriteFile(), """{"file_path":"x.txt"}"""));
        var wrongType = await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.WriteFile(), """{"file_path":"x.txt","content":5}"""));

        Assert.Equal("Missing required parameter 'content'.", missing.Message);
        Assert.Equal("Parameter 'content' must be a string.", wrongType.Message);
        Assert.False(ws.Exists("x.txt"));
    }
}

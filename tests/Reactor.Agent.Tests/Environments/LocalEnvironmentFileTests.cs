using System.Text;
using Reactor.Agent.Environments;

namespace Reactor.Agent.Tests.Environments;

public class LocalEnvironmentFileTests
{
    [Fact]
    public async Task Metadata_reflects_the_current_platform()
    {
        await using var ws = new TestWorkspace();
        var env = ws.Environment;

        var expected = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        Assert.Equal(expected, env.Platform);
        Assert.False(string.IsNullOrWhiteSpace(env.OsVersion));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", env.ShellName);
        }
        else
        {
            Assert.True(env.ShellName is "/bin/bash" or "/bin/sh", env.ShellName);
        }
    }

    [Theory]
    [InlineData(ShellKind.Cmd, "cmd.exe")]
    [InlineData(ShellKind.PowerShell, "powershell.exe")]
    [InlineData(ShellKind.Pwsh, "pwsh")]
    public void Shell_names_follow_the_configured_shell(ShellKind kind, string expected)
    {
        var env = new LocalExecutionEnvironment(new LocalEnvironmentOptions { Shell = kind });

        Assert.Equal(expected, env.ShellName);
    }

    [Fact]
    public void Working_directory_defaults_to_the_current_directory_and_is_normalized()
    {
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory()),
            new LocalExecutionEnvironment().WorkingDirectory);

        var withSlash = Path.Combine(AppContext.BaseDirectory, "some", "dir") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "some", "dir"),
            new LocalExecutionEnvironment(new LocalEnvironmentOptions { WorkingDirectory = withSlash }).WorkingDirectory);
    }

    [Fact]
    public async Task ResolvePath_handles_relative_absolute_home_and_dot_segments()
    {
        await using var ws = new TestWorkspace();
        var env = ws.Environment;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(ws.Root, "src", "a.cs"), env.ResolvePath("src/a.cs"));
        Assert.Equal(Path.Combine(ws.Root, "a.cs"), env.ResolvePath("./src/../a.cs"));
        Assert.Equal(ws.Root, env.ResolvePath("."));
        Assert.Equal(ws.Root, env.ResolvePath(""));
        Assert.Equal(Path.GetFullPath(home), env.ResolvePath("~"));
        Assert.Equal(Path.Combine(home, "notes.txt"), env.ResolvePath("~/notes.txt"));
        var absolute = Path.Combine(Path.GetTempPath(), "x", "y.txt");
        Assert.Equal(absolute, env.ResolvePath(absolute));
        Assert.Equal(Path.GetDirectoryName(ws.Root), env.ResolvePath(".."));
    }

    [Fact]
    public async Task ReadFile_returns_lines_joined_with_newlines()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "one\r\ntwo\nthree\r\n");

        Assert.Equal("one\ntwo\nthree", await ws.Environment.ReadFileAsync("a.txt"));
    }

    [Fact]
    public async Task ReadFile_preserves_line_content_verbatim()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "\tindented  \n  trailing spaces   \n\nlast");

        Assert.Equal("\tindented  \n  trailing spaces   \n\nlast", await ws.Environment.ReadFileAsync("a.txt"));
    }

    [Fact]
    public async Task ReadFile_supports_offset_and_limit()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "1\n2\n3\n4\n5\n");
        var env = ws.Environment;

        Assert.Equal("2\n3", await env.ReadFileAsync("a.txt", offset: 2, limit: 2));
        Assert.Equal("4\n5", await env.ReadFileAsync("a.txt", offset: 4));
        Assert.Equal("1", await env.ReadFileAsync("a.txt", limit: 1));
        Assert.Equal("", await env.ReadFileAsync("a.txt", offset: 9));
        Assert.Equal("", await env.ReadFileAsync("a.txt", limit: 0));
        Assert.Equal("1\n2", await env.ReadFileAsync("a.txt", offset: 0, limit: 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => env.ReadFileAsync("a.txt", limit: -1));
    }

    [Fact]
    public async Task ReadFile_streams_windows_of_large_files()
    {
        await using var ws = new TestWorkspace();
        ws.Write("big.txt", string.Join("\n", Enumerable.Range(1, 100_000).Select(i => "line " + i)) + "\n");

        Assert.Equal("line 50000\nline 50001", await ws.Environment.ReadFileAsync("big.txt", offset: 50_000, limit: 2));
    }

    [Fact]
    public async Task ReadFile_detects_byte_order_marks()
    {
        await using var ws = new TestWorkspace();
        ws.WriteBytes("utf8bom.txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("héllo\nwörld")]);
        ws.WriteBytes("utf16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("wide\ntext")]);

        Assert.Equal("héllo\nwörld", await ws.Environment.ReadFileAsync("utf8bom.txt"));
        Assert.Equal("wide\ntext", await ws.Environment.ReadFileAsync("utf16.txt"));
    }

    [Fact]
    public async Task ReadFile_reports_missing_files_and_directories_clearly()
    {
        await using var ws = new TestWorkspace();
        ws.MakeDirectory("folder");

        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => ws.Environment.ReadFileAsync("nope.txt"));
        Assert.Contains("File not found", missing.Message);
        Assert.Contains("nope.txt", missing.Message);

        var directory = await Assert.ThrowsAsync<IOException>(() => ws.Environment.ReadFileAsync("folder"));
        Assert.Contains("directory", directory.Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ws.Environment.ReadFileBytesAsync("nope.bin"));
    }

    [Fact]
    public async Task Files_held_open_for_writing_by_another_process_can_still_be_read()
    {
        await using var ws = new TestWorkspace();
        var path = ws.Write("log.txt", "first\n");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal("first\n"u8.ToArray(), await ws.Environment.ReadFileBytesAsync("log.txt"));
        Assert.Equal("first", await ws.Environment.ReadFileAsync("log.txt"));
        Assert.Equal("log.txt:1:first", await ws.Environment.GrepAsync("first", ".", new GrepOptions()));
    }

    [Fact]
    public async Task ReadFileBytes_returns_raw_content()
    {
        await using var ws = new TestWorkspace();
        byte[] bytes = [0, 1, 2, 255, 0x0D, 0x0A];
        ws.WriteBytes("data.bin", bytes);

        Assert.Equal(bytes, await ws.Environment.ReadFileBytesAsync("data.bin"));
    }

    [Fact]
    public async Task WriteFile_creates_parent_directories_and_writes_utf8_without_bom()
    {
        await using var ws = new TestWorkspace();

        await ws.Environment.WriteFileAsync("deep/nested/dir/file.txt", "héllo\n");

        Assert.Equal(Encoding.UTF8.GetBytes("héllo\n"), ws.ReadBytes("deep/nested/dir/file.txt"));
    }

    [Fact]
    public async Task WriteFile_with_a_leading_bom_character_writes_a_utf8_bom()
    {
        await using var ws = new TestWorkspace();

        await ws.Environment.WriteFileAsync("bom.txt", "\uFEFFx");

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' }, ws.ReadBytes("bom.txt"));
    }

    [Fact]
    public async Task WriteFile_overwrites_and_rejects_directories()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "old content that is longer");
        ws.MakeDirectory("dir");

        await ws.Environment.WriteFileAsync("a.txt", "new");

        Assert.Equal("new", ws.ReadText("a.txt"));
        await Assert.ThrowsAsync<IOException>(() => ws.Environment.WriteFileAsync("dir", "x"));
    }

    [Fact]
    public async Task Exists_and_delete_behave_as_documented()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "x");
        ws.MakeDirectory("dir");
        var env = ws.Environment;

        Assert.True(await env.FileExistsAsync("a.txt"));
        Assert.False(await env.FileExistsAsync("dir"));
        Assert.True(await env.DirectoryExistsAsync("dir"));
        Assert.False(await env.DirectoryExistsAsync("a.txt"));

        await env.DeleteFileAsync("a.txt");
        Assert.False(ws.Exists("a.txt"));
        await env.DeleteFileAsync("a.txt");
        await Assert.ThrowsAsync<IOException>(() => env.DeleteFileAsync("dir"));
    }

    [Fact]
    public async Task ListDirectory_sorts_directories_first_and_reports_sizes()
    {
        await using var ws = new TestWorkspace();
        ws.Write("b.txt", "12345");
        ws.Write("A.md", "1");
        ws.MakeDirectory("zeta");
        ws.MakeDirectory("Alpha");

        var entries = await ws.Environment.ListDirectoryAsync(".", 1);

        Assert.Equal(new[] { "Alpha", "zeta", "A.md", "b.txt" }, entries.Select(e => e.Name));
        Assert.Equal(new[] { true, true, false, false }, entries.Select(e => e.IsDir));
        Assert.Null(entries[0].Size);
        Assert.Equal(1, entries[2].Size);
        Assert.Equal(5, entries[3].Size);
    }

    [Fact]
    public async Task ListDirectory_descends_to_the_requested_depth_but_not_into_git()
    {
        await using var ws = new TestWorkspace();
        ws.Write("src/app/main.cs", "x");
        ws.Write("src/readme.md", "x");
        ws.Write(".git/HEAD", "ref");
        ws.Write("top.txt", "x");

        var depth1 = await ws.Environment.ListDirectoryAsync(".", 1);
        var depth3 = await ws.Environment.ListDirectoryAsync(".", 3);

        Assert.Equal(new[] { ".git", "src", "top.txt" }, depth1.Select(e => e.Name));
        Assert.Equal(new[] { ".git", "src", "src/app", "src/app/main.cs", "src/readme.md", "top.txt" }, depth3.Select(e => e.Name));
    }

    [Fact]
    public async Task ListDirectory_reports_missing_paths_and_files()
    {
        await using var ws = new TestWorkspace();
        ws.Write("file.txt", "x");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.Environment.ListDirectoryAsync("missing", 1));
        await Assert.ThrowsAsync<IOException>(() => ws.Environment.ListDirectoryAsync("file.txt", 1));
    }

    [Fact]
    public async Task Initialize_creates_the_working_directory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "tw", "init-" + Guid.NewGuid().ToString("N")[..8]);
        var env = new LocalExecutionEnvironment(new LocalEnvironmentOptions { WorkingDirectory = root });
        try
        {
            Assert.False(Directory.Exists(root));
            await env.InitializeAsync();
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Text_decoding_splits_lines_like_editors()
    {
        Assert.Empty(TextFileDecoding.SplitLines(""));
        Assert.Equal(new[] { "" }, TextFileDecoding.SplitLines("\n"));
        Assert.Equal(new[] { "a", "b" }, TextFileDecoding.SplitLines("a\r\nb\r\n"));
        Assert.Equal(new[] { "a", "", "b" }, TextFileDecoding.SplitLines("a\n\nb"));
        Assert.Equal("\r\n", TextFileDecoding.DetectLineEnding("a\r\nb\r\nc\n"));
        Assert.Equal("\n", TextFileDecoding.DetectLineEnding("a\nb\r\nc\n"));
        Assert.Equal("a\r\nb\r\n", TextFileDecoding.NormalizeLineEndings("a\nb\r\n", "\r\n"));
        Assert.True(TextFileDecoding.IsBinary([65, 0, 66]));
        Assert.False(TextFileDecoding.IsBinary([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("ab")]));
    }

    [Fact]
    public void Display_paths_are_relative_inside_the_working_directory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "root");

        Assert.Equal("src/a.cs", PathDisplay.ForDisplay(Path.Combine(root, "src", "a.cs"), root));
        Assert.Equal(".", PathDisplay.ForDisplay(root, root));
        var outside = Path.Combine(AppContext.BaseDirectory, "rootless", "a.cs");
        Assert.Equal(outside, PathDisplay.ForDisplay(outside, root));
    }
}

using Reactor.Agent.Tests.Environments;
using Reactor.Agent.Tools;

namespace Reactor.Agent.Tests.Tools;

public class SearchToolTests
{
    private static TestWorkspace CreateTree()
    {
        var ws = new TestWorkspace();
        ws.Write("README.md", "Intro\nTODO: docs\n");
        ws.Write("src/main.py", "import os\n# TODO: refactor\ndef main():\n    pass\n# todo lowercase\n");
        ws.Write("src/lib/util.py", "# TODO util\n");
        ws.Write("src/lib/data.json", "{\"todo\": true}\n");
        return ws;
    }

    [Fact]
    public async Task Grep_returns_matching_lines_relative_to_the_working_directory()
    {
        await using var ws = CreateTree();

        var output = await ws.RunAsync(CoreTools.Grep(), """{"pattern":"TODO"}""");

        Assert.Equal("README.md:2:TODO: docs\nsrc/lib/util.py:1:# TODO util\nsrc/main.py:2:# TODO: refactor", output.Text);
    }

    [Fact]
    public async Task Grep_supports_path_glob_filter_case_insensitivity_and_max_results()
    {
        await using var ws = CreateTree();

        var scoped = await ws.RunAsync(CoreTools.Grep(), """{"pattern":"todo","path":"src","glob_filter":"*.py","case_insensitive":true}""");
        var limited = await ws.RunAsync(CoreTools.Grep(), Args.Of(("pattern", "(?i)todo"), ("max_results", 2)));

        Assert.Equal("src/lib/util.py:1:# TODO util\nsrc/main.py:2:# TODO: refactor\nsrc/main.py:5:# todo lowercase", scoped.Text);
        Assert.Equal("README.md:2:TODO: docs\nsrc/lib/data.json:1:{\"todo\": true}\n[results truncated at 2]", limited.Text);
    }

    [Fact]
    public async Task Grep_reports_no_matches_in_plain_words()
    {
        await using var ws = CreateTree();

        Assert.Equal("No matches found.", (await ws.RunAsync(CoreTools.Grep(), """{"pattern":"nowhere-to-be-found"}""")).Text);
    }

    [Fact]
    public async Task Grep_output_modes_and_context()
    {
        await using var ws = CreateTree();
        var grep = CoreTools.Grep(includeOutputModes: true);

        var files = await ws.RunAsync(grep, """{"pattern":"TODO","output_mode":"files_with_matches"}""");
        var counts = await ws.RunAsync(grep, """{"pattern":"(?i)todo","output_mode":"count","path":"src"}""");
        var context = await ws.RunAsync(grep, Args.Of(("pattern", "refactor"), ("context", 1)));

        Assert.Equal("README.md\nsrc/lib/util.py\nsrc/main.py", files.Text);
        Assert.Equal("src/lib/data.json:1\nsrc/lib/util.py:1\nsrc/main.py:2", counts.Text);
        Assert.Equal("src/main.py-1-import os\nsrc/main.py:2:# TODO: refactor\nsrc/main.py-3-def main():", context.Text);
    }

    [Fact]
    public async Task Grep_without_output_modes_ignores_mode_arguments()
    {
        await using var ws = CreateTree();
        var recorder = new RecordingEnvironment(ws.Environment);

        await ws.RunAsync(CoreTools.Grep(), """{"pattern":"TODO","output_mode":"count","context":3}""", ws.Context(environment: recorder));

        Assert.Equal("content", recorder.GrepOptions!.OutputMode);
        Assert.Equal(0, recorder.GrepOptions.ContextBefore);
        Assert.Equal(100, recorder.GrepOptions.MaxResults);
        Assert.Equal(ws.Root, recorder.GrepPath);
    }

    [Fact]
    public async Task Grep_validates_arguments()
    {
        await using var ws = CreateTree();
        var grep = CoreTools.Grep(includeOutputModes: true);

        var regex = await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(grep, """{"pattern":"(unclosed"}"""));
        var mode = await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(grep, """{"pattern":"x","output_mode":"lines"}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(grep, """{"pattern":""}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(grep, """{"pattern":"x","max_results":0}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(grep, """{"pattern":"x","context":-1}"""));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.RunAsync(grep, """{"pattern":"x","path":"missing"}"""));

        Assert.StartsWith("Invalid regex:", regex.Message);
        Assert.Contains("files_with_matches", mode.Message);
    }

    [Fact]
    public async Task Glob_lists_matches_newest_first()
    {
        await using var ws = CreateTree();
        ws.Touch("src/main.py", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        ws.Touch("src/lib/util.py", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var output = await ws.RunAsync(CoreTools.Glob(), """{"pattern":"**/*.py"}""");
        var scoped = await ws.RunAsync(CoreTools.Glob(), """{"pattern":"*.json","path":"src/lib"}""");

        Assert.Equal("src/lib/util.py\nsrc/main.py", output.Text);
        Assert.Equal("src/lib/data.json", scoped.Text);
    }

    [Fact]
    public async Task Glob_reports_no_matches_and_errors()
    {
        await using var ws = CreateTree();

        Assert.Equal("No files matched the pattern.", (await ws.RunAsync(CoreTools.Glob(), """{"pattern":"**/*.rs"}""")).Text);
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.Glob(), """{"pattern":"[oops"}"""));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.RunAsync(CoreTools.Glob(), """{"pattern":"*","path":"missing"}"""));
    }
}

public class DirectoryToolTests
{
    [Fact]
    public async Task List_dir_renders_an_indented_tree()
    {
        await using var ws = new TestWorkspace();
        ws.Write("README.md", new string('x', 1536));
        ws.Write("src/app.cs", "12345");
        ws.Write("src/lib/util.cs", "1");
        ws.Write(".git/HEAD", "ref");
        ws.MakeDirectory("empty");

        var depth1 = await ws.RunAsync(CoreTools.ListDir(), """{}""");
        var depth3 = await ws.RunAsync(CoreTools.ListDir(), Args.Of(("path", "."), ("depth", 3)));

        var root = ws.Root + Path.DirectorySeparatorChar;
        Assert.Equal(root + "\n  .git/\n  empty/\n  src/\n  README.md (1.5 KB)", depth1.Text);
        Assert.Equal(
            root + "\n  .git/\n  empty/\n  src/\n    lib/\n      util.cs (1 B)\n    app.cs (5 B)\n  README.md (1.5 KB)",
            depth3.Text);
    }

    [Fact]
    public async Task List_dir_handles_empty_directories_and_limits()
    {
        await using var ws = new TestWorkspace();
        ws.MakeDirectory("empty");
        ws.Write("a/b/c/d/e/f/g.txt", "x");

        var empty = await ws.RunAsync(CoreTools.ListDir(), """{"path":"empty"}""");
        var deep = await ws.RunAsync(CoreTools.ListDir(), """{"depth":50}""");

        Assert.Equal(ws.PathOf("empty") + Path.DirectorySeparatorChar + "\n  (empty directory)", empty.Text);
        Assert.Contains("\n          e/", deep.Text);
        Assert.DoesNotContain("\n            f/", deep.Text);
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ListDir(), """{"depth":0}"""));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.RunAsync(CoreTools.ListDir(), """{"path":"missing"}"""));
        await Assert.ThrowsAsync<IOException>(() => ws.RunAsync(CoreTools.ListDir(), """{"path":"a/b/c/d/e/f/g.txt"}"""));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    public void Sizes_are_human_readable(long bytes, string expected)
    {
        Assert.Equal(expected, ListDirTool.FormatSize(bytes));
    }

    [Fact]
    public async Task Read_many_files_concatenates_files_with_headers()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "alpha\n");
        ws.Write("b.txt", "beta\r\nsecond");

        var output = await ws.RunAsync(CoreTools.ReadManyFiles(), """{"paths":["a.txt","b.txt"]}""");

        Assert.Equal("--- a.txt ---\nalpha\n\n--- b.txt ---\nbeta\nsecond\n", output.Text);
    }

    [Fact]
    public async Task Read_many_files_expands_globs_and_directories_and_deduplicates()
    {
        await using var ws = new TestWorkspace();
        ws.Write("src/a.py", "a");
        ws.Write("src/sub/b.py", "b");
        ws.Write("docs/x.md", "x");
        ws.Write("docs/y.md", "y");

        var output = await ws.RunAsync(CoreTools.ReadManyFiles(), Args.Of(("paths", new[] { "src/**/*.py", "src/a.py", "docs" })));

        Assert.Equal(
            "--- src/a.py ---\na\n\n--- src/sub/b.py ---\nb\n\n--- docs/x.md ---\nx\n\n--- docs/y.md ---\ny\n",
            output.Text);
    }

    [Fact]
    public async Task Read_many_files_skips_binaries_and_images_and_lists_unmatched_entries()
    {
        await using var ws = new TestWorkspace();
        ws.Write("ok.txt", "fine");
        ws.WriteBytes("data.bin", [1, 0, 2]);
        ws.WriteBytes("logo.png", [0x89, 0x50]);

        var output = await ws.RunAsync(CoreTools.ReadManyFiles(), """{"paths":["ok.txt","data.bin","logo.png","missing.txt","*.nothing"]}""");

        Assert.Equal(
            "--- ok.txt ---\nfine\n\n" +
            "--- data.bin ---\n[Skipped binary file (3 bytes).]\n\n" +
            "--- logo.png ---\n[Skipped image file (2 bytes); use read_file to view it.]\n" +
            "\n[No files found for: missing.txt, *.nothing]",
            output.Text);
    }

    [Fact]
    public async Task Read_many_files_reports_when_nothing_matched()
    {
        await using var ws = new TestWorkspace();

        var output = await ws.RunAsync(CoreTools.ReadManyFiles(), """{"paths":"nothing-here.txt"}""");

        Assert.Equal("No files found matching: nothing-here.txt", output.Text);
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ReadManyFiles(), """{"paths":[]}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ReadManyFiles(), """{"paths":[1,2]}"""));
    }
}

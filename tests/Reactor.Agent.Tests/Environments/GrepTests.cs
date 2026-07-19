using Reactor.Agent.Environments;

namespace Reactor.Agent.Tests.Environments;

public class GrepTests
{
    private static async Task<TestWorkspace> CreateTreeAsync()
    {
        await Task.Yield();
        var ws = new TestWorkspace();
        ws.Write("README.md", "Hello world\nTODO: write docs\n");
        ws.Write("src/app.cs", "class App\n{\n    // TODO first\n    void Run() { }\n    // todo second\n}\n");
        ws.Write("src/util/helpers.cs", "static class Helpers\n{\n    // TODO helper\n}\n");
        ws.Write("src/notes.txt", "nothing to see\n");
        ws.Write("node_modules/pkg/index.js", "// TODO in dependency\n");
        ws.Write(".git/config", "TODO in git internals\n");
        return ws;
    }

    private static GrepOptions Options(string mode = "content", int max = 100, string? glob = null, bool ignoreCase = false, int before = 0, int after = 0) => new()
    {
        OutputMode = mode,
        MaxResults = max,
        GlobFilter = glob,
        CaseInsensitive = ignoreCase,
        ContextBefore = before,
        ContextAfter = after,
    };

    [Fact]
    public async Task Content_mode_lists_relative_paths_line_numbers_and_text()
    {
        await using var ws = await CreateTreeAsync();

        var result = await ws.Environment.GrepAsync("TODO", ".", Options());

        Assert.Equal(
            "README.md:2:TODO: write docs\n" +
            "src/app.cs:3:    // TODO first\n" +
            "src/util/helpers.cs:3:    // TODO helper",
            result);
    }

    [Fact]
    public async Task Case_insensitive_search_finds_all_spellings()
    {
        await using var ws = await CreateTreeAsync();

        var result = await ws.Environment.GrepAsync("todo", "src", Options(ignoreCase: true));

        Assert.Equal(
            "src/app.cs:3:    // TODO first\n" +
            "src/app.cs:5:    // todo second\n" +
            "src/util/helpers.cs:3:    // TODO helper",
            result);
    }

    [Fact]
    public async Task Glob_filters_match_file_names_or_relative_paths()
    {
        await using var ws = await CreateTreeAsync();

        var byName = await ws.Environment.GrepAsync("TODO", ".", Options(glob: "*.md"));
        var byPath = await ws.Environment.GrepAsync("TODO", ".", Options(glob: "src/util/**"));
        var braces = await ws.Environment.GrepAsync("TODO|nothing", ".", Options(glob: "*.{txt,md}"));

        Assert.Equal("README.md:2:TODO: write docs", byName);
        Assert.Equal("src/util/helpers.cs:3:    // TODO helper", byPath);
        Assert.Equal("README.md:2:TODO: write docs\nsrc/notes.txt:1:nothing to see", braces);
    }

    [Fact]
    public async Task Files_with_matches_and_count_modes()
    {
        await using var ws = await CreateTreeAsync();

        var files = await ws.Environment.GrepAsync("(?i)todo", ".", Options(mode: "files_with_matches"));
        var counts = await ws.Environment.GrepAsync("(?i)todo", ".", Options(mode: "count"));

        Assert.Equal("README.md\nsrc/app.cs\nsrc/util/helpers.cs", files);
        Assert.Equal("README.md:1\nsrc/app.cs:2\nsrc/util/helpers.cs:1", counts);
    }

    [Fact]
    public async Task Results_stop_at_the_limit_with_a_marker()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", string.Join("\n", Enumerable.Range(1, 5).Select(i => "match " + i)));
        ws.Write("b.txt", "match b\n");

        var limited = await ws.Environment.GrepAsync("match", ".", Options(max: 3));
        var exact = await ws.Environment.GrepAsync("match", ".", Options(max: 6));
        var files = await ws.Environment.GrepAsync("match", ".", Options(mode: "files_with_matches", max: 1));

        Assert.Equal("a.txt:1:match 1\na.txt:2:match 2\na.txt:3:match 3\n[results truncated at 3]", limited);
        Assert.DoesNotContain("truncated", exact);
        Assert.Equal(6, exact.Split('\n').Length);
        Assert.Equal("a.txt\n[results truncated at 1]", files);
    }

    [Fact]
    public async Task Context_lines_use_dashes_and_group_separators()
    {
        await using var ws = new TestWorkspace();
        ws.Write("f.txt", "a\nb\nMATCH1\nc\nd\ne\nf\nMATCH2\ng\n");

        var result = await ws.Environment.GrepAsync("MATCH", ".", Options(before: 1, after: 1));

        Assert.Equal("f.txt-2-b\nf.txt:3:MATCH1\nf.txt-4-c\n--\nf.txt-7-f\nf.txt:8:MATCH2\nf.txt-9-g", result);
    }

    [Fact]
    public async Task Overlapping_context_is_merged()
    {
        await using var ws = new TestWorkspace();
        ws.Write("f.txt", "x\nM1\ny\nM2\nz\n");

        var result = await ws.Environment.GrepAsync("M\\d", ".", Options(before: 2, after: 2));

        Assert.Equal("f.txt-1-x\nf.txt:2:M1\nf.txt-3-y\nf.txt:4:M2\nf.txt-5-z", result);
    }

    [Fact]
    public async Task Truncation_with_context_keeps_only_trailing_context_of_the_last_match()
    {
        await using var ws = new TestWorkspace();
        ws.Write("f.txt", "M1\nafter1\nfiller\nbefore2\nM2\n");

        var result = await ws.Environment.GrepAsync("M\\d", ".", Options(max: 1, before: 1, after: 1));

        Assert.Equal("f.txt:1:M1\nf.txt-2-after1\n[results truncated at 1]", result);
    }

    [Fact]
    public async Task Vcs_directories_node_modules_binary_and_huge_files_are_skipped()
    {
        await using var ws = await CreateTreeAsync();
        ws.WriteBytes("src/binary.bin", [(byte)'T', (byte)'O', (byte)'D', (byte)'O', 0, 1, 2]);
        using (var stream = File.Create(ws.PathOf("src/huge.log")))
        {
            var line = "TODO huge\n"u8.ToArray();
            for (var written = 0L; written <= ManagedGrep.MaxFileSize; written += line.Length)
            {
                stream.Write(line);
            }
        }

        var result = await ws.Environment.GrepAsync("TODO", ".", Options(mode: "files_with_matches"));

        Assert.Equal("README.md\nsrc/app.cs\nsrc/util/helpers.cs", result);
    }

    [Fact]
    public async Task A_file_path_searches_just_that_file()
    {
        await using var ws = await CreateTreeAsync();

        var result = await ws.Environment.GrepAsync("TODO", "src/app.cs", Options());

        Assert.Equal("src/app.cs:3:    // TODO first", result);
    }

    [Fact]
    public async Task Paths_outside_the_working_directory_are_absolute()
    {
        await using var outside = new TestWorkspace();
        outside.Write("x.txt", "needle\n");
        await using var ws = new TestWorkspace();

        var result = await ws.Environment.GrepAsync("needle", outside.Root, Options());

        Assert.Equal(outside.PathOf("x.txt") + ":1:needle", result);
    }

    [Fact]
    public async Task Crlf_files_match_line_anchors_and_hide_carriage_returns()
    {
        await using var ws = new TestWorkspace();
        ws.Write("w.txt", "alpha\r\nbeta\r\n");

        var result = await ws.Environment.GrepAsync("^beta$", ".", Options());

        Assert.Equal("w.txt:2:beta", result);
    }

    [Fact]
    public async Task No_matches_returns_an_empty_string()
    {
        await using var ws = await CreateTreeAsync();

        Assert.Equal("", await ws.Environment.GrepAsync("does-not-occur", ".", Options()));
    }

    [Fact]
    public async Task Invalid_input_is_reported()
    {
        await using var ws = await CreateTreeAsync();

        var regex = await Assert.ThrowsAsync<ArgumentException>(() => ws.Environment.GrepAsync("([unclosed", ".", Options()));
        Assert.StartsWith("Invalid regex:", regex.Message);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.Environment.GrepAsync("x", "missing-dir", Options()));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.Environment.GrepAsync("x", ".", Options(mode: "lines")));
    }

    [Fact]
    public async Task Long_lines_are_clipped()
    {
        await using var ws = new TestWorkspace();
        ws.Write("min.js", "needle" + new string('x', 5000) + "\n");

        var result = await ws.Environment.GrepAsync("needle", ".", Options());

        Assert.EndsWith("... [line truncated]", result);
        Assert.True(result.Length < 2100);
    }

    [Fact]
    public void Ripgrep_json_events_produce_the_same_format()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "rgroot");
        var file = Path.Combine(root, "src", "a.cs").Replace("\\", "\\\\", StringComparison.Ordinal);
        string[] events =
        [
            """{"type":"begin","data":{"path":{"text":"FILE"}}}""",
            """{"type":"context","data":{"path":{"text":"FILE"},"lines":{"text":"before\n"},"line_number":1,"absolute_offset":0,"submatches":[]}}""",
            """{"type":"match","data":{"path":{"text":"FILE"},"lines":{"text":"TODO here\r\n"},"line_number":2,"absolute_offset":7,"submatches":[{"match":{"text":"TODO"},"start":0,"end":4}]}}""",
            """{"type":"match","data":{"path":{"text":"FILE"},"lines":{"bytes":"VE9ETyB0d28K"},"line_number":5,"absolute_offset":30,"submatches":[]}}""",
            """{"type":"end","data":{"path":{"text":"FILE"},"binary_offset":null,"stats":{"matched_lines":2,"matches":2}}}""",
            """{"type":"summary","data":{"stats":{"matched_lines":2}}}""",
        ];

        string Run(GrepOutputMode mode)
        {
            var collector = new GrepCollector(mode, 100, 1, 0);
            foreach (var line in events)
            {
                RipgrepSearch.ProcessJsonLine(line.Replace("FILE", file, StringComparison.Ordinal), collector, root);
            }

            return collector.Build();
        }

        Assert.Equal("src/a.cs-1-before\nsrc/a.cs:2:TODO here\n--\nsrc/a.cs:5:TODO two", Run(GrepOutputMode.Content));
        Assert.Equal("src/a.cs", Run(GrepOutputMode.FilesWithMatches));
        Assert.Equal("src/a.cs:2", Run(GrepOutputMode.Count));
    }

    [Fact]
    public void Ripgrep_arguments_cover_the_options()
    {
        var args = RipgrepSearch.BuildArguments("-pattern", "/repo", new GrepOptions
        {
            CaseInsensitive = true,
            GlobFilter = "*.cs",
            ContextBefore = 2,
            ContextAfter = 3,
        }, GrepOutputMode.Content).ToList();

        Assert.Contains("--json", args);
        Assert.Contains("--ignore-case", args);
        Assert.Equal("*.cs", args[args.IndexOf("--glob") + 1]);
        Assert.Contains("!.git", args);
        Assert.Contains("!node_modules", args);
        Assert.Equal("2", args[args.IndexOf("--before-context") + 1]);
        Assert.Equal("3", args[args.IndexOf("--after-context") + 1]);
        Assert.Equal(new[] { "--regexp", "-pattern", "--", "/repo" }, args.TakeLast(4));

        var files = RipgrepSearch.BuildArguments("x", "/repo", new GrepOptions(), GrepOutputMode.FilesWithMatches).ToList();
        Assert.Equal("1", files[files.IndexOf("--max-count") + 1]);
    }

    [Fact]
    public async Task Ripgrep_and_managed_search_agree_when_ripgrep_is_installed()
    {
        Assert.SkipWhen(ExecutableLocator.Find("rg") is null, "ripgrep (rg) is not installed.");
        await using var ws = await CreateTreeAsync();
        var withRipgrep = new LocalExecutionEnvironment(new LocalEnvironmentOptions { WorkingDirectory = ws.Root, UseRipgrep = true });

        foreach (var options in new[] { Options(), Options(ignoreCase: true, before: 1, after: 1), Options(mode: "count"), Options(mode: "files_with_matches", glob: "*.cs") })
        {
            Assert.Equal(
                await ws.Environment.GrepAsync("TODO", ".", options),
                await withRipgrep.GrepAsync("TODO", ".", options));
        }
    }
}

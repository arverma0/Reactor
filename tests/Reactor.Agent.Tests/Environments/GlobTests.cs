using Reactor.Agent.Environments;

namespace Reactor.Agent.Tests.Environments;

public class GlobTests
{
    private static readonly DateTime Base = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TestWorkspace CreateTree()
    {
        var ws = new TestWorkspace();
        ws.Write("Program.cs", "x");
        ws.Write("README.md", "x");
        ws.Write("src/app.cs", "x");
        ws.Write("src/app.test.cs", "x");
        ws.Write("src/lib/util.cs", "x");
        ws.Write("src/lib/data.json", "x");
        ws.Write("docs/guide.md", "x");
        ws.Write(".github/workflows/ci.yml", "x");
        ws.Write(".git/objects/abc.cs", "x");
        ws.Write(".editorconfig", "x");
        var i = 0;
        foreach (var file in new[]
                 {
                     "Program.cs", "README.md", "src/app.cs", "src/app.test.cs", "src/lib/util.cs", "src/lib/data.json",
                     "docs/guide.md", ".github/workflows/ci.yml", ".editorconfig",
                 })
        {
            ws.Touch(file, Base.AddMinutes(i++));
        }

        return ws;
    }

    private static async Task<IReadOnlyList<string>> Sorted(TestWorkspace ws, string pattern, string path = ".") =>
        (await ws.Environment.GlobAsync(pattern, path)).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Globstar_matches_any_depth_including_the_root()
    {
        await using var ws = CreateTree();

        Assert.Equal(new[] { "Program.cs", "src/app.cs", "src/app.test.cs", "src/lib/util.cs" }, await Sorted(ws, "**/*.cs"));
    }

    [Fact]
    public async Task Single_star_does_not_cross_directories()
    {
        await using var ws = CreateTree();

        Assert.Equal(new[] { "Program.cs" }, await Sorted(ws, "*.cs"));
        Assert.Equal(new[] { "src/app.cs", "src/app.test.cs" }, await Sorted(ws, "src/*.cs"));
    }

    [Fact]
    public async Task Brace_alternatives_expand()
    {
        await using var ws = CreateTree();

        Assert.Equal(new[] { "README.md", "docs/guide.md", "src/lib/data.json" }, await Sorted(ws, "**/*.{md,json}"));
        Assert.Equal(new[] { "docs/guide.md", "src/app.cs" }, await Sorted(ws, "{src/app,docs/guide}.{cs,md}"));
    }

    [Fact]
    public async Task Question_marks_and_character_classes()
    {
        await using var ws = new TestWorkspace();
        foreach (var name in new[] { "a1.txt", "b2.txt", "c3.txt", "ab.txt" })
        {
            ws.Write(name, "x");
        }

        Assert.Equal(new[] { "a1.txt", "b2.txt", "c3.txt" }, await Sorted(ws, "?[0-9].txt"));
        Assert.Equal(new[] { "a1.txt", "ab.txt", "b2.txt" }, await Sorted(ws, "[ab]?.txt"));
        Assert.Equal(new[] { "c3.txt" }, await Sorted(ws, "[!ab]*.txt"));
    }

    [Fact]
    public async Task Trailing_globstar_matches_everything_below()
    {
        await using var ws = CreateTree();

        Assert.Equal(new[] { "src/app.cs", "src/app.test.cs", "src/lib/data.json", "src/lib/util.cs" }, await Sorted(ws, "src/**"));
    }

    [Fact]
    public async Task Dot_files_are_matched_but_git_internals_and_directories_are_not()
    {
        await using var ws = CreateTree();

        var all = await Sorted(ws, "**/*");

        Assert.Contains(".editorconfig", all);
        Assert.Contains(".github/workflows/ci.yml", all);
        Assert.DoesNotContain(all, p => p.StartsWith(".git/", StringComparison.Ordinal));
        Assert.DoesNotContain("src", all);
        Assert.DoesNotContain("src/lib", all);
        Assert.Equal(9, all.Count);
    }

    [Fact]
    public async Task Results_are_sorted_newest_first_with_ties_by_path()
    {
        await using var ws = new TestWorkspace();
        ws.Write("old.txt", "x");
        ws.Write("new.txt", "x");
        ws.Write("tie-b.txt", "x");
        ws.Write("tie-a.txt", "x");
        ws.Touch("old.txt", Base);
        ws.Touch("new.txt", Base.AddHours(3));
        ws.Touch("tie-b.txt", Base.AddHours(1));
        ws.Touch("tie-a.txt", Base.AddHours(1));

        var result = await ws.Environment.GlobAsync("*.txt", ".");

        Assert.Equal(new[] { "new.txt", "tie-a.txt", "tie-b.txt", "old.txt" }, result);
    }

    [Fact]
    public async Task Base_path_scopes_the_search_and_results_stay_relative_to_the_working_directory()
    {
        await using var ws = CreateTree();

        Assert.Equal(new[] { "src/lib/data.json", "src/lib/util.cs" }, await Sorted(ws, "*", "src/lib"));
        Assert.Equal(new[] { "src/lib/util.cs" }, await Sorted(ws, "**/*.cs", ws.PathOf("src/lib")));
    }

    [Fact]
    public async Task Absolute_and_literal_patterns_are_supported()
    {
        await using var ws = CreateTree();

        Assert.Equal(new[] { "src/lib/util.cs" }, await Sorted(ws, ws.PathOf("src") + "/**/util.cs"));
        Assert.Equal(new[] { "src/app.cs" }, await Sorted(ws, "src/app.cs"));
        Assert.Empty(await Sorted(ws, "src/missing.cs"));
        Assert.Equal(new[] { "src/lib/util.cs" }, await Sorted(ws, "./src/lib/*.cs"));
    }

    [Fact]
    public async Task Paths_outside_the_working_directory_are_absolute()
    {
        await using var outside = new TestWorkspace();
        outside.Write("x/y.cs", "x");
        await using var ws = new TestWorkspace();

        var result = await ws.Environment.GlobAsync("**/*.cs", outside.Root);

        Assert.Equal(new[] { outside.PathOf("x/y.cs") }, result);
    }

    [Fact]
    public async Task Invalid_patterns_and_missing_directories_are_reported()
    {
        await using var ws = CreateTree();

        await Assert.ThrowsAsync<ArgumentException>(() => ws.Environment.GlobAsync("", "."));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.Environment.GlobAsync("src/[abc.cs", "."));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.Environment.GlobAsync("src/{a,b.cs", "."));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.Environment.GlobAsync("*", "missing"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ws.Environment.GlobAsync("*", "README.md"));
    }

    [Fact]
    public async Task Matching_follows_the_platform_case_sensitivity()
    {
        await using var ws = CreateTree();

        var result = await Sorted(ws, "**/*.CS");

        if (GlobPattern.IgnoreCaseByDefault)
        {
            Assert.Equal(4, result.Count);
        }
        else
        {
            Assert.Empty(result);
        }
    }

    [Theory]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "dir/a.cs", false)]
    [InlineData("**/*.cs", "a.cs", true)]
    [InlineData("**/*.cs", "x/y/z/a.cs", true)]
    [InlineData("src/**/test/*.cs", "src/test/a.cs", true)]
    [InlineData("src/**/test/*.cs", "src/a/b/test/a.cs", true)]
    [InlineData("src/**/test/*.cs", "src/a/b/tests/a.cs", false)]
    [InlineData("*.{ts,tsx}", "view.tsx", true)]
    [InlineData("*.{ts,tsx}", "view.js", false)]
    [InlineData("file[0-9].txt", "file7.txt", true)]
    [InlineData("file[!0-9].txt", "file7.txt", false)]
    [InlineData("{a,b{c,d}}.txt", "bd.txt", true)]
    [InlineData("{a}.txt", "{a}.txt", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    public void Glob_patterns_match_relative_paths(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, GlobPattern.Parse(pattern, ignoreCase: false).IsMatch(path));
    }
}

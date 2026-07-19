using System.Text;
using Reactor.Agent.Tests.Environments;
using Reactor.Agent.Tools;

namespace Reactor.Agent.Tests.Tools;

public class EditFileToolTests
{
    private const string Fuzzy = " (matched after normalizing whitespace/punctuation)";

    private static Task<ToolOutput> Edit(TestWorkspace ws, string path, string oldString, string newString, object? replaceAll = null) =>
        ws.RunAsync(CoreTools.EditFile(), replaceAll is null
            ? Args.Of(("file_path", path), ("old_string", oldString), ("new_string", newString))
            : Args.Of(("file_path", path), ("old_string", oldString), ("new_string", newString), ("replace_all", replaceAll)));

    [Fact]
    public async Task Exact_unique_match_is_replaced()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.py", "def f():\n    return 1\n");

        var output = await Edit(ws, "a.py", "return 1", "return 2");

        Assert.Equal($"Replaced 1 occurrence(s) in {ws.PathOf("a.py")}", output.Text);
        Assert.Equal("def f():\n    return 2\n", ws.ReadText("a.py"));
    }

    [Fact]
    public async Task Multiple_matches_require_more_context_or_replace_all()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "x = 1\ny = 1\nz = 1\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Edit(ws, "a.txt", "= 1", "= 2"));
        Assert.Equal("old_string matches 3 locations; include more surrounding context to make it unique, or set replace_all=true.", error.Message);
        Assert.Equal("x = 1\ny = 1\nz = 1\n", ws.ReadText("a.txt"));

        var output = await Edit(ws, "a.txt", "= 1", "= 2", replaceAll: true);
        Assert.Equal($"Replaced 3 occurrence(s) in {ws.PathOf("a.txt")}", output.Text);
        Assert.Equal("x = 2\ny = 2\nz = 2\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Replace_all_accepts_string_booleans_from_models()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "a a");

        await ws.RunAsync(CoreTools.EditFile(), """{"file_path":"a.txt","old_string":"a","new_string":"b","replace_all":"true"}""");

        Assert.Equal("b b", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Missing_text_and_invalid_requests_are_errors()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "hello\n");

        var notFound = await Assert.ThrowsAsync<InvalidOperationException>(() => Edit(ws, "a.txt", "goodbye", "x"));
        var empty = await Assert.ThrowsAsync<ArgumentException>(() => Edit(ws, "a.txt", "", "x"));
        var same = await Assert.ThrowsAsync<ArgumentException>(() => Edit(ws, "a.txt", "hello", "hello"));
        var missingFile = await Assert.ThrowsAsync<FileNotFoundException>(() => Edit(ws, "nope.txt", "a", "b"));

        Assert.StartsWith($"old_string was not found in {ws.PathOf("a.txt")}", notFound.Message);
        Assert.Contains("use write_file to create files", empty.Message);
        Assert.Contains("identical", same.Message);
        Assert.Equal($"File not found: {ws.PathOf("nope.txt")}", missingFile.Message);
        Assert.Equal("hello\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Fuzzy_match_ignores_trailing_whitespace_and_edits_the_actual_span()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.cs", "keep();   \nif (x) {    \n    run();\n}\nkeep();\n");

        var output = await Edit(ws, "a.cs", "if (x) {\n    run();\n}", "if (y) {\n    walk();\n}");

        Assert.EndsWith(Fuzzy, output.Text);
        Assert.Equal("keep();   \nif (y) {\n    walk();\n}\nkeep();\n", ws.ReadText("a.cs"));
    }

    [Theory]
    [InlineData("print(\u2018hi\u2019)", "print('hi')")]
    [InlineData("say \u201Cquoted\u201D", "say \"quoted\"")]
    [InlineData("a \u2014 b \u2013 c", "a - b - c")]
    [InlineData("wait\u2026", "wait...")]
    [InlineData("non\u00A0breaking", "non breaking")]
    public async Task Fuzzy_match_treats_typographic_punctuation_as_ascii(string fileText, string oldString)
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "before\n" + fileText + "\nafter\n");

        var output = await Edit(ws, "a.txt", oldString, "REPLACED");

        Assert.EndsWith(Fuzzy, output.Text);
        Assert.Equal("before\nREPLACED\nafter\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Fuzzy_matches_still_have_to_be_unique()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "it\u2019s\nit\u2019s\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Edit(ws, "a.txt", "it's", "it is"));

        Assert.StartsWith("old_string matches 2 locations", error.Message);
    }

    [Fact]
    public async Task Crlf_files_keep_crlf_line_endings()
    {
        await using var ws = new TestWorkspace();
        ws.Write("w.cs", "class A\r\n{\r\n    void M() { }\r\n}\r\n");

        var output = await Edit(ws, "w.cs", "{\n    void M() { }\n}", "{\n    void M() { }\n    void N() { }\n}");

        Assert.DoesNotContain("matched after", output.Text);
        Assert.Equal("class A\r\n{\r\n    void M() { }\r\n    void N() { }\r\n}\r\n", ws.ReadText("w.cs"));
    }

    [Fact]
    public async Task Single_line_edits_that_add_lines_to_crlf_files_use_crlf()
    {
        await using var ws = new TestWorkspace();
        ws.Write("w.txt", "one\r\ntwo\r\n");

        await Edit(ws, "w.txt", "one", "one\none-and-a-half");

        Assert.Equal("one\r\none-and-a-half\r\ntwo\r\n", ws.ReadText("w.txt"));
    }

    [Fact]
    public async Task Lf_files_stay_lf_even_when_new_text_has_crlf()
    {
        await using var ws = new TestWorkspace();
        ws.Write("u.txt", "one\ntwo\n");

        await Edit(ws, "u.txt", "two", "two\r\nthree");

        Assert.Equal("one\ntwo\nthree\n", ws.ReadText("u.txt"));
    }

    [Fact]
    public async Task Utf8_byte_order_marks_are_preserved()
    {
        await using var ws = new TestWorkspace();
        ws.WriteBytes("bom.txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("value = 1\n")]);

        await Edit(ws, "bom.txt", "1", "2");

        Assert.Equal([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("value = 2\n")], ws.ReadBytes("bom.txt"));
    }

    [Fact]
    public async Task Binary_files_cannot_be_edited()
    {
        await using var ws = new TestWorkspace();
        ws.WriteBytes("b.bin", [1, 0, 2]);

        await Assert.ThrowsAsync<InvalidDataException>(() => Edit(ws, "b.bin", "a", "b"));
    }

    [Fact]
    public async Task New_string_may_be_empty_to_delete_text()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "keep\nremove me\nkeep\n");

        await Edit(ws, "a.txt", "remove me\n", "");

        Assert.Equal("keep\nkeep\n", ws.ReadText("a.txt"));
    }
}

public class EditFileGeminiToolTests
{
    private static Task<ToolOutput> Replace(TestWorkspace ws, string path, string oldString, string newString, object? expected = null) =>
        ws.RunAsync(CoreTools.EditFileGemini(), expected is null
            ? Args.Of(("file_path", path), ("old_string", oldString), ("new_string", newString))
            : Args.Of(("file_path", path), ("old_string", oldString), ("new_string", newString), ("expected_replacements", expected)));

    [Fact]
    public async Task Replaces_a_single_occurrence_by_default()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "alpha beta\n");

        var output = await Replace(ws, "a.txt", "beta", "gamma");

        Assert.Equal($"Replaced 1 occurrence(s) in {ws.PathOf("a.txt")}", output.Text);
        Assert.Equal("alpha gamma\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Occurrence_count_must_match_expected_replacements()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "x\nx\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Replace(ws, "a.txt", "x", "y"));
        Assert.Equal(
            $"Expected 1 occurrence(s) of old_string in {ws.PathOf("a.txt")} but found 2. Include more surrounding context to identify the intended location, or set expected_replacements to 2 to replace all of them.",
            error.Message);

        var tooMany = await Assert.ThrowsAsync<InvalidOperationException>(() => Replace(ws, "a.txt", "x", "y", expected: 3));
        Assert.Contains("but found 2", tooMany.Message);
        Assert.Equal("x\nx\n", ws.ReadText("a.txt"));

        var output = await Replace(ws, "a.txt", "x", "y", expected: 2);
        Assert.Equal($"Replaced 2 occurrence(s) in {ws.PathOf("a.txt")}", output.Text);
        Assert.Equal("y\ny\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Expected_replacements_accepts_parsed_and_in_memory_numbers()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "x x");
        ws.Write("b.txt", "x x");

        await ws.RunAsync(CoreTools.EditFileGemini(), """{"file_path":"a.txt","old_string":"x","new_string":"y","expected_replacements":2}""");
        await Replace(ws, "b.txt", "x", "y", expected: 2L);

        Assert.Equal("y y", ws.ReadText("a.txt"));
        Assert.Equal("y y", ws.ReadText("b.txt"));
    }

    [Fact]
    public async Task Not_found_reports_zero_occurrences()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "abc");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Replace(ws, "a.txt", "zzz", "y"));

        Assert.Contains("found 0", error.Message);
    }

    [Fact]
    public async Task Single_replacement_falls_back_to_fuzzy_matching()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "msg = \u201Chello\u201D  \nend\n");

        var output = await Replace(ws, "a.txt", "msg = \"hello\"\nend", "msg = \"bye\"\nend");

        Assert.EndsWith("(matched after normalizing whitespace/punctuation)", output.Text);
        Assert.Equal("msg = \"bye\"\nend\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Empty_old_string_creates_a_new_file_but_never_overwrites()
    {
        await using var ws = new TestWorkspace();
        ws.Write("exists.txt", "keep");

        var output = await Replace(ws, "dir/new.txt", "", "fresh content\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Replace(ws, "exists.txt", "", "x"));

        Assert.Equal($"Created new file {ws.PathOf("dir/new.txt")}", output.Text);
        Assert.Equal("fresh content\n", ws.ReadText("dir/new.txt"));
        Assert.Contains("already exists", error.Message);
        Assert.Equal("keep", ws.ReadText("exists.txt"));
    }

    [Fact]
    public async Task Invalid_expected_replacements_is_rejected()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "x");

        await Assert.ThrowsAsync<ArgumentException>(() => Replace(ws, "a.txt", "x", "y", expected: 0));
    }
}

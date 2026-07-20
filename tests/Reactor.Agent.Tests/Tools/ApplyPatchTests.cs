using System.Text;
using Reactor.Agent.Tests.Environments;
using Reactor.Agent.Tools;
using Reactor.Agent.Tools.ApplyPatch;

namespace Reactor.Agent.Tests.Tools;

public class PatchParserTests
{
    private static string P(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void Parses_add_delete_update_move_hints_and_end_of_file()
    {
        var operations = PatchParser.Parse(P(
            "*** Begin Patch",
            "*** Add File: new.txt",
            "+hello",
            "+world",
            "*** Delete File: old.txt",
            "*** Update File: src/a.py",
            "*** Move to: src/b.py",
            "@@ def main():",
            " context",
            "-removed",
            "+added",
            "*** End of File",
            "*** End Patch"));

        Assert.Equal(3, operations.Count);
        Assert.Equal(PatchOperationKind.Add, operations[0].Kind);
        Assert.Equal("new.txt", operations[0].Path);
        Assert.Equal(new[] { "hello", "world" }, operations[0].AddedLines);
        Assert.Equal(PatchOperationKind.Delete, operations[1].Kind);
        Assert.Equal("old.txt", operations[1].Path);
        var update = operations[2];
        Assert.Equal(PatchOperationKind.Update, update.Kind);
        Assert.Equal("src/b.py", update.MoveTo);
        var hunk = Assert.Single(update.Hunks);
        Assert.Equal("def main():", hunk.ContextHint);
        Assert.True(hunk.IsEndOfFile);
        Assert.Equal(8, hunk.LineNumber);
        Assert.Equal(
            new[] { new PatchLine(PatchLineKind.Context, "context"), new PatchLine(PatchLineKind.Delete, "removed"), new PatchLine(PatchLineKind.Add, "added") },
            hunk.Lines);
    }

    [Theory]
    [InlineData("apply_patch <<'EOF'", "EOF")]
    [InlineData("apply_patch <<EOF", "EOF")]
    [InlineData("apply_patch << \"PATCH\"", "PATCH")]
    [InlineData("<<'EOF'", "EOF")]
    public void Heredoc_wrappers_are_ignored(string opener, string terminator)
    {
        var operations = PatchParser.Parse(P(opener, "*** Begin Patch", "*** Delete File: a.txt", "*** End Patch", terminator));

        Assert.Equal("a.txt", Assert.Single(operations).Path);
    }

    [Fact]
    public void Crlf_input_missing_final_newline_and_markdown_fences_are_accepted()
    {
        var crlf = PatchParser.Parse("*** Begin Patch\r\n*** Update File: a.txt\r\n@@\r\n-x\r\n+y\r\n*** End Patch");
        var fenced = PatchParser.Parse(P("```diff", "*** Begin Patch", "*** Delete File: a.txt", "*** End Patch", "```"));

        Assert.Equal(new[] { new PatchLine(PatchLineKind.Delete, "x"), new PatchLine(PatchLineKind.Add, "y") }, Assert.Single(Assert.Single(crlf).Hunks).Lines);
        Assert.Single(fenced);
    }

    [Theory]
    [InlineData("@@ -12,7 +12,8 @@ def foo():", "def foo():")]
    [InlineData("@@ -1 +1 @@", null)]
    [InlineData("@@", null)]
    [InlineData("@@ class Widget @@", "class Widget")]
    public void Hunk_hints_tolerate_unified_diff_headers(string header, string? hint)
    {
        var operations = PatchParser.Parse(P("*** Begin Patch", "*** Update File: a.txt", header, "-x", "+y", "*** End Patch"));

        Assert.Equal(hint, Assert.Single(Assert.Single(operations).Hunks).ContextHint);
    }

    [Fact]
    public void First_hunk_may_omit_the_header_and_bare_blank_lines_are_context()
    {
        var operations = PatchParser.Parse(P("*** Begin Patch", "*** Update File: a.txt", " a", "", "-b", "+B", "", "*** End Patch"));

        var hunk = Assert.Single(Assert.Single(operations).Hunks);
        Assert.Null(hunk.ContextHint);
        Assert.Equal(
            new[] { new PatchLine(PatchLineKind.Context, "a"), new PatchLine(PatchLineKind.Context, ""), new PatchLine(PatchLineKind.Delete, "b"), new PatchLine(PatchLineKind.Add, "B") },
            hunk.Lines);
    }

    [Fact]
    public void Pure_renames_need_no_hunks()
    {
        var operations = PatchParser.Parse(P("*** Begin Patch", "*** Update File: a.txt", "*** Move to: b.txt", "*** End Patch"));

        Assert.Equal("b.txt", Assert.Single(operations).MoveTo);
        Assert.Empty(operations[0].Hunks);
    }

    [Theory]
    [InlineData("", "Invalid patch (line 1): The patch is empty.")]
    [InlineData("not a patch\n", "Invalid patch (line 1): The patch must start with '*** Begin Patch'.")]
    [InlineData("*** Begin Patch\n*** Add File: a\n+x\n", "Invalid patch (line 3): The patch must end with '*** End Patch'.")]
    [InlineData("*** Begin Patch\n*** Update File: a\n@@\n context\n?bad\n*** End Patch\n", "Invalid patch (line 5): Invalid hunk line '?bad'")]
    [InlineData("*** Begin Patch\n*** Add File: a\nno plus\n*** End Patch\n", "Invalid patch (line 3): Every line of an added file must start with '+'")]
    [InlineData("*** Begin Patch\n*** Update File: a\n*** End Patch\n", "Invalid patch (line 2): '*** Update File: a' contains no hunks.")]
    [InlineData("*** Begin Patch\n*** Rename File: a\n*** End Patch\n", "Invalid patch (line 2): Unexpected line '*** Rename File: a'")]
    [InlineData("*** Begin Patch\n*** Delete File:\n*** End Patch\n", "Invalid patch (line 2): '*** Delete File:' requires a file path.")]
    [InlineData("*** Begin Patch\n*** Update File: a\n*** End of File\n*** End Patch\n", "Invalid patch (line 3): '*** End of File' must follow the lines of a hunk.")]
    [InlineData("*** Begin Patch\n*** Update File: a\n@@ x\n*** End of File\n*** End Patch\n", "Invalid patch (line 3): Empty hunk in '*** Update File: a': after '@@'")]
    [InlineData("*** Begin Patch\n*** Update File: a\n@@ x\n context\n*** Rename: b\n*** End Patch\n", "Invalid patch (line 5): Unexpected line '*** Rename: b' in '*** Update File: a'. Hunk lines start with")]
    [InlineData("*** Begin Patch\n*** Delete File: a\n+junk\n*** End Patch\n", "Invalid patch (line 3): Unexpected content after '*** Delete File: a'.")]
    public void Malformed_patches_report_the_line(string patch, string expectedStart)
    {
        var error = Assert.Throws<PatchFormatException>(() => PatchParser.Parse(patch));

        Assert.StartsWith(expectedStart, error.Message);
    }

    [Fact]
    public void Stacked_hunk_headers_form_a_hint_chain()
    {
        var hunk = Assert.Single(Assert.Single(PatchParser.Parse(P(
            "*** Begin Patch",
            "*** Update File: a.py",
            "@@ class Widget",
            "",
            "@@     def render(self):",
            "-        return 1",
            "+        return 2",
            "*** End Patch"))).Hunks);

        Assert.Equal(["class Widget", "def render(self):"], hunk.ContextHints);
        Assert.Equal("def render(self):", hunk.ContextHint);
        Assert.Equal(3, hunk.LineNumber);
        Assert.Equal(2, hunk.Lines.Count);
    }

    [Fact]
    public void Concatenated_patches_are_parsed_as_one()
    {
        var operations = PatchParser.Parse(P(
            "*** Begin Patch",
            "*** Add File: a.txt",
            "+a",
            "*** End Patch",
            "*** Begin Patch",
            "*** Update File: b.txt",
            "@@",
            "-b",
            "+B",
            "*** End Patch",
            "*** Delete File: c.txt",
            "*** End Patch"));

        Assert.Equal([PatchOperationKind.Add, PatchOperationKind.Update, PatchOperationKind.Delete], operations.Select(o => o.Kind));
        Assert.Equal(["a"], operations[0].AddedLines);
    }

    [Fact]
    public void Indented_end_markers_between_operations_are_accepted()
    {
        // Seen from Qwen2.5-7B: " *** End Patch" (space-prefixed) after each file section.
        var operations = PatchParser.Parse(P(
            "*** Begin Patch",
            "*** Add File: a.txt",
            "+a",
            " *** End Patch",
            "*** Update File: b.txt",
            "@@",
            " keep",
            "-b",
            "+B",
            " *** End Patch",
            "",
            "*** Delete File: c.txt",
            " *** End Patch"));

        Assert.Equal([PatchOperationKind.Add, PatchOperationKind.Update, PatchOperationKind.Delete], operations.Select(o => o.Kind));
        Assert.Equal(["a"], operations[0].AddedLines);
        Assert.Equal([PatchLineKind.Context, PatchLineKind.Delete, PatchLineKind.Add], Assert.Single(operations[1].Hunks).Lines.Select(l => l.Kind));
    }

    [Fact]
    public void An_end_marker_line_inside_a_hunk_is_still_context()
    {
        var hunk = Assert.Single(Assert.Single(PatchParser.Parse(P(
            "*** Begin Patch",
            "*** Update File: docs/patches.md",
            "@@",
            " *** End Patch",
            "-old",
            "+new",
            "*** End Patch"))).Hunks);

        Assert.Equal(new PatchLine(PatchLineKind.Context, "*** End Patch"), hunk.Lines[0]);
        Assert.Equal(3, hunk.Lines.Count);
    }
}

public class ApplyPatchToolTests
{
    private static string P(params string[] lines) => string.Join("\n", lines) + "\n";

    private static Task<ToolOutput> Apply(TestWorkspace ws, string patch) => ws.RunAsync(CoreTools.ApplyPatch(), Args.Of(("patch", patch)));

    [Fact]
    public async Task Add_file_creates_directories_and_ends_with_a_newline()
    {
        await using var ws = new TestWorkspace();

        var output = await Apply(ws, P("*** Begin Patch", "*** Add File: src/utils/helpers.py", "+def greet(name):", "+    return f\"Hello, {name}!\"", "*** End Patch"));

        Assert.Equal("Success. Updated the following files:\nA src/utils/helpers.py", output.Text);
        Assert.Equal("def greet(name):\n    return f\"Hello, {name}!\"\n", ws.ReadText("src/utils/helpers.py"));
    }

    [Fact]
    public async Task Add_file_refuses_to_overwrite()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "keep");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(ws, P("*** Begin Patch", "*** Add File: a.txt", "+new", "*** End Patch")));

        Assert.Contains("already exists; use *** Update File", error.Message);
        Assert.Equal("keep", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Delete_file_removes_it_and_requires_it_to_exist()
    {
        await using var ws = new TestWorkspace();
        ws.Write("src/old_module.py", "x");

        var output = await Apply(ws, P("*** Begin Patch", "*** Delete File: src/old_module.py", "*** End Patch"));
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => Apply(ws, P("*** Begin Patch", "*** Delete File: missing.txt", "*** End Patch")));

        Assert.Equal("Success. Updated the following files:\nD src/old_module.py", output.Text);
        Assert.False(ws.Exists("src/old_module.py"));
        Assert.StartsWith("Cannot delete 'missing.txt'", error.Message);
    }

    [Fact]
    public async Task Update_file_applies_the_spec_example()
    {
        await using var ws = new TestWorkspace();
        ws.Write("src/main.py", "def main():\n    print(\"Hello\")\n    return 0\n");

        var output = await Apply(ws, P(
            "*** Begin Patch",
            "*** Update File: src/main.py",
            "@@ def main():",
            "     print(\"Hello\")",
            "-    return 0",
            "+    print(\"World\")",
            "+    return 1",
            "*** End Patch"));

        Assert.Equal("Success. Updated the following files:\nM src/main.py", output.Text);
        Assert.Equal("def main():\n    print(\"Hello\")\n    print(\"World\")\n    return 1\n", ws.ReadText("src/main.py"));
    }

    [Fact]
    public async Task Multiple_hunks_apply_in_one_update()
    {
        await using var ws = new TestWorkspace();
        ws.Write("src/config.py", "DEFAULT_TIMEOUT = 30\nRETRIES = 3\n\ndef load_config():\n    config = {}\n    config[\"debug\"] = False\n    return config\n");

        await Apply(ws, P(
            "*** Begin Patch",
            "*** Update File: src/config.py",
            "@@ DEFAULT_TIMEOUT = 30",
            "-DEFAULT_TIMEOUT = 30",
            "+DEFAULT_TIMEOUT = 60",
            "@@ def load_config():",
            "     config = {}",
            "-    config[\"debug\"] = False",
            "+    config[\"debug\"] = True",
            "*** End Patch"));

        Assert.Equal(
            "DEFAULT_TIMEOUT = 60\nRETRIES = 3\n\ndef load_config():\n    config = {}\n    config[\"debug\"] = True\n    return config\n",
            ws.ReadText("src/config.py"));
    }

    [Fact]
    public async Task Context_hints_select_between_identical_blocks()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.py", "def first():\n    value = 1\n    return value\n\ndef second():\n    value = 1\n    return value\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.py", "@@ def second():", "     value = 1", "-    return value", "+    return value * 2", "*** End Patch"));

        Assert.Equal("def first():\n    value = 1\n    return value\n\ndef second():\n    value = 1\n    return value * 2\n", ws.ReadText("a.py"));
    }

    [Fact]
    public async Task Hunks_without_hints_use_the_first_match_after_the_previous_hunk()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "x\ny\nx\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.txt", "@@", "-x", "+X1", "@@", "-x", "+X2", "*** End Patch"));

        Assert.Equal("X1\ny\nX2\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task End_of_file_marker_anchors_the_hunk_at_the_end()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.py", "x = 1\nprint(x)\nx = 1\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.py", "@@", "-x = 1", "+x = 2", "*** End of File", "*** End Patch"));

        Assert.Equal("x = 1\nprint(x)\nx = 2\n", ws.ReadText("a.py"));
    }

    [Fact]
    public async Task Update_with_move_renames_the_file()
    {
        await using var ws = new TestWorkspace();
        ws.Write("old_name.py", "import os\nimport sys\nimport old_dep\n");

        var output = await Apply(ws, P(
            "*** Begin Patch",
            "*** Update File: old_name.py",
            "*** Move to: pkg/new_name.py",
            "@@ import os",
            " import sys",
            "-import old_dep",
            "+import new_dep",
            "*** End Patch"));

        Assert.Equal("Success. Updated the following files:\nM old_name.py -> pkg/new_name.py", output.Text);
        Assert.False(ws.Exists("old_name.py"));
        Assert.Equal("import os\nimport sys\nimport new_dep\n", ws.ReadText("pkg/new_name.py"));
    }

    [Fact]
    public async Task Pure_rename_moves_content_unchanged()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "same\r\ncontent\r\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.txt", "*** Move to: b.txt", "*** End Patch"));

        Assert.False(ws.Exists("a.txt"));
        Assert.Equal("same\r\ncontent\r\n", ws.ReadText("b.txt"));
    }

    [Fact]
    public async Task Move_onto_an_existing_file_is_rejected()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "a\n");
        ws.Write("b.txt", "b\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Apply(ws, P("*** Begin Patch", "*** Update File: a.txt", "*** Move to: b.txt", "@@", "-a", "+A", "*** End Patch")));

        Assert.Contains("destination already exists", error.Message);
        Assert.Equal("a\n", ws.ReadText("a.txt"));
        Assert.Equal("b\n", ws.ReadText("b.txt"));
    }

    [Fact]
    public async Task Trailing_whitespace_differences_are_tolerated_and_context_is_kept_verbatim()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "keep   \nold\nkeep too\t\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.txt", "@@", " keep", "-old", "+new", " keep too", "*** End Patch"));

        Assert.Equal("keep   \nnew\nkeep too\t\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Indentation_differences_are_tolerated()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.go", "func f() {\n\tif x {\n\t\treturn 1\n\t}\n}\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.go", "@@", "    if x {", "-        return 1", "+\t\treturn 2", "    }", "*** End Patch"));

        Assert.Equal("func f() {\n\tif x {\n\t\treturn 2\n\t}\n}\n", ws.ReadText("a.go"));
    }

    [Fact]
    public async Task Unicode_punctuation_differences_are_tolerated()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.md", "Title \u2014 \u201CQuoted\u201D\nIt\u2019s old\nend\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.md", "@@", " Title - \"Quoted\"", "-It's old", "+It's new", " end", "*** End Patch"));

        Assert.Equal("Title \u2014 \u201CQuoted\u201D\nIt's new\nend\n", ws.ReadText("a.md"));
    }

    [Fact]
    public async Task Crlf_files_stay_crlf()
    {
        await using var ws = new TestWorkspace();
        ws.Write("w.txt", "a\r\nb\r\nc\r\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: w.txt", "@@", " a", "-b", "+B", "+B2", " c", "*** End Patch"));

        Assert.Equal("a\r\nB\r\nB2\r\nc\r\n", ws.ReadText("w.txt"));
    }

    [Fact]
    public async Task Crlf_patches_apply_to_lf_files()
    {
        await using var ws = new TestWorkspace();
        ws.Write("u.txt", "a\nb\n");

        await Apply(ws, "*** Begin Patch\r\n*** Update File: u.txt\r\n@@\r\n a\r\n-b\r\n+c\r\n*** End Patch\r\n");

        Assert.Equal("a\nc\n", ws.ReadText("u.txt"));
    }

    [Fact]
    public async Task Missing_trailing_newline_and_bom_are_preserved()
    {
        await using var ws = new TestWorkspace();
        ws.Write("n.txt", "a\nb");
        ws.WriteBytes("bom.txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("x\n")]);

        await Apply(ws, P("*** Begin Patch", "*** Update File: n.txt", "@@", " a", "-b", "+B", "*** Update File: bom.txt", "@@", "-x", "+y", "*** End Patch"));

        Assert.Equal("a\nB", ws.ReadText("n.txt"));
        Assert.Equal([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("y\n")], ws.ReadBytes("bom.txt"));
    }

    [Fact]
    public async Task Heredoc_wrapped_patches_apply()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "one\n");

        await Apply(ws, "apply_patch <<'EOF'\n*** Begin Patch\n*** Update File: a.txt\n@@\n-one\n+two\n*** End Patch\nEOF");

        Assert.Equal("two\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Failing_hunks_leave_every_file_untouched()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "a\n");
        ws.Write("b.txt", "b\n");
        ws.Write("gone.txt", "still here\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(ws, P(
            "*** Begin Patch",
            "*** Add File: c.txt",
            "+new",
            "*** Update File: a.txt",
            "@@",
            "-a",
            "+A",
            "*** Delete File: gone.txt",
            "*** Update File: b.txt",
            "@@ def nowhere():",
            " missing context",
            "-b",
            "+B",
            "*** End Patch")));

        Assert.Equal(
            "Failed to apply hunk 1 of 1 to 'b.txt' (patch line 10); the context hint '@@ def nowhere():' was not found either. " +
            "Could not find these lines in the file:\nmissing context\nb\n" +
            "No files were changed. Re-read the file and regenerate the patch with exact context lines.",
            error.Message);
        Assert.False(ws.Exists("c.txt"));
        Assert.Equal("a\n", ws.ReadText("a.txt"));
        Assert.Equal("b\n", ws.ReadText("b.txt"));
        Assert.Equal("still here\n", ws.ReadText("gone.txt"));
    }

    [Fact]
    public async Task Updating_a_missing_or_binary_file_fails()
    {
        await using var ws = new TestWorkspace();
        ws.WriteBytes("b.bin", [1, 0, 2]);

        var missing = await Assert.ThrowsAsync<FileNotFoundException>(
            () => Apply(ws, P("*** Begin Patch", "*** Update File: missing.txt", "@@", "-a", "+b", "*** End Patch")));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => Apply(ws, P("*** Begin Patch", "*** Update File: b.bin", "@@", "-a", "+b", "*** End Patch")));

        Assert.Equal(
            "Cannot update 'missing.txt' (patch line 2): file not found. To create a new file, use '*** Add File: missing.txt' " +
            "and prefix every line of its content with '+'.",
            missing.Message);
    }

    [Fact]
    public async Task Hint_chains_locate_a_repeated_snippet()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.py", "class First:\n    def render(self):\n        return 1\n\nclass Second:\n    def render(self):\n        return 1\n");

        await Apply(ws, P(
            "*** Begin Patch",
            "*** Update File: a.py",
            "@@ class Second:",
            "@@     def render(self):",
            "-        return 1",
            "+        return 2",
            "*** End Patch"));

        Assert.Equal("class First:\n    def render(self):\n        return 1\n\nclass Second:\n    def render(self):\n        return 2\n", ws.ReadText("a.py"));
    }

    [Fact]
    public async Task Added_lines_without_context_go_after_the_innermost_hint()
    {
        await using var ws = new TestWorkspace();
        ws.Write("test_a.py", "import a\n\ndef test_a():\n    assert a.f() == 1\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: test_a.py", "@@ import a", "@@ def test_a():", "+    assert a.g() == 2", "*** End Patch"));

        Assert.Equal("import a\n\ndef test_a():\n    assert a.g() == 2\n    assert a.f() == 1\n", ws.ReadText("test_a.py"));
    }

    [Fact]
    public async Task Added_lines_under_a_missing_hint_fail_instead_of_guessing_a_position()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.py", "x = 1\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Apply(ws, P("*** Begin Patch", "*** Update File: a.py", "@@ def missing():", "+    pass", "*** End Patch")));

        Assert.StartsWith(
            "Failed to apply hunk 1 of 1 to 'a.py' (patch line 3): the context hint '@@ def missing():' was not found, so there is nowhere",
            error.Message);
        Assert.Equal("x = 1\n", ws.ReadText("a.py"));
    }

    [Fact]
    public async Task Patch_shapes_seen_from_small_local_models_apply()
    {
        // Modeled on Qwen2.5-7B output: '*** End Patch' between file sections, stacked @@ lines, and an addition-only hunk.
        await using var ws = new TestWorkspace();
        ws.Write("src/fizzbuzz.py", "def fizzbuzz():\n    return [str(n) for n in range(1, 100)]\n");
        ws.Write("src/test_fizzbuzz.py", "import fizzbuzz\n\ndef test_fizzbuzz():\n    pass\n");

        var output = await Apply(ws, P(
            "*** Begin Patch",
            "*** Update File: src/fizzbuzz.py",
            "@@ def fizzbuzz():",
            "-    return [str(n) for n in range(1, 100)]",
            "+    return [str(n) for n in range(1, 101)]",
            "*** End Patch",
            "*** Update File: src/test_fizzbuzz.py",
            "@@ import fizzbuzz",
            "@@ def test_fizzbuzz():",
            "+    assert len(fizzbuzz.fizzbuzz()) == 100",
            "*** End Patch"));

        Assert.Contains("src/fizzbuzz.py", output.Text);
        Assert.Contains("src/test_fizzbuzz.py", output.Text);
        Assert.Equal("def fizzbuzz():\n    return [str(n) for n in range(1, 101)]\n", ws.ReadText("src/fizzbuzz.py"));
        Assert.Equal("import fizzbuzz\n\ndef test_fizzbuzz():\n    assert len(fizzbuzz.fizzbuzz()) == 100\n    pass\n", ws.ReadText("src/test_fizzbuzz.py"));
    }

    [Fact]
    public async Task Several_operations_are_summarized_in_order()
    {
        await using var ws = new TestWorkspace();
        ws.Write("y.txt", "y\n");
        ws.Write("z.txt", "z\n");

        var output = await Apply(ws, P(
            "*** Begin Patch",
            "*** Add File: x.txt",
            "+x",
            "*** Update File: y.txt",
            "@@",
            "-y",
            "+Y",
            "*** Delete File: z.txt",
            "*** End Patch"));

        Assert.Equal("Success. Updated the following files:\nA x.txt\nM y.txt\nD z.txt", output.Text);
        Assert.Equal("x\n", ws.ReadText("x.txt"));
        Assert.Equal("Y\n", ws.ReadText("y.txt"));
        Assert.False(ws.Exists("z.txt"));
    }

    [Fact]
    public async Task Pure_additions_go_after_the_hint_or_at_the_end()
    {
        await using var ws = new TestWorkspace();
        ws.Write("conf.ini", "[section]\nkey=1\n");
        ws.Write("list.txt", "a\nb\n");

        await Apply(ws, P(
            "*** Begin Patch",
            "*** Update File: conf.ini",
            "@@ [section]",
            "+new=2",
            "*** Update File: list.txt",
            "@@",
            "+c",
            "*** End Patch"));

        Assert.Equal("[section]\nnew=2\nkey=1\n", ws.ReadText("conf.ini"));
        Assert.Equal("a\nb\nc\n", ws.ReadText("list.txt"));
    }

    [Fact]
    public async Task Blank_context_lines_without_a_leading_space_match_empty_lines()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "a\n\nb\n");

        await Apply(ws, P("*** Begin Patch", "*** Update File: a.txt", " a", "", "-b", "+B", "*** End Patch"));

        Assert.Equal("a\n\nB\n", ws.ReadText("a.txt"));
    }

    [Fact]
    public async Task Operations_on_the_same_file_compose()
    {
        await using var ws = new TestWorkspace();
        ws.Write("a.txt", "1\n2\n");
        ws.Write("re.txt", "old\n");

        await Apply(ws, P(
            "*** Begin Patch",
            "*** Update File: a.txt",
            "@@",
            "-1",
            "+one",
            "*** Update File: a.txt",
            "@@",
            "-2",
            "+two",
            "*** Delete File: re.txt",
            "*** Add File: re.txt",
            "+fresh",
            "*** End Patch"));

        Assert.Equal("one\ntwo\n", ws.ReadText("a.txt"));
        Assert.Equal("fresh\n", ws.ReadText("re.txt"));
    }

    [Fact]
    public async Task Absolute_paths_and_the_input_alias_are_accepted()
    {
        await using var ws = new TestWorkspace();
        var path = ws.Write("abs.txt", "before\n");

        var output = await ws.RunAsync(CoreTools.ApplyPatch(), Args.Of(("input", P("*** Begin Patch", "*** Update File: " + path, "@@", "-before", "+after", "*** End Patch"))));

        Assert.Equal("Success. Updated the following files:\nM " + path, output.Text);
        Assert.Equal("after\n", ws.ReadText("abs.txt"));
    }

    [Fact]
    public async Task Invalid_requests_are_reported()
    {
        await using var ws = new TestWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(CoreTools.ApplyPatch(), """{}"""));
        var empty = await Assert.ThrowsAsync<FormatException>(() => Apply(ws, P("*** Begin Patch", "*** End Patch")));
        var malformed = await Assert.ThrowsAsync<PatchFormatException>(() => Apply(ws, "garbage"));

        Assert.Equal("Invalid patch: it contains no file operations.", empty.Message);
        Assert.StartsWith("Invalid patch (line 1)", malformed.Message);
    }
}

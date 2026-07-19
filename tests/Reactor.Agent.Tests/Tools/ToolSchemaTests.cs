using System.Text.Json.Nodes;
using Reactor.Agent.Tools;
using Reactor.Llm;
using Reactor.Llm.Utilities;

namespace Reactor.Agent.Tests.Tools;

public class ToolSchemaTests
{
    private static RegisteredTool[] AllTools() =>
    [
        CoreTools.ReadFile(), CoreTools.WriteFile(), CoreTools.EditFile(), CoreTools.EditFileGemini(), CoreTools.Shell(),
        CoreTools.Grep(), CoreTools.Grep(includeOutputModes: true), CoreTools.Glob(), CoreTools.ListDir(),
        CoreTools.ReadManyFiles(), CoreTools.ApplyPatch(), CoreTools.WebFetch(),
    ];

    [Fact]
    public void Every_tool_has_a_valid_object_schema_with_described_properties()
    {
        foreach (var tool in AllTools())
        {
            var definition = tool.Definition;
            Assert.True(ToolDefinition.IsValidName(definition.Name), definition.Name);
            Assert.True(definition.Description.Length > 80, $"{definition.Name} needs a real description");
            Assert.Equal("object", definition.Parameters["type"]!.GetValue<string>());
            var properties = Assert.IsType<JsonObject>(definition.Parameters["properties"]);
            foreach (var (name, schema) in properties)
            {
                var property = Assert.IsType<JsonObject>(schema);
                Assert.False(string.IsNullOrWhiteSpace(property["type"]?.GetValue<string>()), $"{definition.Name}.{name} type");
                Assert.False(string.IsNullOrWhiteSpace(property["description"]?.GetValue<string>()), $"{definition.Name}.{name} description");
            }

            foreach (var required in definition.Parameters["required"]?.AsArray() ?? [])
            {
                Assert.True(properties.ContainsKey(required!.GetValue<string>()), $"{definition.Name} requires unknown {required}");
            }

            // Round-trips through the constructor's validation.
            _ = new ToolDefinition(definition.Name, definition.Description, (JsonObject)definition.Parameters.DeepClone());
        }
    }

    [Theory]
    [InlineData("ReadFile", "read_file", "file_path", "file_path,offset,limit")]
    [InlineData("WriteFile", "write_file", "file_path,content", "file_path,content")]
    [InlineData("EditFile", "edit_file", "file_path,old_string,new_string", "file_path,old_string,new_string,replace_all")]
    [InlineData("EditFileGemini", "edit_file", "file_path,old_string,new_string", "file_path,old_string,new_string,expected_replacements")]
    [InlineData("Shell", "shell", "command", "command,timeout_ms,description")]
    [InlineData("Grep", "grep", "pattern", "pattern,path,glob_filter,case_insensitive,max_results")]
    [InlineData("Glob", "glob", "pattern", "pattern,path")]
    [InlineData("ListDir", "list_dir", "", "path,depth")]
    [InlineData("ReadManyFiles", "read_many_files", "paths", "paths")]
    [InlineData("ApplyPatch", "apply_patch", "patch", "patch")]
    [InlineData("WebFetch", "web_fetch", "url", "url,max_length")]
    public void Tool_names_and_parameters_match_the_spec(string factory, string name, string required, string properties)
    {
        var tool = factory switch
        {
            "ReadFile" => CoreTools.ReadFile(),
            "WriteFile" => CoreTools.WriteFile(),
            "EditFile" => CoreTools.EditFile(),
            "EditFileGemini" => CoreTools.EditFileGemini(),
            "Shell" => CoreTools.Shell(),
            "Grep" => CoreTools.Grep(),
            "Glob" => CoreTools.Glob(),
            "ListDir" => CoreTools.ListDir(),
            "ReadManyFiles" => CoreTools.ReadManyFiles(),
            "ApplyPatch" => CoreTools.ApplyPatch(),
            _ => CoreTools.WebFetch(),
        };

        Assert.Equal(name, tool.Name);
        var actualRequired = tool.Definition.Parameters["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [];
        Assert.Equal(required.Split(',', StringSplitOptions.RemoveEmptyEntries), actualRequired);
        Assert.Equal(properties.Split(','), tool.Definition.Parameters["properties"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Grep_output_modes_are_only_offered_when_requested()
    {
        var plain = CoreTools.Grep().Definition.Parameters["properties"]!.AsObject();
        var withModes = CoreTools.Grep(includeOutputModes: true).Definition.Parameters["properties"]!.AsObject();

        Assert.False(plain.ContainsKey("output_mode"));
        Assert.False(plain.ContainsKey("context"));
        Assert.Equal(
            new[] { "content", "files_with_matches", "count" },
            withModes["output_mode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("integer", withModes["context"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Each_factory_call_returns_independent_schema_objects()
    {
        var first = CoreTools.ReadFile();
        var second = CoreTools.ReadFile();

        first.Definition.Parameters["properties"]!.AsObject().Remove("offset");

        Assert.True(second.Definition.Parameters["properties"]!.AsObject().ContainsKey("offset"));
    }

    [Theory]
    [InlineData("read_file", """{"file_path":"a.txt","offset":3,"limit":10}""")]
    [InlineData("edit_file", """{"file_path":"a.txt","old_string":"a","new_string":"b","replace_all":true}""")]
    [InlineData("shell", """{"command":"ls","timeout_ms":5000,"description":"list"}""")]
    [InlineData("grep", """{"pattern":"x","output_mode":"count","context":2,"max_results":5}""")]
    [InlineData("read_many_files", """{"paths":["a.txt","src/**/*.cs"]}""")]
    [InlineData("list_dir", """{}""")]
    public void Typical_arguments_validate_against_the_schemas(string name, string json)
    {
        var registry = new ToolRegistry(AllTools());
        var tool = registry.Get(name)!;

        Assert.Empty(JsonSchemaValidator.Validate(JsonNode.Parse(json), tool.Definition.Parameters));
    }

    [Fact]
    public void Missing_required_arguments_fail_schema_validation()
    {
        Assert.NotEmpty(JsonSchemaValidator.Validate(JsonNode.Parse("{}"), CoreTools.ReadFile().Definition.Parameters));
        Assert.NotEmpty(JsonSchemaValidator.Validate(JsonNode.Parse("""{"paths":"x"}"""), CoreTools.ReadManyFiles().Definition.Parameters));
    }

    [Fact]
    public void Registering_both_edit_variants_keeps_the_latest()
    {
        var registry = new ToolRegistry([CoreTools.EditFile(), CoreTools.EditFileGemini()]);

        Assert.Single(registry.Names(), "edit_file");
        Assert.True(registry.Get("edit_file")!.Definition.Parameters["properties"]!.AsObject().ContainsKey("expected_replacements"));
    }
}

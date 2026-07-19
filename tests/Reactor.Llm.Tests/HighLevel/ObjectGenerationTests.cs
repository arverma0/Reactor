using System.Text.Json.Nodes;
using Reactor.Llm.Tests.Support;

namespace Reactor.Llm.Tests.HighLevel;

public class ObjectGenerationTests
{
    private static readonly JsonObject PersonSchema = (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}},"required":["name","age"],"additionalProperties":false}
        """)!;

    [Fact]
    public async Task Native_strategy_uses_json_schema_response_format()
    {
        var adapter = new ScriptedAdapter("openai").ThenText("""{"name":"Alice","age":30}""");

        var result = await Ai.GenerateObjectAsync(new GenerateObjectOptions
        {
            Model = "gpt-5.2",
            Prompt = "Extract: Alice is 30 years old",
            Schema = PersonSchema,
            Client = Client.Create(adapter),
        });

        Assert.Equal("Alice", result.Output!["name"]!.GetValue<string>());
        Assert.Equal(30, result.Output["age"]!.GetValue<int>());
        var format = adapter.Requests[0].ResponseFormat!;
        Assert.Equal(ResponseFormat.JsonSchemaType, format.Type);
        Assert.True(format.Strict);
        var person = result.OutputAs<Person>();
        Assert.Equal(new Person("Alice", 30), person);
    }

    [Fact]
    public async Task Tool_strategy_forces_a_named_tool_call_for_anthropic()
    {
        var adapter = new ScriptedAdapter("anthropic").ThenToolCalls(("t1", "person", """{"name":"Bob","age":41}"""));

        var result = await Ai.GenerateObjectAsync(new GenerateObjectOptions
        {
            Model = "claude-opus-4-6",
            Prompt = "Extract: Bob is 41",
            Schema = PersonSchema,
            SchemaName = "person",
            Client = Client.Create(adapter),
        });

        Assert.Equal("Bob", result.Output!["name"]!.GetValue<string>());
        var request = adapter.Requests[0];
        Assert.Equal(ToolChoice.NamedMode, request.ToolChoice!.Mode);
        Assert.Equal("person", request.ToolChoice.ToolName);
        Assert.Equal("person", Assert.Single(request.Tools!).Name);
        Assert.Null(request.ResponseFormat);
    }

    [Fact]
    public async Task Tool_strategy_wraps_non_object_schemas()
    {
        var adapter = new ScriptedAdapter("anthropic").ThenToolCalls(("t1", "colors", """{"value":["red","green"]}"""));
        var schema = (JsonObject)JsonNode.Parse("""{"type":"array","items":{"type":"string"}}""")!;

        var result = await Ai.GenerateObjectAsync(new GenerateObjectOptions
        {
            Model = "claude-opus-4-6",
            Prompt = "List colors",
            Schema = schema,
            SchemaName = "colors",
            Client = Client.Create(adapter),
        });

        Assert.Equal("""["red","green"]""", result.Output!.ToJsonString());
    }

    [Fact]
    public async Task Prompt_strategy_injects_schema_and_parses_fenced_json()
    {
        var adapter = new ScriptedAdapter("custom").ThenText("Sure!\n```json\n{\"name\":\"Cy\",\"age\":7}\n```");

        var result = await Ai.GenerateObjectAsync(new GenerateObjectOptions
        {
            Model = "local-model",
            Prompt = "Extract",
            Schema = PersonSchema,
            Client = Client.Create(adapter),
        });

        Assert.Equal("Cy", result.Output!["name"]!.GetValue<string>());
        Assert.Contains("JSON Schema", adapter.Requests[0].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_json_and_schema_violations_raise_no_object_generated()
    {
        var notJson = new ScriptedAdapter("openai").ThenText("I cannot do that");
        var wrongShape = new ScriptedAdapter("openai").ThenText("""{"name":"Al"}""");

        var e1 = await Assert.ThrowsAsync<NoObjectGeneratedError>(() => Ai.GenerateObjectAsync(new GenerateObjectOptions
        {
            Model = "gpt-5.2",
            Prompt = "x",
            Schema = PersonSchema,
            Client = Client.Create(notJson),
        }));
        Assert.Equal("I cannot do that", e1.Text);

        var e2 = await Assert.ThrowsAsync<NoObjectGeneratedError>(() => Ai.GenerateObjectAsync(new GenerateObjectOptions
        {
            Model = "gpt-5.2",
            Prompt = "x",
            Schema = PersonSchema,
            Client = Client.Create(wrongShape),
        }));
        Assert.Contains("age", e2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_mode_is_only_requested_for_compatible_schemas()
    {
        Assert.True(ObjectGeneration.IsStrictCompatible(PersonSchema));
        Assert.False(ObjectGeneration.IsStrictCompatible(JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"}}}""")));
        Assert.False(ObjectGeneration.IsStrictCompatible(JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}},"required":["a"],"additionalProperties":false}""")));
    }

    [Fact]
    public async Task Stream_object_yields_growing_partials_and_a_validated_final_object()
    {
        var adapter = new ScriptedAdapter("openai").ThenText("""{"name":"Alexandria","age":33}""");
        var stream = Ai.StreamObject(new GenerateObjectOptions
        {
            Model = "gpt-5.2",
            Prompt = "x",
            Schema = PersonSchema,
            Client = Client.Create(adapter),
        });

        var partials = new List<string>();
        await foreach (var partial in stream)
        {
            partials.Add(partial.ToJsonString());
        }

        Assert.True(partials.Count >= 3);
        Assert.Contains(partials, p => p.Contains("\"name\":\"Ale", StringComparison.Ordinal) && !p.Contains("age", StringComparison.Ordinal));
        var final = await stream.GetObjectAsync();
        Assert.Equal(33, final["age"]!.GetValue<int>());
        Assert.Equal(final.ToJsonString(), partials[^1]);
    }

    [Fact]
    public async Task Stream_object_tool_strategy_tracks_argument_deltas()
    {
        var adapter = new ScriptedAdapter("anthropic").ThenToolCalls(("t", "response", """{"name":"Zed","age":5}"""));
        var stream = Ai.StreamObject(new GenerateObjectOptions
        {
            Model = "claude-opus-4-6",
            Prompt = "x",
            Schema = PersonSchema,
            Client = Client.Create(adapter),
        });

        var count = 0;
        await foreach (var _ in stream)
        {
            count++;
        }

        Assert.True(count >= 2);
        Assert.Equal("Zed", (await stream.GetObjectAsync())["name"]!.GetValue<string>());
    }

    private sealed record Person(string Name, int Age);
}

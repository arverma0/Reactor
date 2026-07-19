using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Gemini;

namespace Reactor.Llm.Tests.Providers;

public class GeminiSchemaSanitizerTests
{
    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual?.ToJsonString());

    [Fact]
    public void Keeps_supported_keywords_and_drops_the_rest()
    {
        var schema = Parse("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "urn:args",
              "type": "object",
              "title": "Args",
              "additionalProperties": false,
              "properties": {
                "path": {"type": "string", "description": "File path", "default": "/x", "examples": ["a"], "format": "uri"},
                "mode": {"const": "fast"},
                "when": {"type": "string", "format": "date-time"},
                "ratio": {"type": ["number", "null"], "minimum": 0, "maximum": 1, "format": "double", "exclusiveMaximum": 2},
                "tags": {"type": "array", "items": {"type": "string", "minLength": 1, "pattern": "^[a-z]+$"}, "minItems": 1, "uniqueItems": true},
                "meta": {"type": "object", "patternProperties": {"^x-": {"type": "string"}}, "propertyOrdering": ["a"]}
              },
              "required": ["path", "missing"]
            }
            """);
        var original = schema.DeepClone();

        var sanitized = GeminiSchemaSanitizer.Sanitize(schema);

        AssertJson("""
            {
              "type": "object",
              "title": "Args",
              "properties": {
                "path": {"type": "string", "description": "File path"},
                "mode": {"type": "string", "enum": ["fast"]},
                "when": {"type": "string", "format": "date-time"},
                "ratio": {"type": "number", "nullable": true, "format": "double", "minimum": 0, "maximum": 1},
                "tags": {"type": "array", "minItems": 1, "items": {"type": "string", "minLength": 1, "pattern": "^[a-z]+$"}},
                "meta": {"type": "object", "propertyOrdering": ["a"]}
              },
              "required": ["path"]
            }
            """, sanitized);
        Assert.True(JsonNode.DeepEquals(original, schema), "The input schema must not be mutated.");
    }

    [Fact]
    public void Inlines_local_references_and_breaks_recursion()
    {
        var schema = Parse("""
            {
              "type": "object",
              "$defs": {
                "Address": {"type": "object", "properties": {"street": {"type": "string"}, "zip": {"type": "string"}}, "required": ["street"]},
                "Node": {"type": "object", "properties": {"value": {"type": "integer"}, "children": {"type": "array", "items": {"$ref": "#/$defs/Node"}}}}
              },
              "definitions": {"Color": {"type": "string", "enum": ["red", "green"]}},
              "properties": {
                "home": {"$ref": "#/$defs/Address", "description": "Home address"},
                "color": {"$ref": "#/definitions/Color"},
                "tree": {"$ref": "#/$defs/Node"},
                "external": {"$ref": "https://example.com/schema.json"}
              }
            }
            """);

        var sanitized = GeminiSchemaSanitizer.Sanitize(schema);

        AssertJson("""
            {
              "type": "object",
              "properties": {
                "home": {"type": "object", "description": "Home address", "properties": {"street": {"type": "string"}, "zip": {"type": "string"}}, "required": ["street"]},
                "color": {"type": "string", "enum": ["red", "green"]},
                "tree": {"type": "object", "properties": {"value": {"type": "integer"}, "children": {"type": "array", "items": {"type": "object"}}}},
                "external": {"type": "object"}
              }
            }
            """, sanitized);
    }

    [Fact]
    public void Converts_combinators()
    {
        var schema = Parse("""
            {
              "type": "object",
              "properties": {
                "maybe": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "Optional"},
                "either": {"oneOf": [{"type": "string"}, {"type": "integer"}]},
                "multi": {"type": ["string", "integer", "null"]},
                "merged": {"allOf": [{"type": "object", "properties": {"a": {"type": "string"}}, "required": ["a"]}, {"properties": {"b": {"type": "integer"}}}]}
              }
            }
            """);

        var sanitized = GeminiSchemaSanitizer.Sanitize(schema);

        AssertJson("""
            {
              "type": "object",
              "properties": {
                "maybe": {"type": "string", "nullable": true, "description": "Optional"},
                "either": {"anyOf": [{"type": "string"}, {"type": "integer"}]},
                "multi": {"anyOf": [{"type": "string"}, {"type": "integer"}], "nullable": true},
                "merged": {"type": "object", "properties": {"a": {"type": "string"}, "b": {"type": "integer"}}, "required": ["a"]}
              }
            }
            """, sanitized);
    }

    [Fact]
    public void Moves_non_string_enums_to_the_description_and_fills_missing_array_items()
    {
        var schema = Parse("""
            {"type":"object","properties":{
              "level":{"type":"integer","enum":[1,2,3],"description":"Level"},
              "anything":{"type":"array"},
              "pair":{"type":"array","items":[{"type":"string"},{"type":"integer"}]}}}
            """);

        var sanitized = GeminiSchemaSanitizer.Sanitize(schema);

        AssertJson("""
            {"type":"object","properties":{
              "level":{"type":"integer","description":"Level (Allowed values: 1, 2, 3.)"},
              "anything":{"type":"array","items":{"type":"string"}},
              "pair":{"type":"array","items":{"type":"string"}}}}
            """, sanitized);
    }

    [Fact]
    public void Parameters_without_properties_are_omitted()
    {
        Assert.Null(GeminiSchemaSanitizer.SanitizeParameters(Parse("""{"type":"object","properties":{},"additionalProperties":false}""")));
        Assert.Null(GeminiSchemaSanitizer.SanitizeParameters(Parse("""{"type":"object"}""")));
        Assert.NotNull(GeminiSchemaSanitizer.SanitizeParameters(Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""")));
    }

    [Fact]
    public void Handles_in_memory_numeric_values()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100L, ["default"] = 10 },
                ["score"] = new JsonObject { ["type"] = "number", ["minimum"] = 0.5m },
            },
        };

        var sanitized = GeminiSchemaSanitizer.Sanitize(schema);

        AssertJson("""
            {"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":100},"score":{"type":"number","minimum":0.5}}}
            """, sanitized);
    }
}

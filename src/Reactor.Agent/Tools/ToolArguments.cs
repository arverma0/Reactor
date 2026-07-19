using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Agent.Tools;

/// <summary>
/// Reads tool arguments with clear, model-facing error messages. Values may be backed by parsed JSON or created in memory
/// from CLR values (which are type-exact), so numbers are read through every numeric representation.
/// </summary>
internal static class ToolArguments
{
    public static string RequireString(JsonObject arguments, string name) =>
        OptionalString(arguments, name) ?? throw new ArgumentException($"Missing required parameter '{name}'.");

    public static string? OptionalString(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }

        throw new ArgumentException($"Parameter '{name}' must be a string.");
    }

    public static int? OptionalInt(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (TryGetInteger(value, out var result))
            {
                return result;
            }

            if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }

        throw new ArgumentException($"Parameter '{name}' must be an integer.");
    }

    public static bool? OptionalBool(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            if (value.TryGetValue<string>(out var text) && bool.TryParse(text.Trim(), out var parsed))
            {
                return parsed;
            }

            if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }

        throw new ArgumentException($"Parameter '{name}' must be a boolean (true or false).");
    }

    /// <summary>A required array of strings; a single string is accepted as a one-element array.</summary>
    public static IReadOnlyList<string> RequireStringArray(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            throw new ArgumentException($"Missing required parameter '{name}'.");
        }

        if (node is JsonValue single && single.TryGetValue<string>(out var only))
        {
            return [only];
        }

        if (node is not JsonArray array)
        {
            throw new ArgumentException($"Parameter '{name}' must be an array of strings.");
        }

        var items = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                items.Add(text);
            }
            else
            {
                throw new ArgumentException($"Parameter '{name}' must be an array of strings.");
            }
        }

        return items;
    }

    /// <summary>Reads an integral number from any numeric representation (or a numeric string).</summary>
    internal static bool TryGetInteger(JsonValue value, out int result)
    {
        result = 0;
        if (value.TryGetValue<int>(out result))
        {
            return true;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        if (value.TryGetValue<bool>(out _))
        {
            return false;
        }

        // Covers long, short, byte, double, float, decimal, ... (in-memory values are type-exact) and JSON numbers.
        if (decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            number == decimal.Truncate(number))
        {
            result = number > int.MaxValue ? int.MaxValue : number < int.MinValue ? int.MinValue : (int)number;
            return true;
        }

        if (double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var real) &&
            double.IsFinite(real) && real == Math.Floor(real))
        {
            result = (int)Math.Clamp(real, int.MinValue, int.MaxValue);
            return true;
        }

        return false;
    }
}

/// <summary>Small builders for JSON Schema tool parameter definitions.</summary>
internal static class ToolSchema
{
    public static JsonObject Object(IEnumerable<(string Name, JsonObject Schema)> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (required.Length > 0)
        {
            result["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        }

        return result;
    }

    public static JsonObject String(string description) => Typed("string", description);

    public static JsonObject Integer(string description) => Typed("integer", description);

    public static JsonObject Boolean(string description) => Typed("boolean", description);

    public static JsonObject StringArray(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    public static JsonObject StringEnum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
        ["description"] = description,
    };

    private static JsonObject Typed(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };
}

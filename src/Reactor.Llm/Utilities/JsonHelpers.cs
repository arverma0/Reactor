using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Reactor.Llm.Utilities;

/// <summary>JSON helpers shared by adapters and the high-level API.</summary>
public static class JsonHelpers
{
    /// <summary>Web defaults, nulls omitted.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Renders a node as text: JSON strings verbatim, everything else as compact JSON, null as empty.</summary>
    public static string NodeToText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s;
        }

        return node.ToJsonString();
    }

    /// <summary>Converts an arbitrary value (string, JsonNode, JsonElement, POCO) to a detached JsonNode.</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        JsonDocument document => JsonNode.Parse(document.RootElement.GetRawText()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions),
    };

    /// <summary>Parses JSON text, returning null instead of throwing on invalid input.</summary>
    public static JsonNode? ParseOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Gets a string property, or null when absent or not a string.</summary>
    public static string? Str(this JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(property, out var value) && value is JsonValue v &&
            v.GetValueKind() == JsonValueKind.String)
        {
            return v.GetValue<string>();
        }

        return null;
    }

    /// <summary>Gets an integer property (numbers or numeric strings), or null.</summary>
    public static int? Int(this JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(property, out var value) && value is JsonValue v)
        {
            switch (v.GetValueKind())
            {
                case JsonValueKind.Number:
                    if (v.TryGetValue<int>(out var i))
                    {
                        return i;
                    }

                    if (v.TryGetValue<long>(out var l))
                    {
                        return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
                    }

                    if (v.TryGetValue<double>(out var d))
                    {
                        return (int)d;
                    }

                    break;
                case JsonValueKind.String:
                    if (int.TryParse(v.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return parsed;
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>Gets a boolean property, or null.</summary>
    public static bool? Bool(this JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(property, out var value) && value is JsonValue v)
        {
            return v.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>Gets an object property, or null.</summary>
    public static JsonObject? Obj(this JsonNode? node, string property) =>
        node is JsonObject obj && obj.TryGetPropertyValue(property, out var value) ? value as JsonObject : null;

    /// <summary>Gets an array property, or null.</summary>
    public static JsonArray? Arr(this JsonNode? node, string property) =>
        node is JsonObject obj && obj.TryGetPropertyValue(property, out var value) ? value as JsonArray : null;

    /// <summary>Deep-merges <paramref name="source"/> into <paramref name="target"/> (objects merge recursively; other values replace).</summary>
    public static void DeepMerge(JsonObject target, JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObj && target[key] is JsonObject targetObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}

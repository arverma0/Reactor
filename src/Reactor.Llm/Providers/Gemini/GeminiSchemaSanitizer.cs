using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Gemini;

/// <summary>
/// Converts JSON Schema into the OpenAPI 3.0 subset accepted by Gemini function declarations and response schemas.
/// </summary>
/// <remarks>
/// Kept: <c>type, format, description, nullable, enum, items, properties, required, minItems, maxItems, minimum, maximum,
/// minLength, maxLength, pattern, anyOf, title, propertyOrdering, minProperties, maxProperties</c>. Converted:
/// <c>"type": [T, "null"]</c> → <c>type T + nullable</c>; <c>const</c> → <c>enum</c>; <c>oneOf</c> → <c>anyOf</c>;
/// <c>allOf</c> members are merged; local <c>$ref</c>s (<c>#/$defs/...</c>, <c>#/definitions/...</c>, JSON pointers) are
/// inlined, with recursive or unresolvable references replaced by a plain object schema. Dropped: everything else
/// (<c>additionalProperties, $schema, $id, default, examples, patternProperties</c>, ...). Gemini also rejects formats
/// other than <c>enum</c>/<c>date-time</c> (strings), <c>float</c>/<c>double</c>, and <c>int32</c>/<c>int64</c>,
/// non-string enum values (moved into the description), <c>required</c> names that are not declared properties, and
/// arrays without <c>items</c> (given string items).
/// </remarks>
internal static class GeminiSchemaSanitizer
{
    private const int MaxDepth = 32;

    private static readonly string[] CopiedNumbers =
        ["minItems", "maxItems", "minimum", "maximum", "minLength", "maxLength", "minProperties", "maxProperties"];

    /// <summary>Sanitizes a response or nested schema.</summary>
    public static JsonObject Sanitize(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return SanitizeNode(schema, schema, new HashSet<string>(StringComparer.Ordinal), 0);
    }

    /// <summary>
    /// Sanitizes function parameters. Returns null for an object schema without properties: Gemini rejects empty
    /// <c>properties</c> for OBJECT parameters, and a parameterless function simply omits <c>parameters</c>.
    /// </summary>
    public static JsonObject? SanitizeParameters(JsonObject schema)
    {
        var sanitized = Sanitize(schema);
        var isObject = string.Equals(sanitized.Str("type"), "object", StringComparison.OrdinalIgnoreCase);
        return isObject && sanitized["properties"] is not JsonObject { Count: > 0 } ? null : sanitized;
    }

    private static JsonObject SanitizeNode(JsonObject node, JsonObject root, HashSet<string> refStack, int depth)
    {
        if (depth > MaxDepth)
        {
            return new JsonObject { ["type"] = "object" };
        }

        if (node.Str("$ref") is { } reference)
        {
            return SanitizeReference(node, reference, root, refStack, depth);
        }

        var result = new JsonObject();
        ApplyType(node["type"], result);

        if (node.Str("title") is { } title)
        {
            result["title"] = title;
        }

        var description = node.Str("description");
        if (node.Str("format") is { } format && IsSupportedFormat(result.Str("type"), format))
        {
            result["format"] = format;
        }

        if (node.Bool("nullable") == true)
        {
            result["nullable"] = true;
        }

        var enumValues = node["enum"] as JsonArray;
        if (node.TryGetPropertyValue("const", out var constValue))
        {
            enumValues = new JsonArray(constValue?.DeepClone());
        }

        if (enumValues is not null)
        {
            if (enumValues.Count > 0 && enumValues.All(v => v is JsonValue sv && sv.GetValueKind() == JsonValueKind.String))
            {
                result["enum"] = enumValues.DeepClone();
                result["type"] ??= "string";
            }
            else if (enumValues.Count > 0)
            {
                var allowed = string.Join(", ", enumValues.Select(v => v?.ToJsonString() ?? "null"));
                description = string.IsNullOrEmpty(description) ? $"Allowed values: {allowed}." : $"{description} (Allowed values: {allowed}.)";
            }
        }

        if (description is not null)
        {
            result["description"] = description;
        }

        foreach (var keyword in CopiedNumbers)
        {
            if (node[keyword] is JsonValue number && number.GetValueKind() == JsonValueKind.Number)
            {
                result[keyword] = number.DeepClone();
            }
        }

        if (node.Str("pattern") is { } pattern)
        {
            result["pattern"] = pattern;
        }

        if (node["properties"] is JsonObject properties)
        {
            var sanitizedProperties = new JsonObject();
            foreach (var (name, value) in properties)
            {
                sanitizedProperties[name] = value is JsonObject property
                    ? SanitizeNode(property, root, refStack, depth + 1)
                    : new JsonObject();
            }

            if (sanitizedProperties.Count > 0)
            {
                result["properties"] = sanitizedProperties;
            }
        }

        if (node["propertyOrdering"] is JsonArray ordering && ordering.All(v => v is JsonValue ov && ov.GetValueKind() == JsonValueKind.String))
        {
            result["propertyOrdering"] = ordering.DeepClone();
        }

        if (node["items"] is JsonObject items)
        {
            result["items"] = SanitizeNode(items, root, refStack, depth + 1);
        }
        else if (node["items"] is JsonArray { Count: > 0 } tuple && tuple[0] is JsonObject first)
        {
            result["items"] = SanitizeNode(first, root, refStack, depth + 1);
        }

        var alternatives = (node["anyOf"] as JsonArray ?? node["oneOf"] as JsonArray)?.OfType<JsonObject>().ToList();
        if (alternatives is { Count: > 0 })
        {
            ApplyAlternatives(alternatives, result, root, refStack, depth);
        }

        if (node["allOf"] is JsonArray allOf)
        {
            foreach (var member in allOf.OfType<JsonObject>())
            {
                MergeInto(result, SanitizeNode(member, root, refStack, depth + 1));
            }
        }

        FinishNode(node, result);
        return result;
    }

    private static JsonObject SanitizeReference(JsonObject node, string reference, JsonObject root, HashSet<string> refStack, int depth)
    {
        if (ResolveReference(root, reference) is { } target && refStack.Add(reference))
        {
            try
            {
                // Sibling keywords (e.g. a property-specific description) override the referenced definition.
                var merged = (JsonObject)target.DeepClone();
                foreach (var (key, value) in node)
                {
                    if (key != "$ref")
                    {
                        merged[key] = value?.DeepClone();
                    }
                }

                return SanitizeNode(merged, root, refStack, depth + 1);
            }
            finally
            {
                refStack.Remove(reference);
            }
        }

        // Recursive or unresolvable: keep the sibling keywords and fall back to a plain object.
        var rest = (JsonObject)node.DeepClone();
        rest.Remove("$ref");
        var sanitized = SanitizeNode(rest, root, refStack, depth + 1);
        if (sanitized["type"] is null && sanitized["anyOf"] is null)
        {
            sanitized["type"] = "object";
        }

        return sanitized;
    }

    private static void ApplyType(JsonNode? type, JsonObject result)
    {
        switch (type)
        {
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var name = value.GetValue<string>();
                if (string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
                {
                    result["nullable"] = true;
                }
                else
                {
                    result["type"] = name;
                }

                break;
            case JsonArray array:
                var names = array.OfType<JsonValue>()
                    .Where(v => v.GetValueKind() == JsonValueKind.String)
                    .Select(v => v.GetValue<string>())
                    .ToList();
                var nonNull = names.Where(n => !string.Equals(n, "null", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
                if (nonNull.Count == 1)
                {
                    result["type"] = nonNull[0];
                }
                else if (nonNull.Count > 1)
                {
                    var anyOf = new JsonArray();
                    foreach (var n in nonNull)
                    {
                        anyOf.Add(new JsonObject { ["type"] = n });
                    }

                    result["anyOf"] = anyOf;
                }

                if (nonNull.Count < names.Count)
                {
                    result["nullable"] = true;
                }

                break;
        }
    }

    private static void ApplyAlternatives(List<JsonObject> alternatives, JsonObject result, JsonObject root, HashSet<string> refStack, int depth)
    {
        var sanitized = new List<JsonObject>();
        foreach (var alternative in alternatives)
        {
            var s = SanitizeNode(alternative, root, refStack, depth + 1);
            var onlyNull = s.Count == 1 && s.Bool("nullable") == true;
            if (onlyNull)
            {
                result["nullable"] = true;
            }
            else
            {
                sanitized.Add(s);
            }
        }

        if (sanitized.Count == 1)
        {
            MergeInto(result, sanitized[0]);
        }
        else if (sanitized.Count > 1)
        {
            var anyOf = result["anyOf"] as JsonArray ?? new JsonArray();
            foreach (var s in sanitized)
            {
                anyOf.Add(s);
            }

            result["anyOf"] = anyOf;
        }
    }

    /// <summary>Merges <paramref name="source"/> into <paramref name="target"/>: properties and required are unioned; other keys keep the target's value.</summary>
    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is null)
            {
                continue;
            }

            if (key == "properties" && value is JsonObject sourceProperties)
            {
                var properties = target["properties"] as JsonObject ?? new JsonObject();
                foreach (var (name, property) in sourceProperties.ToList())
                {
                    if (!properties.ContainsKey(name))
                    {
                        properties[name] = property?.DeepClone();
                    }
                }

                target["properties"] = properties;
            }
            else if (key == "required" && value is JsonArray sourceRequired)
            {
                var required = target["required"] as JsonArray ?? new JsonArray();
                var existing = required.Select(JsonHelpers.NodeToText).ToHashSet(StringComparer.Ordinal);
                foreach (var name in sourceRequired)
                {
                    if (name is not null && existing.Add(JsonHelpers.NodeToText(name)))
                    {
                        required.Add(name.DeepClone());
                    }
                }

                target["required"] = required;
            }
            else if (!target.ContainsKey(key))
            {
                target[key] = value.DeepClone();
            }
        }
    }

    private static void FinishNode(JsonObject node, JsonObject result)
    {
        var requested = StringValues(node["required"] as JsonArray).Concat(StringValues(result["required"] as JsonArray)).ToList();
        if (requested.Count > 0)
        {
            var declared = result["properties"] as JsonObject;
            var names = new JsonArray();
            foreach (var name in requested.Distinct(StringComparer.Ordinal))
            {
                if (declared?.ContainsKey(name) == true)
                {
                    names.Add(name);
                }
            }

            if (names.Count > 0)
            {
                result["required"] = names;
            }
            else
            {
                result.Remove("required");
            }
        }

        if (string.Equals(result.Str("type"), "array", StringComparison.OrdinalIgnoreCase) && result["items"] is null)
        {
            result["items"] = new JsonObject { ["type"] = "string" };
        }
    }

    private static IEnumerable<string> StringValues(JsonArray? array) =>
        array is null
            ? []
            : array.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>());

    private static bool IsSupportedFormat(string? type, string format) => type?.ToLowerInvariant() switch
    {
        "string" => format is "enum" or "date-time",
        "number" => format is "float" or "double",
        "integer" => format is "int32" or "int64",
        _ => false,
    };

    /// <summary>Resolves a local JSON pointer reference such as <c>#/$defs/Address</c>.</summary>
    private static JsonObject? ResolveReference(JsonObject root, string reference)
    {
        if (!reference.StartsWith('#'))
        {
            return null;
        }

        JsonNode? current = root;
        foreach (var rawSegment in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = Uri.UnescapeDataString(rawSegment).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray arr when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < arr.Count => arr[i],
                _ => null,
            };

            if (current is null)
            {
                return null;
            }
        }

        return current as JsonObject;
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reactor.Llm.Utilities;

/// <summary>
/// A pragmatic JSON Schema validator covering the keywords used for tool parameters and structured output:
/// <c>type</c> (incl. arrays and <c>nullable</c>), <c>enum</c>, <c>const</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c>, <c>patternProperties</c>, <c>items</c>/<c>prefixItems</c>, <c>min/maxItems</c>,
/// <c>uniqueItems</c>, <c>min/maxLength</c>, <c>pattern</c>, numeric bounds, <c>multipleOf</c>,
/// <c>min/maxProperties</c>, <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>/<c>not</c>, and local <c>$ref</c>s.
/// </summary>
public static class JsonSchemaValidator
{
    private const int MaxDepth = 64;

    /// <summary>Validates <paramref name="instance"/> against <paramref name="schema"/>; returns human-readable errors (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(JsonNode? instance, JsonNode? schema)
    {
        var errors = new List<string>();
        if (schema is null)
        {
            return errors;
        }

        ValidateNode(instance, schema, schema, "$", errors, 0);
        return errors;
    }

    /// <summary>True when <paramref name="instance"/> conforms to <paramref name="schema"/>.</summary>
    public static bool IsValid(JsonNode? instance, JsonNode? schema) => Validate(instance, schema).Count == 0;

    private static void ValidateNode(JsonNode? instance, JsonNode schema, JsonNode root, string path, List<string> errors, int depth)
    {
        if (depth > MaxDepth)
        {
            errors.Add($"{path}: schema nesting is too deep");
            return;
        }

        if (schema is JsonValue boolSchema && boolSchema.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            if (boolSchema.GetValueKind() == JsonValueKind.False)
            {
                errors.Add($"{path}: no value is allowed here");
            }

            return;
        }

        if (schema is not JsonObject s)
        {
            return;
        }

        if (s.Str("$ref") is { } reference)
        {
            var target = ResolveRef(root, reference);
            if (target is null)
            {
                errors.Add($"{path}: unresolvable $ref '{reference}'");
            }
            else
            {
                ValidateNode(instance, target, root, path, errors, depth + 1);
            }
        }

        var kind = KindOf(instance);
        var nullable = s.Bool("nullable") == true;
        if (kind == "null" && nullable)
        {
            return;
        }

        if (s["type"] is { } typeNode)
        {
            var allowed = typeNode switch
            {
                JsonArray arr => arr.Select(t => t is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null)
                    .Where(t => t is not null).Cast<string>().ToList(),
                JsonValue v when v.GetValueKind() == JsonValueKind.String => [v.GetValue<string>()],
                _ => [],
            };

            if (allowed.Count > 0 && !allowed.Any(t => MatchesType(instance, kind, t)))
            {
                errors.Add($"{path}: expected {string.Join(" or ", allowed)} but got {kind}");
                return;
            }
        }

        if (s["enum"] is JsonArray enumValues && !enumValues.Any(e => JsonNode.DeepEquals(e, instance)))
        {
            errors.Add($"{path}: value {Describe(instance)} is not one of {enumValues.ToJsonString()}");
        }

        if (s.TryGetPropertyValue("const", out var constValue) && !JsonNode.DeepEquals(constValue, instance))
        {
            errors.Add($"{path}: value must be {constValue?.ToJsonString() ?? "null"}");
        }

        switch (instance)
        {
            case JsonObject obj:
                ValidateObject(obj, s, root, path, errors, depth);
                break;
            case JsonArray arr:
                ValidateArray(arr, s, root, path, errors, depth);
                break;
            case JsonValue value:
                ValidateScalar(value, kind, s, path, errors);
                break;
        }

        if (s["allOf"] is JsonArray allOf)
        {
            foreach (var sub in allOf)
            {
                if (sub is not null)
                {
                    ValidateNode(instance, sub, root, path, errors, depth + 1);
                }
            }
        }

        if (s["anyOf"] is JsonArray anyOf && anyOf.Count > 0 &&
            !anyOf.Any(sub => sub is not null && Validate(instance, sub, root, path, depth).Count == 0))
        {
            errors.Add($"{path}: value does not match any of the allowed schemas (anyOf)");
        }

        if (s["oneOf"] is JsonArray oneOf && oneOf.Count > 0)
        {
            var matches = oneOf.Count(sub => sub is not null && Validate(instance, sub, root, path, depth).Count == 0);
            if (matches != 1)
            {
                errors.Add($"{path}: value must match exactly one schema (oneOf) but matched {matches}");
            }
        }

        if (s["not"] is { } notSchema && Validate(instance, notSchema, root, path, depth).Count == 0)
        {
            errors.Add($"{path}: value must not match the 'not' schema");
        }
    }

    private static List<string> Validate(JsonNode? instance, JsonNode schema, JsonNode root, string path, int depth)
    {
        var errors = new List<string>();
        ValidateNode(instance, schema, root, path, errors, depth + 1);
        return errors;
    }

    private static void ValidateObject(JsonObject obj, JsonObject s, JsonNode root, string path, List<string> errors, int depth)
    {
        if (s["required"] is JsonArray required)
        {
            foreach (var name in required)
            {
                if (name is JsonValue v && v.GetValueKind() == JsonValueKind.String && !obj.ContainsKey(v.GetValue<string>()))
                {
                    errors.Add($"{path}: missing required property '{v.GetValue<string>()}'");
                }
            }
        }

        var properties = s.Obj("properties");
        var patternProperties = s.Obj("patternProperties");
        foreach (var (key, value) in obj)
        {
            var childPath = $"{path}.{key}";
            var matched = false;
            if (properties is not null && properties.TryGetPropertyValue(key, out var propSchema) && propSchema is not null)
            {
                matched = true;
                ValidateNode(value, propSchema, root, childPath, errors, depth + 1);
            }

            if (patternProperties is not null)
            {
                foreach (var (pattern, patternSchema) in patternProperties)
                {
                    if (patternSchema is not null && SafeRegexMatch(pattern, key))
                    {
                        matched = true;
                        ValidateNode(value, patternSchema, root, childPath, errors, depth + 1);
                    }
                }
            }

            if (!matched && s.TryGetPropertyValue("additionalProperties", out var additional) && additional is not null)
            {
                if (additional is JsonValue av && av.GetValueKind() == JsonValueKind.False)
                {
                    errors.Add($"{path}: unexpected property '{key}'");
                }
                else if (additional is JsonObject)
                {
                    ValidateNode(value, additional, root, childPath, errors, depth + 1);
                }
            }
        }

        if (s.Int("minProperties") is { } minProps && obj.Count < minProps)
        {
            errors.Add($"{path}: expected at least {minProps} properties");
        }

        if (s.Int("maxProperties") is { } maxProps && obj.Count > maxProps)
        {
            errors.Add($"{path}: expected at most {maxProps} properties");
        }
    }

    private static void ValidateArray(JsonArray arr, JsonObject s, JsonNode root, string path, List<string> errors, int depth)
    {
        var prefixCount = 0;
        if (s["prefixItems"] is JsonArray prefixItems)
        {
            prefixCount = prefixItems.Count;
            for (var i = 0; i < Math.Min(prefixItems.Count, arr.Count); i++)
            {
                if (prefixItems[i] is { } itemSchema)
                {
                    ValidateNode(arr[i], itemSchema, root, $"{path}[{i}]", errors, depth + 1);
                }
            }
        }

        switch (s["items"])
        {
            case JsonArray tuple:
                for (var i = 0; i < Math.Min(tuple.Count, arr.Count); i++)
                {
                    if (tuple[i] is { } itemSchema)
                    {
                        ValidateNode(arr[i], itemSchema, root, $"{path}[{i}]", errors, depth + 1);
                    }
                }

                break;
            case { } itemsSchema:
                for (var i = prefixCount; i < arr.Count; i++)
                {
                    ValidateNode(arr[i], itemsSchema, root, $"{path}[{i}]", errors, depth + 1);
                }

                break;
        }

        if (s.Int("minItems") is { } minItems && arr.Count < minItems)
        {
            errors.Add($"{path}: expected at least {minItems} items but got {arr.Count}");
        }

        if (s.Int("maxItems") is { } maxItems && arr.Count > maxItems)
        {
            errors.Add($"{path}: expected at most {maxItems} items but got {arr.Count}");
        }

        if (s.Bool("uniqueItems") == true)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                for (var j = i + 1; j < arr.Count; j++)
                {
                    if (JsonNode.DeepEquals(arr[i], arr[j]))
                    {
                        errors.Add($"{path}: items must be unique (duplicates at [{i}] and [{j}])");
                        return;
                    }
                }
            }
        }
    }

    private static void ValidateScalar(JsonValue value, string kind, JsonObject s, string path, List<string> errors)
    {
        if (kind == "string")
        {
            var str = value.GetValue<string>();
            var length = new StringInfo(str).LengthInTextElements;
            if (s.Int("minLength") is { } minLength && length < minLength)
            {
                errors.Add($"{path}: string is shorter than {minLength} characters");
            }

            if (s.Int("maxLength") is { } maxLength && length > maxLength)
            {
                errors.Add($"{path}: string is longer than {maxLength} characters");
            }

            if (s.Str("pattern") is { } pattern && !SafeRegexMatch(pattern, str))
            {
                errors.Add($"{path}: string does not match pattern '{pattern}'");
            }
        }
        else if (kind is "number" or "integer")
        {
            var number = value.GetValue<double>();
            if (Number(s, "minimum") is { } min && number < min)
            {
                errors.Add($"{path}: {Format(number)} is less than the minimum {Format(min)}");
            }

            if (Number(s, "maximum") is { } max && number > max)
            {
                errors.Add($"{path}: {Format(number)} is greater than the maximum {Format(max)}");
            }

            if (Number(s, "exclusiveMinimum") is { } exMin && number <= exMin)
            {
                errors.Add($"{path}: {Format(number)} must be greater than {Format(exMin)}");
            }

            if (Number(s, "exclusiveMaximum") is { } exMax && number >= exMax)
            {
                errors.Add($"{path}: {Format(number)} must be less than {Format(exMax)}");
            }

            if (Number(s, "multipleOf") is { } multipleOf && multipleOf > 0)
            {
                var quotient = number / multipleOf;
                if (Math.Abs(quotient - Math.Round(quotient)) > 1e-9)
                {
                    errors.Add($"{path}: {Format(number)} is not a multiple of {Format(multipleOf)}");
                }
            }
        }
    }

    private static bool MatchesType(JsonNode? instance, string kind, string type) => type switch
    {
        "integer" => kind == "integer",
        "number" => kind is "number" or "integer",
        _ => kind == type,
    };

    private static string KindOf(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "object";
            case JsonArray:
                return "array";
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        return "string";
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return "boolean";
                    case JsonValueKind.Null:
                        return "null";
                    case JsonValueKind.Number:
                        var d = v.GetValue<double>();
                        return Math.Abs(d % 1) < double.Epsilon && !double.IsInfinity(d) ? "integer" : "number";
                }

                break;
        }

        return "unknown";
    }

    private static double? Number(JsonObject s, string property) =>
        s[property] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static JsonNode? ResolveRef(JsonNode root, string reference)
    {
        if (reference == "#")
        {
            return root;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? current = root;
        foreach (var rawSegment in reference[2..].Split('/'))
        {
            var segment = Uri.UnescapeDataString(rawSegment).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var idx) && idx < a.Count => a[idx],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static bool SafeRegexMatch(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static string Describe(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}

using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reactor.Execution;

/// <summary>
/// Normalizes context values into a JSON-compatible plain-object model (string, long, double, bool, null,
/// <see cref="List{T}"/> of object, <see cref="Dictionary{TKey,TValue}"/> of string to object) so contexts can be
/// deep-copied, checkpointed, and compared reliably.
/// </summary>
public static class ContextValues
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Converts any value into the plain JSON-compatible model.</summary>
    public static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                return b;
            case int or long or short or byte or sbyte or ushort or uint:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case ulong ul:
                return ul <= long.MaxValue ? (long)ul : (double)ul;
            case float or double or decimal:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return d % 1 == 0 && Math.Abs(d) < 9e15 ? (long)d : d;
            case char c:
                return c.ToString();
            case Enum e:
                return e.ToString();
            case DateTimeOffset dto:
                return dto.ToString("O", CultureInfo.InvariantCulture);
            case DateTime dt:
                return dt.ToString("O", CultureInfo.InvariantCulture);
            case TimeSpan ts:
                return ts.ToString("c", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString();
            case JsonNode node:
                return FromJson(node);
            case JsonElement element:
                return FromJson(JsonNode.Parse(element.GetRawText()));
            case IDictionary dictionary:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    map[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = Normalize(entry.Value);
                }

                return map;
            case IEnumerable enumerable:
                var list = new List<object?>();
                foreach (var item in enumerable)
                {
                    list.Add(Normalize(item));
                }

                return list;
            default:
                return FromJson(JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions));
        }
    }

    /// <summary>Converts a JSON node into the plain model.</summary>
    public static object? FromJson(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, child) in obj)
                {
                    map[key] = FromJson(child);
                }

                return map;
            case JsonArray arr:
                return arr.Select(FromJson).ToList();
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        return v.GetValue<string>();
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Null:
                        return null;
                    case JsonValueKind.Number:
                        var text = v.ToJsonString();
                        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                        {
                            return l;
                        }

                        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                }

                break;
        }

        return node.ToJsonString();
    }

    /// <summary>Converts a plain value into a JSON node (for checkpoints and status files).</summary>
    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        Dictionary<string, object?> map => new JsonObject(map.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ToJson(kv.Value)))),
        List<object?> list => new JsonArray(list.Select(ToJson).ToArray()),
        _ => ToJson(Normalize(value)),
    };

    /// <summary>Deep copy of a normalized value.</summary>
    public static object? DeepCopy(object? value) => value switch
    {
        Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => DeepCopy(kv.Value), StringComparer.Ordinal),
        List<object?> list => list.Select(DeepCopy).ToList(),
        _ => value,
    };

    /// <summary>
    /// Canonical string form used by conditions and prompts: strings verbatim, booleans lowercase, numbers invariant,
    /// structured values as compact JSON, null as empty.
    /// </summary>
    public static string ToDisplayString(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        Dictionary<string, object?> or List<object?> => ToJson(value)?.ToJsonString() ?? "",
        _ => ToJson(Normalize(value))?.ToJsonString() ?? value.ToString() ?? "",
    };
}

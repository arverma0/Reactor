using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm;

/// <summary>Structured-output strategies (spec 4.5): native schema output, tool-based extraction, or prompt-based.</summary>
internal static class ObjectGeneration
{
    private const string WrapperProperty = "value";

    public static ObjectGenerationStrategy ResolveStrategy(GenerateObjectOptions options, Client client)
    {
        if (options.Strategy != ObjectGenerationStrategy.Auto)
        {
            return options.Strategy;
        }

        var provider = options.Provider ?? client.DefaultProvider ?? ModelCatalog.InferProvider(options.Model);
        return provider?.ToLowerInvariant() switch
        {
            "openai" or "gemini" or "openai-compatible" => ObjectGenerationStrategy.Native,
            "anthropic" => ObjectGenerationStrategy.Tool,
            _ => ObjectGenerationStrategy.Prompt,
        };
    }

    public static GenerateOptions Prepare(GenerateObjectOptions options, ObjectGenerationStrategy strategy, Client client)
    {
        _ = client;
        var schema = (JsonObject)options.Schema.DeepClone();
        GenerateOptions baseOptions = options with { MaxToolRounds = 0 };
        switch (strategy)
        {
            case ObjectGenerationStrategy.Native:
                return baseOptions with
                {
                    ResponseFormat = ResponseFormat.ForSchema(schema, strict: IsStrictCompatible(schema), name: SafeName(options.SchemaName)),
                };
            case ObjectGenerationStrategy.Tool:
                var toolName = ToolName(options.SchemaName);
                var tool = Tool.Passive(
                    toolName,
                    options.SchemaDescription ?? "Return the final answer as structured data by calling this tool with arguments matching the schema.",
                    IsObjectSchema(schema) ? schema : WrapSchema(schema));
                return baseOptions with
                {
                    Tools = [tool],
                    ToolChoice = ToolChoice.Named(toolName),
                    ResponseFormat = null,
                };
            default:
                var instructions =
                    "Respond ONLY with a single JSON value that conforms to the following JSON Schema. " +
                    "Do not include any prose, explanations, or code fences.\n\nJSON Schema:\n" + schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                return baseOptions with
                {
                    System = string.IsNullOrEmpty(options.System) ? instructions : options.System + "\n\n" + instructions,
                };
        }
    }

    /// <summary>Parses and validates the structured output from a response.</summary>
    public static JsonNode Extract(Response response, ObjectGenerationStrategy strategy, GenerateObjectOptions options)
    {
        JsonNode? value;
        string rawText;
        if (strategy == ObjectGenerationStrategy.Tool)
        {
            var call = response.ToolCalls.FirstOrDefault(c => c.Name == ToolName(options.SchemaName));
            if (call is null)
            {
                throw new NoObjectGeneratedError("The model did not call the structured output tool.", response.Text);
            }

            if (call.ArgumentsError is not null)
            {
                throw new NoObjectGeneratedError($"The structured output tool arguments were not valid JSON: {call.ArgumentsError}", call.RawArguments);
            }

            rawText = call.RawArguments ?? call.Arguments.ToJsonString();
            value = IsObjectSchema(options.Schema) ? call.Arguments : call.Arguments[WrapperProperty]?.DeepClone();
        }
        else
        {
            rawText = response.Text;
            var json = ExtractJsonText(rawText);
            value = json is null ? null : JsonHelpers.ParseOrNull(json);
            if (value is null)
            {
                throw new NoObjectGeneratedError("The response did not contain valid JSON.", rawText);
            }
        }

        var errors = JsonSchemaValidator.Validate(value, options.Schema);
        if (errors.Count > 0)
        {
            throw new NoObjectGeneratedError("The generated object does not match the schema: " + string.Join("; ", errors), rawText);
        }

        return value ?? JsonValue.Create((string?)null)!;
    }

    /// <summary>
    /// Extracts the JSON document from model text: strips Markdown code fences and surrounding prose, returning the
    /// outermost object/array (or the trimmed text when it is a bare JSON scalar).
    /// </summary>
    public static string? ExtractJsonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = StripFence(text.Trim());
        if (JsonHelpers.ParseOrNull(trimmed) is not null)
        {
            return trimmed;
        }

        var start = trimmed.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return null;
        }

        var open = trimmed[start];
        var close = open == '{' ? '}' : ']';
        var end = trimmed.LastIndexOf(close);
        return end > start ? trimmed[start..(end + 1)] : trimmed[start..];
    }

    /// <summary>Removes a leading <c>```json</c> fence (and trailing fence if present), tolerating partial text.</summary>
    public static string StripFence(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var newline = t.IndexOf('\n', StringComparison.Ordinal);
        if (newline < 0)
        {
            return "";
        }

        t = t[(newline + 1)..];
        var fence = t.LastIndexOf("```", StringComparison.Ordinal);
        return fence >= 0 ? t[..fence].Trim() : t;
    }

    public static string ToolName(string schemaName)
    {
        var name = SafeName(schemaName);
        return ToolDefinition.IsValidName(name) ? name : "structured_output";
    }

    public static bool IsObjectSchema(JsonObject schema) => schema.Str("type") == "object";

    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        var result = sb.ToString();
        if (result.Length == 0 || !char.IsAsciiLetter(result[0]))
        {
            result = "r_" + result;
        }

        return result.Length > 64 ? result[..64] : result;
    }

    private static JsonObject WrapSchema(JsonObject schema) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { [WrapperProperty] = schema.DeepClone() },
        ["required"] = new JsonArray(WrapperProperty),
    };

    /// <summary>
    /// OpenAI strict mode requires every object schema to set <c>additionalProperties: false</c> and list all of its
    /// properties as required. Strict is only requested when the schema already satisfies that.
    /// </summary>
    internal static bool IsStrictCompatible(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Str("type") == "object" || obj["properties"] is JsonObject)
                {
                    if (obj["additionalProperties"] is not JsonValue ap || ap.GetValueKind() != JsonValueKind.False)
                    {
                        return false;
                    }

                    var properties = obj.Obj("properties");
                    var required = obj.Arr("required")?.Select(r => r?.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
                    if (properties is not null && properties.Any(p => !required.Contains(p.Key)))
                    {
                        return false;
                    }
                }

                foreach (var (key, child) in obj)
                {
                    if (key is "properties" or "$defs" or "definitions")
                    {
                        if (child is JsonObject map && map.Any(kv => !IsStrictCompatible(kv.Value)))
                        {
                            return false;
                        }
                    }
                    else if (key is "items" or "anyOf" or "allOf" or "oneOf" or "additionalProperties")
                    {
                        if (!IsStrictCompatible(child))
                        {
                            return false;
                        }
                    }
                }

                return true;
            case JsonArray arr:
                return arr.All(IsStrictCompatible);
            default:
                return true;
        }
    }
}

/// <summary>
/// The result of <see cref="Ai.StreamObject"/>: yields progressively more complete partial objects (enumerable once)
/// and exposes the final validated object.
/// </summary>
public sealed class ObjectStreamResult : IAsyncEnumerable<JsonNode>
{
    private readonly StreamResult _stream;
    private readonly ObjectGenerationStrategy _strategy;
    private readonly GenerateObjectOptions _options;
    private readonly TaskCompletionSource<JsonNode> _object = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    internal ObjectStreamResult(StreamResult stream, ObjectGenerationStrategy strategy, GenerateObjectOptions options)
    {
        _stream = stream;
        _strategy = strategy;
        _options = options;
    }

    /// <summary>The underlying event stream result (steps, usage, response).</summary>
    public StreamResult Events => _stream;

    public IAsyncEnumerator<JsonNode> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("An ObjectStreamResult can only be enumerated once. Use GetObjectAsync() to await the final object.");
        }

        return Run(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    /// <summary>The final, schema-validated object. Consumes the stream if it has not been enumerated.</summary>
    /// <exception cref="NoObjectGeneratedError">Parsing or validation failed.</exception>
    public async Task<JsonNode> GetObjectAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            await foreach (var _ in this.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
            }
        }

        return await _object.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<JsonNode> Run([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        string? lastSerialized = null;
        Response? final = null;
        var completed = false;
        try
        {
            await foreach (var evt in _stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (evt.Type == StreamEventType.Error)
                {
                    _object.TrySetException(evt.Error ?? new StreamError("The stream reported an error."));
                    yield break;
                }

                if (evt.Type == StreamEventType.Finish)
                {
                    final = evt.Response;
                    continue;
                }

                var appended = _strategy == ObjectGenerationStrategy.Tool
                    ? evt.Type == StreamEventType.ToolCallDelta ? evt.Delta : null
                    : evt.Type == StreamEventType.TextDelta ? evt.Delta : null;
                if (string.IsNullOrEmpty(appended))
                {
                    continue;
                }

                buffer.Append(appended);
                var candidate = _strategy == ObjectGenerationStrategy.Tool ? buffer.ToString() : ObjectGeneration.StripFence(buffer.ToString());
                var partial = PartialJson.TryParse(candidate);
                if (partial is not null && _strategy == ObjectGenerationStrategy.Tool && !ObjectGeneration.IsObjectSchema(_options.Schema))
                {
                    partial = partial["value"]?.DeepClone();
                }

                if (partial is null)
                {
                    continue;
                }

                var serialized = partial.ToJsonString();
                if (serialized != lastSerialized)
                {
                    lastSerialized = serialized;
                    yield return partial;
                }
            }

            completed = true;
        }
        finally
        {
            if (!completed && !_object.Task.IsCompleted)
            {
                _object.TrySetException(new AbortError("The object stream was not consumed to completion."));
            }
        }

        if (final is null)
        {
            _object.TrySetException(new NoObjectGeneratedError("The stream ended without a final response.", buffer.ToString()));
            yield break;
        }

        JsonNode? result = null;
        try
        {
            result = ObjectGeneration.Extract(final, _strategy, _options);
        }
        catch (NoObjectGeneratedError ex)
        {
            _object.TrySetException(ex);
        }

        if (result is not null)
        {
            _object.TrySetResult(result);
            var serialized = result.ToJsonString();
            if (serialized != lastSerialized)
            {
                yield return result.DeepClone();
            }
        }
    }
}

/// <summary>Helpers for typed structured output.</summary>
public static class GenerateResultExtensions
{
    /// <summary>Deserializes <see cref="GenerateResult.Output"/> into <typeparamref name="T"/>.</summary>
    public static T? OutputAs<T>(this GenerateResult result, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Output is null ? default : result.Output.Deserialize<T>(options ?? JsonHelpers.SerializerOptions);
    }
}

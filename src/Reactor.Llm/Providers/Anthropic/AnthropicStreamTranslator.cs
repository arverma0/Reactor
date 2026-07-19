using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Anthropic;

/// <summary>
/// Translates Anthropic Messages API stream events into unified stream events. Content blocks are accumulated into a
/// message object of the same shape as a non-streaming response, which <see cref="AnthropicResponseParser"/> turns into
/// the FINISH response.
/// </summary>
internal sealed class AnthropicStreamTranslator(string provider, Request request, IReadOnlyList<Warning> warnings, RateLimitInfo? rateLimit)
    : StreamTranslatorBase(provider, request, warnings, rateLimit)
{
    private readonly SortedDictionary<int, BlockState> _blocks = new();
    private readonly JsonObject _usage = new();
    private string? _id;
    private string? _model;
    private string? _stopReason;
    private string? _stopSequence;
    private bool _sawMessageDelta;

    public override List<StreamEvent> Process(SseEvent sse)
    {
        var events = new List<StreamEvent>();
        if (IsFinished || string.IsNullOrWhiteSpace(sse.Data))
        {
            return events;
        }

        var node = ParseObject(sse.Data);
        if (node is null)
        {
            EnsureStarted(events, null);
            events.Add(ProviderEvent(JsonValue.Create(sse.Data)));
            return events;
        }

        EnsureStarted(events, node);
        switch (node.Str("type") ?? sse.Event)
        {
            case "message_start":
                OnMessageStart(node);
                break;
            case "content_block_start":
                OnBlockStart(node, events);
                break;
            case "content_block_delta":
                OnBlockDelta(node, events);
                break;
            case "content_block_stop":
                if (node.Int("index") is { } index && _blocks.TryGetValue(index, out var block))
                {
                    EndBlock(block, node, events);
                }

                break;
            case "message_delta":
                _sawMessageDelta = true;
                if (node.Obj("delta") is { } delta)
                {
                    _stopReason = delta.Str("stop_reason") ?? _stopReason;
                    _stopSequence = delta.Str("stop_sequence") ?? _stopSequence;
                }

                MergeUsage(node.Obj("usage"));
                break;
            case "message_stop":
                Finish(node, events);
                break;
            case "ping":
                break;
            case "error":
                events.Add(ErrorEvent(ProviderErrors.FromPayload(Provider, node.Obj("error") ?? node, node), node));
                IsFinished = true;
                break;
            default:
                events.Add(ProviderEvent(node));
                break;
        }

        return events;
    }

    public override List<StreamEvent> Complete()
    {
        if (IsFinished)
        {
            return [];
        }

        // Some proxies close the connection right after message_delta; the content is complete at that point.
        if (_sawMessageDelta)
        {
            var events = new List<StreamEvent>();
            Finish(null, events);
            return events;
        }

        return UnexpectedEnd("message_stop");
    }

    private void OnMessageStart(JsonObject node)
    {
        var message = node.Obj("message");
        _id = message.Str("id") ?? _id;
        _model = message.Str("model") ?? _model;
        _stopReason = message.Str("stop_reason") ?? _stopReason;
        MergeUsage(message.Obj("usage"));
    }

    private void MergeUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return;
        }

        foreach (var (key, value) in usage)
        {
            if (value is not null)
            {
                _usage[key] = value.DeepClone();
            }
        }
    }

    private void OnBlockStart(JsonObject node, List<StreamEvent> events)
    {
        var index = node.Int("index") ?? _blocks.Count;
        var contentBlock = node.Obj("content_block") ?? new JsonObject();
        var type = contentBlock.Str("type") ?? "";
        var block = new BlockState(index, type, (JsonObject)contentBlock.DeepClone());
        _blocks[index] = block;

        switch (type)
        {
            case "text":
                events.Add(TextStart(block.Id, node));
                AppendText(block, contentBlock.Str("text"), node, events);
                break;
            case "thinking":
                events.Add(ReasoningStart(block.Id, node));
                AppendText(block, contentBlock.Str("thinking"), node, events);
                block.Signature.Append(contentBlock.Str("signature"));
                break;
            case "tool_use":
                events.Add(ToolCallStart(contentBlock.Str("id") ?? "", contentBlock.Str("name") ?? "", node));
                break;
            case "redacted_thinking":
                break;
            default:
                // Server tools and other block types have no unified streaming equivalent.
                events.Add(ProviderEvent(node));
                break;
        }
    }

    private void OnBlockDelta(JsonObject node, List<StreamEvent> events)
    {
        var index = node.Int("index") ?? 0;
        var delta = node.Obj("delta");
        var deltaType = delta.Str("type");
        if (!_blocks.TryGetValue(index, out var block))
        {
            // Tolerate a missing content_block_start by inferring the block type from the delta.
            var inferred = deltaType switch
            {
                "text_delta" => "text",
                "thinking_delta" or "signature_delta" => "thinking",
                "input_json_delta" => "tool_use",
                _ => "unknown",
            };
            OnBlockStart(new JsonObject { ["index"] = index, ["content_block"] = new JsonObject { ["type"] = inferred } }, events);
            block = _blocks[index];
        }

        if (block.Ended)
        {
            return;
        }

        switch (deltaType)
        {
            case "text_delta":
                AppendText(block, delta.Str("text"), node, events);
                break;
            case "thinking_delta":
                AppendText(block, delta.Str("thinking"), node, events);
                break;
            case "signature_delta":
                block.Signature.Append(delta.Str("signature"));
                break;
            case "input_json_delta":
                var fragment = delta.Str("partial_json") ?? "";
                block.Json.Append(fragment);
                if (block.Type == "tool_use" && fragment.Length > 0)
                {
                    events.Add(ToolCallDelta(block.Block.Str("id") ?? "", block.Block.Str("name") ?? "", fragment, node));
                }

                break;
            case "citations_delta":
                break;
            default:
                events.Add(ProviderEvent(node));
                break;
        }
    }

    private static void AppendText(BlockState block, string? text, JsonNode raw, List<StreamEvent> events)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        block.Text.Append(text);
        if (block.Type == "text")
        {
            events.Add(TextDelta(block.Id, text, raw));
        }
        else if (block.Type == "thinking")
        {
            events.Add(ReasoningDelta(block.Id, text, raw));
        }
    }

    private static void EndBlock(BlockState block, JsonNode? raw, List<StreamEvent> events)
    {
        if (block.Ended)
        {
            return;
        }

        block.Ended = true;
        switch (block.Type)
        {
            case "text":
                events.Add(TextEnd(block.Id, raw));
                break;
            case "thinking":
                events.Add(ReasoningEnd(block.Id, raw));
                break;
            case "tool_use":
                events.Add(ToolCallEnd(block.Block.Str("id") ?? "", block.Block.Str("name") ?? "", block.RawInput, raw));
                break;
        }
    }

    private void Finish(JsonNode? raw, List<StreamEvent> events)
    {
        foreach (var block in _blocks.Values)
        {
            EndBlock(block, raw, events);
        }

        var content = new JsonArray();
        foreach (var block in _blocks.Values)
        {
            content.Add(block.ToMessageBlock());
        }

        var message = new JsonObject
        {
            ["id"] = _id,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = _model,
            ["content"] = content,
            ["stop_reason"] = _stopReason,
            ["stop_sequence"] = _stopSequence,
            ["usage"] = _usage.DeepClone(),
        };

        events.Add(FinishEvent(AnthropicResponseParser.Parse(message, Request, Provider, Warnings, RateLimit), raw));
        IsFinished = true;
    }

    private sealed class BlockState(int index, string type, JsonObject block)
    {
        public string Id { get; } = index.ToString(CultureInfo.InvariantCulture);

        public string Type { get; } = type;

        /// <summary>The block as announced by content_block_start.</summary>
        public JsonObject Block { get; } = block;

        public StringBuilder Text { get; } = new();

        public StringBuilder Json { get; } = new();

        public StringBuilder Signature { get; } = new();

        public bool Ended { get; set; }

        /// <summary>The accumulated tool input JSON; an empty stream of deltas means the start block's input (normally <c>{}</c>).</summary>
        public string RawInput => Json.Length > 0
            ? Json.ToString()
            : Block["input"] is JsonObject { Count: > 0 } initial ? initial.ToJsonString() : "{}";

        /// <summary>The block in the non-streaming response shape.</summary>
        public JsonObject ToMessageBlock()
        {
            switch (Type)
            {
                case "text":
                    return new JsonObject { ["type"] = "text", ["text"] = Text.ToString() };
                case "thinking":
                    var thinking = new JsonObject { ["type"] = "thinking", ["thinking"] = Text.ToString() };
                    if (Signature.Length > 0)
                    {
                        thinking["signature"] = Signature.ToString();
                    }

                    return thinking;
                case "tool_use":
                    var raw = RawInput;
                    return new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = Block.Str("id"),
                        ["name"] = Block.Str("name"),
                        ["input"] = JsonHelpers.ParseOrNull(raw) is JsonObject parsed ? parsed : JsonValue.Create(raw),
                    };
                default:
                    var copy = (JsonObject)Block.DeepClone();
                    if (Json.Length > 0)
                    {
                        copy["input"] = JsonHelpers.ParseOrNull(Json.ToString()) ?? JsonValue.Create(Json.ToString());
                    }

                    return copy;
            }
        }
    }
}

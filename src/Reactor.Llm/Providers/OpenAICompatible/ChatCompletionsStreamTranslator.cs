using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.OpenAICompatible;

/// <summary>
/// Translates Chat Completions stream chunks into unified stream events. Deltas are accumulated into a completion of the
/// non-streaming shape, which <see cref="ChatCompletionsResponseParser"/> turns into the FINISH response on
/// <c>data: [DONE]</c>.
/// </summary>
internal sealed class ChatCompletionsStreamTranslator(string provider, Request request, IReadOnlyList<Warning> warnings, RateLimitInfo? rateLimit)
    : StreamTranslatorBase(provider, request, warnings, rateLimit)
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly StringBuilder _refusal = new();
    private readonly SortedDictionary<int, ToolState> _tools = new();
    private string? _openTextId;
    private string? _openReasoningId;
    private int _segmentCount;
    private string? _id;
    private string? _model;
    private JsonNode? _created;
    private string? _finishReason;
    private JsonObject? _usage;

    public override List<StreamEvent> Process(SseEvent sse)
    {
        var events = new List<StreamEvent>();
        if (IsFinished || string.IsNullOrWhiteSpace(sse.Data))
        {
            return events;
        }

        if (sse.Data.Trim() == "[DONE]")
        {
            Finish(null, events);
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
        if (node["error"] is JsonObject or JsonValue)
        {
            events.Add(ErrorEvent(ProviderErrors.FromPayload(Provider, node["error"], node), node));
            IsFinished = true;
            return events;
        }

        _id ??= node.Str("id");
        _model ??= node.Str("model");
        _created ??= node["created"]?.DeepClone();
        if (node.Obj("usage") is { } usage)
        {
            _usage = (JsonObject)usage.DeepClone();
        }

        var choice = AdapterSupport.Objects(node.Arr("choices")).FirstOrDefault(c => (c.Int("index") ?? 0) == 0);
        if (choice is null)
        {
            return events;
        }

        if (choice.Obj("delta") is { } delta)
        {
            OnDelta(delta, node, events);
        }

        if (choice.Str("finish_reason") is { } finishReason)
        {
            _finishReason = finishReason;
            CloseSegments(node, events);
            EndTools(node, events);
        }

        return events;
    }

    public override List<StreamEvent> Complete()
    {
        if (IsFinished)
        {
            return [];
        }

        // Tolerate servers that close the stream without [DONE] once the finish reason has arrived.
        if (_finishReason is null)
        {
            return UnexpectedEnd("[DONE]");
        }

        var events = new List<StreamEvent>();
        Finish(null, events);
        return events;
    }

    private void OnDelta(JsonObject delta, JsonObject raw, List<StreamEvent> events)
    {
        var reasoning = delta.Str("reasoning_content") ?? delta.Str("reasoning");
        if (!string.IsNullOrEmpty(reasoning))
        {
            CloseText(raw, events);
            if (_openReasoningId is null)
            {
                _openReasoningId = NextSegmentId("reasoning");
                events.Add(ReasoningStart(_openReasoningId, raw));
            }

            _reasoning.Append(reasoning);
            events.Add(ReasoningDelta(_openReasoningId, reasoning, raw));
        }

        var content = delta.Str("content");
        if (!string.IsNullOrEmpty(content))
        {
            AppendText(_text, content, raw, events);
        }

        var refusal = delta.Str("refusal");
        if (!string.IsNullOrEmpty(refusal))
        {
            AppendText(_refusal, refusal, raw, events);
        }

        foreach (var call in AdapterSupport.Objects(delta.Arr("tool_calls")))
        {
            OnToolCallDelta(call, raw, events);
        }
    }

    private void AppendText(StringBuilder target, string text, JsonNode raw, List<StreamEvent> events)
    {
        CloseReasoning(raw, events);
        if (_openTextId is null)
        {
            _openTextId = NextSegmentId("text");
            events.Add(TextStart(_openTextId, raw));
        }

        target.Append(text);
        events.Add(TextDelta(_openTextId, text, raw));
    }

    private void OnToolCallDelta(JsonObject call, JsonNode raw, List<StreamEvent> events)
    {
        CloseSegments(raw, events);
        var id = call.Str("id");
        var index = call.Int("index")
                    ?? (id is not null ? _tools.Values.FirstOrDefault(t => t.Id == id)?.Index : null)
                    ?? (_tools.Count == 0 ? 0 : _tools.Keys.Max() + (id is null ? 0 : 1));
        if (!_tools.TryGetValue(index, out var tool))
        {
            tool = new ToolState(index);
            _tools[index] = tool;
        }

        if (tool.Ended)
        {
            return;
        }

        if (!string.IsNullOrEmpty(id) && tool.Id is null)
        {
            tool.Id = id;
        }

        var function = call.Obj("function");
        if (function.Str("name") is { Length: > 0 } name && tool.Name.Length == 0)
        {
            tool.Name = name;
        }

        var fragment = function?["arguments"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject obj => obj.ToJsonString(),
            _ => "",
        };

        if (!tool.Started && tool.Name.Length > 0)
        {
            // START waits until the function name is known; arguments received earlier are replayed as one delta.
            StartTool(tool, raw, events);
            if (tool.Arguments.Length > 0)
            {
                events.Add(ToolCallDelta(tool.Id!, tool.Name, tool.Arguments.ToString(), raw));
            }
        }

        if (fragment.Length > 0)
        {
            tool.Arguments.Append(fragment);
            if (tool.Started)
            {
                events.Add(ToolCallDelta(tool.Id!, tool.Name, fragment, raw));
            }
        }
    }

    private static void StartTool(ToolState tool, JsonNode? raw, List<StreamEvent> events)
    {
        tool.Started = true;
        tool.Id ??= AdapterSupport.NewToolCallId();
        events.Add(ToolCallStart(tool.Id, tool.Name, raw));
    }

    private void EndTools(JsonNode? raw, List<StreamEvent> events)
    {
        foreach (var tool in _tools.Values.Where(t => !t.Ended))
        {
            if (!tool.Started)
            {
                StartTool(tool, raw, events);
            }

            tool.Ended = true;
            events.Add(ToolCallEnd(tool.Id!, tool.Name, tool.Arguments.ToString(), raw));
        }
    }

    private void CloseText(JsonNode? raw, List<StreamEvent> events)
    {
        if (_openTextId is not null)
        {
            events.Add(TextEnd(_openTextId, raw));
            _openTextId = null;
        }
    }

    private void CloseReasoning(JsonNode? raw, List<StreamEvent> events)
    {
        if (_openReasoningId is not null)
        {
            events.Add(ReasoningEnd(_openReasoningId, raw));
            _openReasoningId = null;
        }
    }

    private void CloseSegments(JsonNode? raw, List<StreamEvent> events)
    {
        CloseReasoning(raw, events);
        CloseText(raw, events);
    }

    private string NextSegmentId(string prefix)
    {
        var id = prefix + "_" + _segmentCount.ToString(CultureInfo.InvariantCulture);
        _segmentCount++;
        return id;
    }

    private void Finish(JsonNode? raw, List<StreamEvent> events)
    {
        EnsureStarted(events, raw);
        CloseSegments(raw, events);
        EndTools(raw, events);

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = _text.Length > 0 ? _text.ToString() : null,
        };
        if (_reasoning.Length > 0)
        {
            message["reasoning_content"] = _reasoning.ToString();
        }

        if (_refusal.Length > 0)
        {
            message["refusal"] = _refusal.ToString();
        }

        if (_tools.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var tool in _tools.Values)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = tool.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = tool.Arguments.ToString() },
                });
            }

            message["tool_calls"] = calls;
        }

        var completion = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion",
            ["created"] = _created?.DeepClone(),
            ["model"] = _model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = _finishReason,
            }),
        };
        if (_usage is not null)
        {
            completion["usage"] = _usage.DeepClone();
        }

        events.Add(FinishEvent(ChatCompletionsResponseParser.Parse(completion, Request, Provider, Warnings, RateLimit), raw));
        IsFinished = true;
    }

    private sealed class ToolState(int index)
    {
        public int Index { get; } = index;

        public string? Id { get; set; }

        public string Name { get; set; } = "";

        public StringBuilder Arguments { get; } = new();

        public bool Started { get; set; }

        public bool Ended { get; set; }
    }
}

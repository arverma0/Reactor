using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.OpenAI;

/// <summary>
/// Translates OpenAI Responses API stream events into unified stream events. The FINISH response is produced by
/// <see cref="OpenAIResponseParser"/> from the response embedded in the terminal event (falling back to the items
/// collected from <c>response.output_item.done</c> when that response carries no output).
/// </summary>
internal sealed class OpenAIStreamTranslator(string provider, Request request, IReadOnlyList<Warning> warnings, RateLimitInfo? rateLimit)
    : StreamTranslatorBase(provider, request, warnings, rateLimit)
{
    private readonly List<TextSegment> _textSegments = [];
    private readonly Dictionary<string, TextSegment> _textById = new(StringComparer.Ordinal);
    private readonly List<ToolState> _tools = [];
    private readonly Dictionary<string, ToolState> _toolsByItem = new(StringComparer.Ordinal);
    private readonly List<ReasoningState> _reasoning = [];
    private readonly Dictionary<string, ReasoningState> _reasoningByItem = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, JsonObject> _completedItems = new();

    public override List<StreamEvent> Process(SseEvent sse)
    {
        var events = new List<StreamEvent>();
        if (IsFinished || string.IsNullOrWhiteSpace(sse.Data) || sse.Data == "[DONE]")
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
            case "response.created":
            case "response.in_progress":
            case "response.queued":
            case "response.content_part.added":
            case "response.content_part.done":
            case "response.reasoning_summary_part.added":
            case "response.reasoning_summary_part.done":
            case "response.reasoning_summary_text.done":
            case "response.function_call_arguments.done":
            case "response.custom_tool_call_input.done":
            case "response.output_text.annotation.added":
                break;
            case "response.output_item.added":
                OnItemAdded(node, events);
                break;
            case "response.output_text.delta":
            case "response.refusal.delta":
                OnTextDelta(node, events);
                break;
            case "response.output_text.done":
            case "response.refusal.done":
                EndText(TextIdOf(node), node, events);
                break;
            case "response.reasoning_summary_text.delta":
                OnReasoningDelta(node, events);
                break;
            case "response.function_call_arguments.delta":
            case "response.custom_tool_call_input.delta":
                OnToolDelta(node, events);
                break;
            case "response.output_item.done":
                OnItemDone(node, events);
                break;
            case "response.completed":
            case "response.incomplete":
                OnCompleted(node, events);
                break;
            case "response.failed":
                OnFailed(node, events);
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

    public override List<StreamEvent> Complete() => UnexpectedEnd("response.completed");

    private static string TextIdOf(JsonObject node)
    {
        var itemId = node.Str("item_id") ?? $"output_{node.Int("output_index") ?? 0}";
        var contentIndex = node.Int("content_index") ?? 0;
        return contentIndex == 0 ? itemId : $"{itemId}:{contentIndex}";
    }

    private void OnItemAdded(JsonObject node, List<StreamEvent> events)
    {
        var item = node.Obj("item");
        var itemId = item.Str("id") ?? $"output_{node.Int("output_index") ?? 0}";
        switch (item.Str("type"))
        {
            case "function_call":
            case "custom_tool_call":
                var tool = GetOrStartTool(itemId, item, node, events);
                var initial = item.Str(item.Str("type") == "custom_tool_call" ? "input" : "arguments");
                if (!string.IsNullOrEmpty(initial) && tool.Arguments.Length == 0)
                {
                    tool.Arguments.Append(initial);
                    events.Add(ToolCallDelta(tool.CallId, tool.Name, initial, node));
                }

                break;
            case "reasoning":
                GetOrStartReasoning(itemId, node, events);
                break;
            case "message":
                break;
            default:
                // Built-in tool items (web search, file search, ...) have no unified equivalent.
                events.Add(ProviderEvent(node));
                break;
        }
    }

    private void OnTextDelta(JsonObject node, List<StreamEvent> events)
    {
        var textId = TextIdOf(node);
        if (!_textById.TryGetValue(textId, out var segment))
        {
            segment = new TextSegment(textId, node.Str("item_id") ?? "");
            _textById[textId] = segment;
            _textSegments.Add(segment);
            events.Add(TextStart(textId, node));
        }

        var delta = node.Str("delta") ?? "";
        if (delta.Length > 0 && !segment.Ended)
        {
            events.Add(TextDelta(textId, delta, node));
        }
    }

    private void EndText(string textId, JsonNode? raw, List<StreamEvent> events)
    {
        if (_textById.TryGetValue(textId, out var segment) && !segment.Ended)
        {
            segment.Ended = true;
            events.Add(TextEnd(textId, raw));
        }
    }

    private void OnReasoningDelta(JsonObject node, List<StreamEvent> events)
    {
        var itemId = node.Str("item_id") ?? $"output_{node.Int("output_index") ?? 0}";
        var state = GetOrStartReasoning(itemId, node, events);
        var delta = node.Str("delta") ?? "";
        if (delta.Length == 0 || state.Ended)
        {
            return;
        }

        // Summary parts are joined with a blank line, mirroring OpenAIResponseParser, so the concatenated deltas equal
        // the final THINKING text.
        var summaryIndex = node.Int("summary_index") ?? 0;
        if (state.HasText && summaryIndex != state.SummaryIndex)
        {
            delta = OpenAIResponseParser.SummarySeparator + delta;
        }

        state.SummaryIndex = summaryIndex;
        state.HasText = true;
        events.Add(ReasoningDelta(itemId, delta, node));
    }

    private void OnToolDelta(JsonObject node, List<StreamEvent> events)
    {
        var itemId = node.Str("item_id") ?? $"output_{node.Int("output_index") ?? 0}";
        var tool = GetOrStartTool(itemId, null, node, events);
        var delta = node.Str("delta") ?? "";
        if (delta.Length > 0 && !tool.Ended)
        {
            tool.Arguments.Append(delta);
            events.Add(ToolCallDelta(tool.CallId, tool.Name, delta, node));
        }
    }

    private void OnItemDone(JsonObject node, List<StreamEvent> events)
    {
        var item = node.Obj("item");
        if (item is null)
        {
            return;
        }

        var outputIndex = node.Int("output_index") ?? (_completedItems.Count == 0 ? 0 : _completedItems.Keys.Max() + 1);
        _completedItems[outputIndex] = (JsonObject)item.DeepClone();
        var itemId = item.Str("id") ?? $"output_{outputIndex}";
        switch (item.Str("type"))
        {
            case "function_call":
            case "custom_tool_call":
                var tool = GetOrStartTool(itemId, item, node, events);
                EndTool(tool, item, node, events);
                break;
            case "reasoning":
                if (_reasoningByItem.TryGetValue(itemId, out var reasoning) && !reasoning.Ended)
                {
                    reasoning.Ended = true;
                    events.Add(ReasoningEnd(itemId, node));
                }

                break;
            case "message":
                foreach (var segment in _textSegments.Where(s => s.ItemId == itemId && !s.Ended))
                {
                    segment.Ended = true;
                    events.Add(TextEnd(segment.TextId, node));
                }

                break;
            default:
                events.Add(ProviderEvent(node));
                break;
        }
    }

    private void OnCompleted(JsonObject node, List<StreamEvent> events)
    {
        var root = node.Obj("response") is { } embedded ? (JsonObject)embedded.DeepClone() : new JsonObject();
        if (root["output"] is not JsonArray { Count: > 0 } && _completedItems.Count > 0)
        {
            var output = new JsonArray();
            foreach (var item in _completedItems.Values)
            {
                output.Add(item.DeepClone());
            }

            root["output"] = output;
        }

        if (node.Str("type") == "response.incomplete" && root.Str("status") is null)
        {
            root["status"] = "incomplete";
        }

        CloseOpenSegments(root, node, events);
        events.Add(FinishEvent(OpenAIResponseParser.Parse(root, Request, Provider, Warnings, RateLimit), node));
        IsFinished = true;
    }

    private void OnFailed(JsonObject node, List<StreamEvent> events)
    {
        var error = node.Obj("response").Obj("error");
        var sdkError = error is null
            ? new ProviderError($"{Provider} reported that the response failed.", Provider, raw: node)
            : ProviderErrors.FromPayload(Provider, error, node);
        events.Add(ErrorEvent(sdkError, node));
        IsFinished = true;
    }

    /// <summary>Closes segments whose done events never arrived, so every START has a matching END before FINISH.</summary>
    private void CloseOpenSegments(JsonObject root, JsonNode raw, List<StreamEvent> events)
    {
        foreach (var segment in _textSegments.Where(s => !s.Ended))
        {
            segment.Ended = true;
            events.Add(TextEnd(segment.TextId, raw));
        }

        foreach (var reasoning in _reasoning.Where(r => !r.Ended))
        {
            reasoning.Ended = true;
            events.Add(ReasoningEnd(reasoning.ItemId, raw));
        }

        foreach (var tool in _tools.Where(t => !t.Ended))
        {
            var finalItem = AdapterSupport.Objects(root.Arr("output"))
                .FirstOrDefault(i => i.Str("id") == tool.ItemId || (i.Str("call_id") is { } c && c == tool.CallId));
            EndTool(tool, finalItem, raw, events);
        }
    }

    private void EndTool(ToolState tool, JsonObject? item, JsonNode raw, List<StreamEvent> events)
    {
        if (tool.Ended)
        {
            return;
        }

        tool.Ended = true;
        var callId = item.Str("call_id") ?? tool.CallId;
        var name = item.Str("name") ?? tool.Name;
        var arguments = item.Str(tool.IsCustom ? "input" : "arguments") ?? tool.Arguments.ToString();
        events.Add(ToolCallEnd(callId, name, arguments, raw));
    }

    private ToolState GetOrStartTool(string itemId, JsonObject? item, JsonNode raw, List<StreamEvent> events)
    {
        if (!_toolsByItem.TryGetValue(itemId, out var tool))
        {
            tool = new ToolState(
                itemId,
                item.Str("call_id") ?? itemId,
                item.Str("name") ?? "",
                item.Str("type") == "custom_tool_call");
            _toolsByItem[itemId] = tool;
            _tools.Add(tool);
            events.Add(ToolCallStart(tool.CallId, tool.Name, raw));
        }

        return tool;
    }

    private ReasoningState GetOrStartReasoning(string itemId, JsonNode raw, List<StreamEvent> events)
    {
        if (!_reasoningByItem.TryGetValue(itemId, out var state))
        {
            state = new ReasoningState(itemId);
            _reasoningByItem[itemId] = state;
            _reasoning.Add(state);
            events.Add(ReasoningStart(itemId, raw));
        }

        return state;
    }

    private sealed class TextSegment(string textId, string itemId)
    {
        public string TextId { get; } = textId;

        public string ItemId { get; } = itemId;

        public bool Ended { get; set; }
    }

    private sealed class ToolState(string itemId, string callId, string name, bool isCustom)
    {
        public string ItemId { get; } = itemId;

        public string CallId { get; } = callId;

        public string Name { get; } = name;

        public bool IsCustom { get; } = isCustom;

        public StringBuilder Arguments { get; } = new();

        public bool Ended { get; set; }
    }

    private sealed class ReasoningState(string itemId)
    {
        public string ItemId { get; } = itemId;

        public int SummaryIndex { get; set; }

        public bool HasText { get; set; }

        public bool Ended { get; set; }
    }
}

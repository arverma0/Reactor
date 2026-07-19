using System.Text;
using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>
/// Collects stream events into a complete <see cref="Response"/>, bridging streaming and non-streaming code.
/// Segment order (reasoning, text, tool calls) follows the order in which segments started.
/// </summary>
public sealed class StreamAccumulator
{
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<string, Segment> _textById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Segment> _toolsById = new(StringComparer.Ordinal);
    private Segment? _currentReasoning;
    private Response? _final;
    private FinishReason? _finishReason;
    private Usage? _usage;
    private readonly List<Warning> _warnings = [];

    /// <summary>True once a FINISH event has been processed.</summary>
    public bool IsFinished => _final is not null;

    /// <summary>The error event's error, if the stream reported one.</summary>
    public SdkError? Error { get; private set; }

    public void Process(StreamEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var type = evt.Type;
        if (type == StreamEventType.StreamStart)
        {
            if (evt.Warnings is not null)
            {
                _warnings.AddRange(evt.Warnings);
            }
        }
        else if (type == StreamEventType.TextStart)
        {
            GetText(evt.TextId);
        }
        else if (type == StreamEventType.TextDelta)
        {
            GetText(evt.TextId).Buffer.Append(evt.Delta);
        }
        else if (type == StreamEventType.ReasoningStart)
        {
            _currentReasoning = NewSegment(SegmentKind.Reasoning);
        }
        else if (type == StreamEventType.ReasoningDelta)
        {
            (_currentReasoning ??= NewSegment(SegmentKind.Reasoning)).Buffer.Append(evt.ReasoningDelta ?? evt.Delta);
        }
        else if (type == StreamEventType.ReasoningEnd)
        {
            _currentReasoning = null;
        }
        else if (type == StreamEventType.ToolCallStart || type == StreamEventType.ToolCallDelta)
        {
            if (evt.ToolCall is { } call)
            {
                var segment = GetTool(call.Id, call.Name);
                if (type == StreamEventType.ToolCallDelta && evt.Delta is not null)
                {
                    segment.Buffer.Append(evt.Delta);
                }
            }
        }
        else if (type == StreamEventType.ToolCallEnd)
        {
            if (evt.ToolCall is { } call)
            {
                GetTool(call.Id, call.Name).FinalCall = call;
            }
        }
        else if (type == StreamEventType.Finish)
        {
            _finishReason = evt.FinishReason ?? evt.Response?.FinishReason;
            _usage = evt.Usage ?? evt.Response?.Usage;
            _final = evt.Response ?? Build();
        }
        else if (type == StreamEventType.Error)
        {
            Error = evt.Error ?? new StreamError("The stream reported an error.");
        }
    }

    /// <summary>The final response (after FINISH) or a snapshot of the accumulated state.</summary>
    public Response Response => _final ?? Build();

    /// <summary>Snapshot of the accumulated state so far.</summary>
    public Response PartialResponse => Build();

    private Response Build()
    {
        var parts = new List<ContentPart>();
        foreach (var segment in _segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Text when segment.Buffer.Length > 0:
                    parts.Add(ContentPart.TextPart(segment.Buffer.ToString()));
                    break;
                case SegmentKind.Reasoning when segment.Buffer.Length > 0:
                    parts.Add(ContentPart.ThinkingPart(segment.Buffer.ToString()));
                    break;
                case SegmentKind.Tool:
                    if (segment.FinalCall is { } final)
                    {
                        parts.Add(ContentPart.ToolCallPart(final.Id, final.Name, final.Arguments.DeepClone()));
                    }
                    else
                    {
                        var raw = segment.Buffer.ToString();
                        JsonNode? args = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : Utilities.JsonHelpers.ParseOrNull(raw) ?? JsonValue.Create(raw);
                        parts.Add(ContentPart.ToolCallPart(segment.Id!, segment.Name!, args));
                    }

                    break;
            }
        }

        var hasTools = parts.Any(p => p.Kind == ContentKind.ToolCall);
        return new Response
        {
            Message = new Message { Role = Role.Assistant, Content = parts },
            FinishReason = _finishReason ?? (hasTools ? FinishReason.ToolCalls : FinishReason.Stop),
            Usage = _usage ?? Usage.Empty,
            Warnings = _warnings.ToList(),
        };
    }

    private Segment GetText(string? textId)
    {
        var key = textId ?? "";
        if (!_textById.TryGetValue(key, out var segment))
        {
            segment = NewSegment(SegmentKind.Text);
            _textById[key] = segment;
        }

        return segment;
    }

    private Segment GetTool(string id, string name)
    {
        if (!_toolsById.TryGetValue(id, out var segment))
        {
            segment = NewSegment(SegmentKind.Tool);
            segment.Id = id;
            segment.Name = name;
            _toolsById[id] = segment;
        }
        else if (string.IsNullOrEmpty(segment.Name) && !string.IsNullOrEmpty(name))
        {
            segment.Name = name;
        }

        return segment;
    }

    private Segment NewSegment(SegmentKind kind)
    {
        var segment = new Segment(kind);
        _segments.Add(segment);
        return segment;
    }

    private enum SegmentKind
    {
        Text,
        Reasoning,
        Tool,
    }

    private sealed class Segment(SegmentKind kind)
    {
        public SegmentKind Kind { get; } = kind;
        public StringBuilder Buffer { get; } = new();
        public string? Id { get; set; }
        public string? Name { get; set; }
        public ToolCall? FinalCall { get; set; }
    }
}

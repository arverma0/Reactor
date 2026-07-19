using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Providers.Common;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Providers.Gemini;

/// <summary>
/// Translates Gemini <c>streamGenerateContent?alt=sse</c> chunks into unified stream events. Chunk parts are accumulated
/// into one response of the non-streaming shape (consecutive text or thought chunks merge into a single part, keeping any
/// thought signature), which <see cref="GeminiResponseParser"/> turns into the FINISH response.
/// </summary>
internal sealed class GeminiStreamTranslator(string provider, Request request, IReadOnlyList<Warning> warnings, RateLimitInfo? rateLimit)
    : StreamTranslatorBase(provider, request, warnings, rateLimit)
{
    private readonly List<AccumulatedPart> _parts = [];
    private TextSegment? _open;
    private int _segmentCount;
    private bool _sawCandidate;
    private string? _finishReason;
    private JsonObject? _usage;
    private JsonObject? _promptFeedback;
    private string? _responseId;
    private string? _modelVersion;

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
        if (node.Obj("error") is { } error)
        {
            events.Add(ErrorEvent(ErrorFromChunk(error, node), node));
            IsFinished = true;
            return events;
        }

        _responseId ??= node.Str("responseId");
        _modelVersion = node.Str("modelVersion") ?? _modelVersion;
        if (node.Obj("usageMetadata") is { } usage)
        {
            _usage = (JsonObject)usage.DeepClone();
        }

        if (node.Obj("promptFeedback") is { } feedback)
        {
            _promptFeedback = (JsonObject)feedback.DeepClone();
        }

        var candidate = AdapterSupport.Objects(node.Arr("candidates")).FirstOrDefault();
        if (candidate is null)
        {
            return events;
        }

        _sawCandidate = true;
        foreach (var part in AdapterSupport.Objects(candidate.Obj("content").Arr("parts")))
        {
            OnPart(part, node, events);
        }

        if (candidate.Str("finishReason") is { } finishReason)
        {
            _finishReason = finishReason;
            CloseOpenSegment(node, events);
        }

        return events;
    }

    public override List<StreamEvent> Complete()
    {
        if (IsFinished)
        {
            return [];
        }

        // Gemini has no terminal event: the stream simply ends after the chunk carrying the finish reason.
        if (_finishReason is null && _promptFeedback.Str("blockReason") is null)
        {
            return UnexpectedEnd("a finish reason");
        }

        var events = new List<StreamEvent>();
        CloseOpenSegment(null, events);
        events.Add(FinishEvent(GeminiResponseParser.Parse(BuildResponse(), Request, Provider, Warnings, RateLimit), null));
        IsFinished = true;
        return events;
    }

    private SdkError ErrorFromChunk(JsonObject error, JsonObject raw)
    {
        var message = error.Str("message") ?? $"{Provider} reported an error during streaming.";
        var httpStatus = AdapterSupport.GetInt(error, "code");
        return error.Str("status") is { } status
            ? ErrorMapper.FromGrpcStatus(status, message, Provider, httpStatus, raw)
            : httpStatus is >= 400 and <= 599
                ? ErrorMapper.FromHttpStatus(httpStatus.Value, message, Provider, raw: raw)
                : ProviderErrors.FromPayload(Provider, error, raw);
    }

    private void OnPart(JsonObject part, JsonObject raw, List<StreamEvent> events)
    {
        var signature = part.Str("thoughtSignature") ?? part.Str("thought_signature");
        if (part.Obj("functionCall") is { } call)
        {
            CloseOpenSegment(raw, events);
            var accumulated = (JsonObject)part.DeepClone();
            var id = call.Str("id") is { Length: > 0 } providerId ? providerId : AdapterSupport.NewToolCallId();

            // The synthesized id is stored on the accumulated part so the FINISH response reuses it.
            ((JsonObject)accumulated["functionCall"]!)["id"] = id;
            _parts.Add(new AccumulatedPart(null, accumulated));

            var name = call.Str("name") ?? "";
            var arguments = call["args"] is JsonObject args ? args.ToJsonString() : "{}";
            events.Add(ToolCallStart(id, name, raw));
            events.Add(ToolCallEnd(id, name, arguments, raw));
            return;
        }

        if (part.Str("text") is { } text)
        {
            var thought = part.Bool("thought") == true;
            if (_open is null || _open.Thought != thought || (signature is not null && _open.Signature is not null))
            {
                CloseOpenSegment(raw, events);
                var segmentId = (thought ? "reasoning_" : "text_") + _segmentCount.ToString(CultureInfo.InvariantCulture);
                _segmentCount++;
                _open = new TextSegment(segmentId, thought);
                _parts.Add(new AccumulatedPart(_open, null));
                events.Add(thought ? ReasoningStart(_open.Id, raw) : TextStart(_open.Id, raw));
            }

            _open.Signature ??= signature;
            if (text.Length > 0)
            {
                _open.Text.Append(text);
                events.Add(thought ? ReasoningDelta(_open.Id, text, raw) : TextDelta(_open.Id, text, raw));
            }

            return;
        }

        // Inline media and other part types are kept for the final response without stream events.
        CloseOpenSegment(raw, events);
        _parts.Add(new AccumulatedPart(null, part.DeepClone()));
    }

    private void CloseOpenSegment(JsonNode? raw, List<StreamEvent> events)
    {
        if (_open is null)
        {
            return;
        }

        events.Add(_open.Thought ? ReasoningEnd(_open.Id, raw) : TextEnd(_open.Id, raw));
        _open = null;
    }

    private JsonObject BuildResponse()
    {
        var root = new JsonObject();
        if (_sawCandidate)
        {
            var parts = new JsonArray();
            foreach (var part in _parts)
            {
                parts.Add(part.Segment?.ToPart() ?? part.Node?.DeepClone());
            }

            var candidate = new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = parts },
                ["index"] = 0,
            };
            if (_finishReason is not null)
            {
                candidate["finishReason"] = _finishReason;
            }

            root["candidates"] = new JsonArray(candidate);
        }

        if (_promptFeedback is not null)
        {
            root["promptFeedback"] = _promptFeedback.DeepClone();
        }

        if (_usage is not null)
        {
            root["usageMetadata"] = _usage.DeepClone();
        }

        if (_modelVersion is not null)
        {
            root["modelVersion"] = _modelVersion;
        }

        if (_responseId is not null)
        {
            root["responseId"] = _responseId;
        }

        return root;
    }

    /// <summary>An accumulated response part: an open-ended text/thought segment or a complete provider part.</summary>
    private readonly record struct AccumulatedPart(TextSegment? Segment, JsonNode? Node);

    private sealed class TextSegment(string id, bool thought)
    {
        public string Id { get; } = id;

        public bool Thought { get; } = thought;

        public StringBuilder Text { get; } = new();

        public string? Signature { get; set; }

        public JsonObject ToPart()
        {
            var part = new JsonObject { ["text"] = Text.ToString() };
            if (Thought)
            {
                part["thought"] = true;
            }

            if (Signature is not null)
            {
                part["thoughtSignature"] = Signature;
            }

            return part;
        }
    }
}

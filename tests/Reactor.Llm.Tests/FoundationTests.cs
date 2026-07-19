using System.Text;
using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm.Tests;

public class FoundationTests
{
    [Fact]
    public void Usage_addition_sums_and_preserves_null_optionals()
    {
        var a = new Usage { InputTokens = 10, OutputTokens = 5, TotalTokens = 15, ReasoningTokens = 3 };
        var b = new Usage { InputTokens = 1, OutputTokens = 2, TotalTokens = 3 };

        var sum = a + b;

        Assert.Equal(11, sum.InputTokens);
        Assert.Equal(7, sum.OutputTokens);
        Assert.Equal(18, sum.TotalTokens);
        Assert.Equal(3, sum.ReasoningTokens);
        Assert.Null(sum.CacheReadTokens);
    }

    [Fact]
    public async Task Sse_parser_handles_multiline_data_comments_and_crlf()
    {
        const string payload = ": comment\r\nevent: message_start\r\ndata: {\"a\":1}\r\n\r\ndata: line1\ndata: line2\n\nevent: x\nid: 7\nretry: 1500\ndata: last";
        var events = new List<SseEvent>();
        await foreach (var evt in SseParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(payload))))
        {
            events.Add(evt);
        }

        Assert.Equal(3, events.Count);
        Assert.Equal("message_start", events[0].Event);
        Assert.Equal("{\"a\":1}", events[0].Data);
        Assert.Null(events[1].Event);
        Assert.Equal("line1\nline2", events[1].Data);
        Assert.Equal("x", events[2].Event);
        Assert.Equal("7", events[2].Id);
        Assert.Equal(1500, events[2].Retry);
        Assert.Equal("last", events[2].Data);
    }

    [Theory]
    [InlineData("{\"name\": \"Al", "{\"name\":\"Al\"}")]
    [InlineData("{\"items\": [1, 2, 3", "{\"items\":[1,2,3]}")]
    [InlineData("{\"a\": 1, \"b", "{\"a\":1}")]
    [InlineData("[{\"x\": tru", "[{}]")]
    [InlineData("{\"n\": 12.", "{}")]
    public void Partial_json_completes_truncated_documents(string input, string expected)
    {
        var node = PartialJson.TryParse(input);
        Assert.NotNull(node);
        Assert.Equal(expected, node!.ToJsonString());
    }

    [Fact]
    public void Schema_validator_reports_missing_required_and_type_errors()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer","minimum":0}},
             "required":["name","age"],"additionalProperties":false}
            """);

        Assert.Empty(JsonSchemaValidator.Validate(JsonNode.Parse("""{"name":"Alice","age":30}"""), schema));

        var errors = JsonSchemaValidator.Validate(JsonNode.Parse("""{"age":-1.5,"extra":true}"""), schema);
        Assert.Contains(errors, e => e.Contains("missing required property 'name'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("expected integer", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unexpected property 'extra'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(400, typeof(InvalidRequestError), false)]
    [InlineData(401, typeof(AuthenticationError), false)]
    [InlineData(403, typeof(AccessDeniedError), false)]
    [InlineData(404, typeof(NotFoundError), false)]
    [InlineData(408, typeof(RequestTimeoutError), false)]
    [InlineData(413, typeof(ContextLengthError), false)]
    [InlineData(422, typeof(InvalidRequestError), false)]
    [InlineData(429, typeof(RateLimitError), true)]
    [InlineData(500, typeof(ServerError), true)]
    [InlineData(503, typeof(ServerError), true)]
    [InlineData(599, typeof(ServerError), true)]
    public void Http_status_maps_to_error_type(int status, Type expected, bool retryable)
    {
        var error = ErrorMapper.FromHttpStatus(status, "boom", "openai");
        Assert.IsType(expected, error);
        Assert.Equal(retryable, error.Retryable);
    }

    [Fact]
    public void Message_classification_refines_ambiguous_errors()
    {
        Assert.IsType<ContextLengthError>(ErrorMapper.FromHttpStatus(400, "This model's maximum context length is 8192 tokens", "openai"));
        Assert.IsType<ContentFilterError>(ErrorMapper.FromHttpStatus(400, "Blocked by content filter", "openai"));
        Assert.IsType<QuotaExceededError>(ErrorMapper.FromHttpStatus(429, "You exceeded your quota", "openai", "insufficient_quota"));
        Assert.IsType<RateLimitError>(ErrorMapper.FromGrpcStatus("RESOURCE_EXHAUSTED", "slow down", "gemini", 429));
    }
}

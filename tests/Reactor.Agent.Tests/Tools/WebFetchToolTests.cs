using System.Net;
using System.Text;
using Reactor.Agent.Tests.Environments;
using Reactor.Agent.Tools;

namespace Reactor.Agent.Tests.Tools;

public class WebFetchToolTests
{
    private const string Page = """
        <!DOCTYPE html>
        <html>
        <head>
          <title>Sample &amp; Page</title>
          <style>body { color: red; }</style>
          <script>var secret = "do not show";</script>
        </head>
        <body>
          <noscript>Enable JavaScript</noscript>
          <h1>Main   Heading</h1>
          <p>First paragraph with <b>bold</b> and <a href="/x">a link</a>.</p>
          <!-- a comment -->
          <ul><li>One</li><li>Two &lt;tags&gt;</li></ul>
          <div>Line one<br>Line two</div>



          <pre>  indented
            code &amp; more</pre>
          <table><tr><td>A</td><td>B</td></tr></table>
        </body>
        </html>
        """;

    private static (RegisteredTool Tool, StubHandler Handler) Create(HttpStatusCode status, string body, string? contentType)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            if (contentType is not null)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            return response;
        });
        return (CoreTools.WebFetch(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Html_is_converted_to_readable_text()
    {
        await using var ws = new TestWorkspace();
        var (tool, handler) = Create(HttpStatusCode.OK, Page, "text/html; charset=utf-8");

        var output = (await ws.RunAsync(tool, """{"url":"https://example.com/page"}""")).Text;

        Assert.Equal(
            "Sample & Page\n\nMain Heading\n\nFirst paragraph with bold and a link.\n\n- One\n- Two <tags>\n\nLine one\nLine two\n\n" +
            "  indented\n    code & more\n\nA B",
            output);
        Assert.Equal(new Uri("https://example.com/page"), handler.LastRequest!.RequestUri);
        Assert.Contains("ReactorAgent", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Json_and_plain_text_are_returned_as_is()
    {
        await using var ws = new TestWorkspace();
        var (json, _) = Create(HttpStatusCode.OK, "{\n  \"a\": [1, 2]\n}", "application/json");
        var (text, _) = Create(HttpStatusCode.OK, "plain  text\n\n\n\nkept", "text/plain");

        Assert.Equal("{\n  \"a\": [1, 2]\n}", (await ws.RunAsync(json, """{"url":"http://api.example.com/data"}""")).Text);
        Assert.Equal("plain  text\n\n\n\nkept", (await ws.RunAsync(text, """{"url":"http://example.com/a.txt"}""")).Text);
    }

    [Fact]
    public async Task Charsets_are_honored()
    {
        await using var ws = new TestWorkspace();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.Latin1.GetBytes("caf\u00E9")) { Headers = { { "Content-Type", "text/plain; charset=iso-8859-1" } } },
        });

        var output = await ws.RunAsync(CoreTools.WebFetch(new HttpClient(handler)), """{"url":"https://example.com/latin1"}""");

        Assert.Equal("caf\u00E9", output.Text);
    }

    [Fact]
    public async Task Missing_content_type_is_sniffed()
    {
        await using var ws = new TestWorkspace();
        var (html, _) = Create(HttpStatusCode.OK, "<html><body><p>Hi</p></body></html>", null);

        Assert.Equal("Hi", (await ws.RunAsync(html, """{"url":"https://example.com"}""")).Text);
    }

    [Fact]
    public async Task Output_is_capped_at_max_length()
    {
        await using var ws = new TestWorkspace();
        var (tool, _) = Create(HttpStatusCode.OK, new string('x', 500), "text/plain");

        var output = (await ws.RunAsync(tool, Args.Of(("url", "https://example.com/big"), ("max_length", 100)))).Text;

        Assert.Equal(new string('x', 100) + "\n\n[Content truncated: showing the first 100 of 500 characters. Increase max_length to see more.]", output);
    }

    [Fact]
    public async Task Unsupported_content_and_http_errors_are_reported()
    {
        await using var ws = new TestWorkspace();
        var (image, _) = Create(HttpStatusCode.OK, "PNG", "image/png");
        var (notFound, _) = Create(HttpStatusCode.NotFound, "missing", "text/plain");

        var unsupported = await Assert.ThrowsAsync<NotSupportedException>(() => ws.RunAsync(image, """{"url":"https://example.com/a.png"}"""));
        var http = await Assert.ThrowsAsync<HttpRequestException>(() => ws.RunAsync(notFound, """{"url":"https://example.com/missing"}"""));

        Assert.Contains("Unsupported content type 'image/png'", unsupported.Message);
        Assert.Equal("Fetching https://example.com/missing failed with HTTP 404 Not Found.", http.Message);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    public async Task Only_http_and_https_urls_are_allowed(string url)
    {
        await using var ws = new TestWorkspace();
        var (tool, handler) = Create(HttpStatusCode.OK, "x", "text/plain");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => ws.RunAsync(tool, Args.Of(("url", url))));

        Assert.Contains("only absolute http:// and https:// URLs are supported", error.Message);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Network_failures_are_reported_with_the_url()
    {
        await using var ws = new TestWorkspace();
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => ws.RunAsync(CoreTools.WebFetch(new HttpClient(handler)), """{"url":"https://unreachable.example"}"""));

        Assert.Equal("Failed to fetch https://unreachable.example: connection refused", error.Message);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        await using var ws = new TestWorkspace();
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ws.RunAsync(CoreTools.WebFetch(new HttpClient(handler)), """{"url":"https://slow.example"}""", ws.Context(cancellationToken: cts.Token)));
    }

    [Fact]
    public void Html_to_text_handles_entities_scripts_and_blank_lines()
    {
        var text = HtmlToText.Convert("<script>alert(1)</script><p>a&nbsp;&nbsp;b &#169; &quot;q&quot;</p>\n\n\n\n<p>c</p><SCRIPT type=x>bad()</SCRIPT>");

        Assert.Equal("a b \u00A9 \"q\"\n\nc", text);
    }
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and records the last request.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _respond = (request, _) => Task.FromResult(respond(request));

    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return _respond(request, cancellationToken);
    }
}

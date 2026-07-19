using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Agent.Environments;
using Reactor.Llm;

namespace Reactor.Agent.Tools;

/// <summary><c>web_fetch</c>: fetches an http(s) URL and returns readable text.</summary>
internal static class WebFetchTool
{
    public const string Name = "web_fetch";
    public const int DefaultMaxLength = 50_000;

    /// <summary>The User-Agent header sent with every request.</summary>
    public const string UserAgent = "Mozilla/5.0 (compatible; ReactorAgent/0.1; +https://github.com/strongdm/attractor)";

    /// <summary>Overall time allowed for a fetch (connect, headers, and body).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const long MaxDownloadBytes = 10 * 1024 * 1024;

    private const string Description =
        "Fetch a web page or other text resource over HTTP or HTTPS and return its content as text. HTML pages are " +
        "converted to readable plain text (scripts, styles, and markup removed); plain text and JSON are returned as is. " +
        "Other content types such as images or PDFs are not supported. The result is capped at `max_length` characters " +
        "(default 50000).";

    private static readonly Lazy<HttpClient> SharedClient = new(
        () => new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static RegisteredTool Create(HttpClient? httpClient) => new(
        new ToolDefinition(Name, Description, ToolSchema.Object(
            [
                ("url", ToolSchema.String("The http:// or https:// URL to fetch.")),
                ("max_length", ToolSchema.Integer("Maximum number of characters to return. Defaults to 50000.")),
            ],
            "url")),
        (arguments, context) => ExecuteAsync(arguments, context, httpClient ?? SharedClient.Value));

    private static async Task<ToolOutput> ExecuteAsync(JsonObject arguments, ToolContext context, HttpClient client)
    {
        var url = ToolArguments.RequireString(arguments, "url").Trim();
        var maxLength = ToolArguments.OptionalInt(arguments, "max_length") ?? DefaultMaxLength;
        if (maxLength < 1)
        {
            throw new ArgumentException("max_length must be at least 1.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid URL '{url}': only absolute http:// and https:// URLs are supported.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation(
                "Accept", "text/html,application/xhtml+xml,text/plain;q=0.9,application/json;q=0.9,*/*;q=0.5");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "" : " " + response.ReasonPhrase.Trim();
                throw new HttpRequestException(
                    $"Fetching {url} failed with HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}{reason}.",
                    null,
                    response.StatusCode);
            }

            var contentType = response.Content.Headers.ContentType;
            var mediaType = contentType?.MediaType?.Trim().ToLowerInvariant();
            var bytes = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var kind = Classify(mediaType, bytes);
            if (kind == ContentKind.Unsupported)
            {
                throw new NotSupportedException(
                    $"Unsupported content type '{mediaType ?? "unknown"}' at {url}; web_fetch can only return HTML, text, or JSON.");
            }

            var text = Decode(bytes, contentType?.CharSet);
            if (kind == ContentKind.Html)
            {
                text = HtmlToText.Convert(text);
            }

            if (text.Length > maxLength)
            {
                var total = text.Length;
                var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
                text = text[..cut] + $"\n\n[Content truncated: showing the first {cut.ToString(CultureInfo.InvariantCulture)} of " +
                       $"{total.ToString(CultureInfo.InvariantCulture)} characters. Increase max_length to see more.]";
            }

            return text;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Fetching {url} timed out after {RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            throw new HttpRequestException($"Failed to fetch {url}: {ex.Message}", ex);
        }
    }

    private enum ContentKind
    {
        Html,
        Text,
        Unsupported,
    }

    private static ContentKind Classify(string? mediaType, byte[] body)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            if (TextFileDecoding.IsBinary(body))
            {
                return ContentKind.Unsupported;
            }

            var head = TextFileDecoding.Decode(body.AsSpan(0, Math.Min(body.Length, 1024))).Text.TrimStart();
            return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
                   head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                ? ContentKind.Html
                : ContentKind.Text;
        }

        if (mediaType is "text/html" or "application/xhtml+xml")
        {
            return ContentKind.Html;
        }

        if (mediaType.StartsWith("text/", StringComparison.Ordinal) ||
            mediaType.EndsWith("+json", StringComparison.Ordinal) ||
            mediaType.EndsWith("+xml", StringComparison.Ordinal) ||
            mediaType is "application/json" or "application/xml" or "application/javascript" or "application/x-javascript"
                or "application/ecmascript" or "application/yaml" or "application/x-yaml" or "application/toml"
                or "application/x-ndjson" or "application/ndjson" or "application/graphql" or "application/sql")
        {
            return ContentKind.Text;
        }

        return ContentKind.Unsupported;
    }

    private static string Decode(byte[] body, string? charset)
    {
        if (TextFileDecoding.DetectBom(body) is null && !string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim().Trim('"')).GetString(body);
            }
            catch (ArgumentException)
            {
                // Unknown charset: fall back to UTF-8.
            }
        }

        return TextFileDecoding.Decode(body).Text;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while (buffer.Length < MaxDownloadBytes &&
                   (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxDownloadBytes - buffer.Length)), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }
}

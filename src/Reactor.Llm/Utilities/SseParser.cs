using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Reactor.Llm.Utilities;

/// <summary>One Server-Sent Event.</summary>
/// <param name="Event">The <c>event:</c> field, or null for the default (<c>message</c>) event.</param>
/// <param name="Data">The joined <c>data:</c> lines.</param>
/// <param name="Id">The <c>id:</c> field.</param>
/// <param name="Retry">The <c>retry:</c> reconnection interval in milliseconds.</param>
public sealed record SseEvent(string? Event, string Data, string? Id = null, int? Retry = null);

/// <summary>
/// A spec-compliant Server-Sent Events parser: handles <c>event:</c>, multi-line <c>data:</c>, <c>id:</c>, <c>retry:</c>,
/// comment lines, blank-line event boundaries, and LF/CRLF/CR line endings.
/// </summary>
public static class SseParser
{
    /// <summary>Parses events from <paramref name="stream"/>.</summary>
    /// <param name="stream">The response body.</param>
    /// <param name="readTimeout">Maximum time to wait for the next line; <see cref="RequestTimeoutError"/> is thrown when exceeded.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        TimeSpan? readTimeout = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);
        await foreach (var evt in ParseAsync(reader, readTimeout, cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>Parses events from a text reader.</summary>
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        TextReader reader,
        TimeSpan? readTimeout = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? eventType = null;
        string? id = null;
        int? retry = null;
        var data = new StringBuilder();
        var hasData = false;
        var useTimeout = readTimeout is { } t && t > TimeSpan.Zero && t != Timeout.InfiniteTimeSpan;

        while (true)
        {
            string? line;
            if (useTimeout)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(readTimeout!.Value);
                try
                {
                    line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RequestTimeoutError(
                        $"No stream data received for {readTimeout.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s.", ex);
                }
            }
            else
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }

            if (line is null)
            {
                if (hasData)
                {
                    yield return new SseEvent(eventType, data.ToString(), id, retry);
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseEvent(eventType, data.ToString(), id, retry);
                }

                eventType = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }
            }

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    if (hasData)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    hasData = true;
                    break;
                case "id":
                    if (!value.Contains('\0', StringComparison.Ordinal))
                    {
                        id = value;
                    }

                    break;
                case "retry":
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
                    {
                        retry = ms;
                    }

                    break;
            }
        }
    }
}

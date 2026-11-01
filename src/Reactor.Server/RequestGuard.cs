using System.Net;
using Microsoft.AspNetCore.Http;

namespace Reactor.Server;

/// <summary>
/// Keeps web pages from driving a server that runs without a token. Pipelines run shell commands, so a page that can
/// start one is remote code execution. With a token, the token is the protection: pages cannot send it.
/// </summary>
internal static class RequestGuard
{
    /// <summary>Why <paramref name="request"/> is refused, or null when it may proceed.</summary>
    public static string? Refusal(HttpRequest request, string? authToken)
    {
        if (!string.IsNullOrEmpty(authToken))
        {
            return null;
        }

        // DNS rebinding: a page on attacker.example re-resolves its name to 127.0.0.1 and becomes same-origin with the
        // server. Names that cannot be rebound are localhost (*.localhost) and IP literals.
        if (!IsUnreboundableHost(request.Host))
        {
            return $"Requests for host '{request.Host.Host}' need a token: use localhost or an IP address, or start the server with --token.";
        }

        // Cross-site request forgery: a browser sends any page's form or text/plain POST here without a CORS preflight,
        // and labels it with the page's origin. Command-line clients and scripts send no Origin.
        if (!IsSafeMethod(request.Method) && request.Headers.Origin.ToString() is { Length: > 0 } origin && !IsSameOrigin(origin, request))
        {
            return $"Cross-origin requests (from '{origin}') are refused.";
        }

        return null;
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsUnreboundableHost(HostString host)
    {
        if (!host.HasValue)
        {
            return true;
        }

        var name = HostName(host.Host);
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(name, out _);
    }

    private static bool IsSameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(HostName(uri.Host), HostName(request.Host.Host), StringComparison.OrdinalIgnoreCase)
        && uri.Port == (request.Host.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80));

    private static string HostName(string host) => host.Trim('[', ']');
}

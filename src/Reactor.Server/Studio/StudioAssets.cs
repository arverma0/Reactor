using System.Collections.Frozen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Reactor.Server.Studio;

/// <summary>
/// Serves the Studio web app embedded in this assembly (built from <c>src/Reactor.Studio</c> into resources named
/// <c>Reactor.Server.studio/&lt;path&gt;</c>). When the app was not built (no Node.js at build time), <c>/</c> serves a
/// placeholder that links to the classic console.
/// </summary>
public static class StudioAssets
{
    private const string Prefix = "Reactor.Server.studio/";

    private static readonly Lazy<FrozenDictionary<string, string>> Resources = new(() =>
        typeof(StudioAssets).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToFrozenDictionary(n => n[Prefix.Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal));

    /// <summary>True when the Studio bundle is embedded.</summary>
    public static bool IsAvailable => Resources.Value.ContainsKey("index.html");

    /// <summary>Maps <c>/</c> (the Studio), <c>/assets/*</c>, and root files from the bundle.</summary>
    public static WebApplication MapStudioAssets(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/", () => IsAvailable ? Serve("index.html", noCache: true) : Results.Text(Placeholder, "text/html; charset=utf-8"));
        app.MapGet("/studio", () => Results.Redirect("/"));
        app.MapGet("/assets/{**path}", (string path) => Serve("assets/" + path, noCache: false));
        app.MapGet("/favicon.svg", () => Serve("favicon.svg", noCache: false));
        return app;
    }

    /// <summary>Paths served without authentication (the app shell and its static files).</summary>
    public static bool IsPublicPath(PathString path) =>
        path == "/" || path == "/studio" || path == "/favicon.svg" || path.StartsWithSegments("/assets", StringComparison.Ordinal);

    private static IResult Serve(string path, bool noCache)
    {
        if (path.Contains("..", StringComparison.Ordinal) || !Resources.Value.TryGetValue(path, out var resource))
        {
            return Results.NotFound();
        }

        var stream = typeof(StudioAssets).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return Results.NotFound();
        }

        return new AssetResult(stream, ContentType(path), noCache ? "no-cache" : "public, max-age=31536000, immutable");
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private sealed class AssetResult(Stream stream, string contentType, string cacheControl) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            await using (stream.ConfigureAwait(false))
            {
                httpContext.Response.ContentType = contentType;
                httpContext.Response.Headers.CacheControl = cacheControl;
                httpContext.Response.Headers.XContentTypeOptions = "nosniff";
                httpContext.Response.ContentLength = stream.Length;
                await stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    private const string Placeholder = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Reactor</title>
        <style>
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #0b0c0e; color: #e9e6df;
                 font: 15px/1.6 ui-sans-serif, system-ui, "Segoe UI", sans-serif; }
          main { max-width: 34rem; padding: 2rem; }
          h1 { font-weight: 500; letter-spacing: -.01em; margin: 0 0 .75rem; }
          code { font: 13px ui-monospace, Consolas, monospace; color: #ff6b1a; }
          a { color: #e9e6df; }
          p { color: #a9a59c; }
        </style></head>
        <body><main>
          <h1>Reactor Studio isn't built</h1>
          <p>This server was built without the Studio web app (Node.js 20.19+ was not found at build time).
             Install Node.js and rebuild — <code>dotnet build</code> builds the app automatically.</p>
          <p>The classic pipeline console is available at <a href="/console">/console</a>.</p>
        </main></body></html>
        """;
}

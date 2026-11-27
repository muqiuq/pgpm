using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using PGPM.Api;

namespace PGPM.Hosting;

/// <summary>
/// The web host's plumbing (docs/03 §3.1, §11.1): the reverse proxy, the WebSocket keep-alive, the response headers, the
/// web UI's files at <c>/</c> with its SPA fallback, and the plain 404 for everything that matches no endpoint.
/// </summary>
public static partial class HostingSetup
{
    /// <summary>The live sync channel's keep-alive (docs/03 §3.1): below the 60 s a reverse proxy must allow idle.</summary>
    public static readonly WebSocketOptions WebSocketOptions = new() { KeepAliveInterval = TimeSpan.FromSeconds(25) };

    /// <summary>The MCP endpoint (phase 6): authenticates by its own token scheme, never by the session cookie.</summary>
    public static bool IsMcpPath(PathString path) => path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddPgpmHosting(this IServiceCollection services, IConfiguration configuration)
    {
        // Proxy: docker, addresses and networks, comma-separated (PNMTD). Parsed here, so a bad entry stops the start instead
        // of trusting nothing.
        if (!string.IsNullOrWhiteSpace(configuration["Proxy"]))
        {
            var proxy = configuration["Proxy"]!;
            ProxySetting.Parse(proxy);
            services.Configure<ForwardedHeadersOptions>(options => ProxySetting.Configure(options, proxy));
        }
        return services;
    }

    // --- response headers --------------------------------------------------------------------------------------------

    /// <summary>
    /// The web UI's policy. PNMTD's plus what a PWA needs: <c>worker-src 'self'</c> (the service worker),
    /// <c>manifest-src 'self'</c>, <c>img-src blob:</c> (images and thumbnails shown from IndexedDB via
    /// <c>URL.createObjectURL</c>), and the live channel's <c>ws(s)://</c> origin in <c>connect-src</c>: older Safari
    /// versions don't match a WebSocket against <c>'self'</c>. <c>style-src 'unsafe-inline'</c> is PrimeVue's styled
    /// mode, which writes its theme into &lt;style&gt; elements. No <c>data:</c> anywhere (assetsInlineLimit: 0).
    /// </summary>
    private const string UiCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob:; font-src 'self'; " +
        "connect-src 'self'{0}; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; " +
        "form-action 'self'; frame-ancestors 'none'";

    private const string ApiCsp = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>Security headers on every response; the UI's CSP everywhere but /api and /healthz.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        var publicUrl = app.ApplicationServices.GetRequiredService<IConfiguration>()["PublicUrl"];
        var publicWebSocketOrigin = WebSocketOrigin(publicUrl);
        return app.Use((context, next) =>
        {
            var path = context.Request.Path;
            var isApi = ApiPaths.IsApi(path) || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase);
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                if (isApi)
                {
                    headers.ContentSecurityPolicy = ApiCsp;
                    if (!headers.ContainsKey("Cache-Control"))
                    {
                        headers.CacheControl = "no-store";
                    }
                }
                else
                {
                    var ws = publicWebSocketOrigin ?? WebSocketOrigin(context.Request);
                    headers.ContentSecurityPolicy = string.Format(System.Globalization.CultureInfo.InvariantCulture, UiCsp,
                        ws is null ? "" : " " + ws);
                }
                return Task.CompletedTask;
            });
            return next(context);
        });
    }

    /// <summary><c>wss://host[:port]</c> of the configured PublicUrl, or null.</summary>
    internal static string? WebSocketOrigin(string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl) || !Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }
        return $"{(uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws")}://{uri.Authority}";
    }

    /// <summary>The request's own origin as a WebSocket origin; nothing for a Host header that isn't a plain host[:port].</summary>
    internal static string? WebSocketOrigin(HttpRequest request)
    {
        var host = request.Host.Value;
        if (string.IsNullOrEmpty(host) || !PlainHost().IsMatch(host))
        {
            return null;
        }
        return $"{(request.IsHttps ? "wss" : "ws")}://{host}";
    }

    [GeneratedRegex(@"^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(:[0-9]{1,5})?$")]
    private static partial Regex PlainHost();

    // --- the web UI ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Vite's hashed assets never change: cached for a year. Everything else (index.html, sw.js, the manifest, the icons)
    /// is revalidated, so a release is seen at once: the service worker's update check never comes from a cache.
    /// </summary>
    internal static readonly StaticFileOptions FileOptions = new()
    {
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.CacheControl =
                context.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
        },
    };

    /// <summary>The files of the web UI's build in wwwroot (anonymous: the shell holds no data).</summary>
    public static IApplicationBuilder UseSpaFiles(this IApplicationBuilder app) => app.UseStaticFiles(FileOptions);

    /// <summary>
    /// Deep links (/projects/…) get index.html. Paths with a file extension do not, so a missing asset is a 404 rather
    /// than HTML. Unknown /api paths get a ProblemDetails 404 (401 when signed out), never the UI.
    /// </summary>
    public static void MapSpaFallback(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapFallbackToFile("{**path:nonfile}", "index.html", FileOptions)
            .AllowAnonymous();
        endpoints.MapFallback(ApiPaths.V1 + "/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound))
            .ExcludeFromDescription();
        endpoints.MapFallback("/api/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound))
            .ExcludeFromDescription();
    }

    /// <summary>
    /// Between routing and authentication: a request that matches no endpoint (a missing asset) is an empty 404, and
    /// routing's 405 stays a 405 – deny by default would answer both with 401.
    /// </summary>
    public static IApplicationBuilder UsePlainNotFound(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            if (endpoint.DisplayName == Http405EndpointDisplayName && endpoint.RequestDelegate is { } reject)
            {
                return reject(context);
            }
            return next(context);
        });

    /// <summary>The name of the endpoint HttpMethodMatcherPolicy creates for a 405 (not public API).</summary>
    public const string Http405EndpointDisplayName = "405 HTTP Method Not Supported";
}

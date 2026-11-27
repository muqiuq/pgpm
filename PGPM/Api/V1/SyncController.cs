using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Hosting;
using PGPM.Sharing;
using PGPM.Sync;
using PGPM.Sync.Live;

namespace PGPM.Api.V1;

/// <summary>
/// The sync core's two transports (docs/03 §11.4): <c>POST /api/v1/sync</c> for every write and the catch-up pages, and
/// the live WebSocket <c>GET /api/v1/sync/live</c> that delivers changes while online. Rights and access are checked per
/// mutation and per row, not per endpoint: any signed-in user may call them.
/// </summary>
[Route("api/v1/sync")]
public sealed class SyncController(SyncService sync, LiveSyncHub hub, IOptions<SyncOptions> options, IConfiguration configuration) : V1ControllerBase
{
    /// <summary>Push (mutations, each with its result) and pull (a page after <c>since</c>). 503 <c>busy</c>: retry with backoff.</summary>
    [HttpPost]
    [SyncBodyLimit]
    [NotAudited("Every accepted change is in the change log (History), with its user and source.")]
    public async Task<SyncResponse> Sync([FromBody] SyncRequest request, CancellationToken ct) =>
        await sync.SyncAsync(await Viewer.OfAsync(HttpContext, ct), request, HttpContext, ct);

    /// <summary>
    /// The live channel (a WebSocket upgrade; 400 for a plain request). The <c>Origin</c> must be this server's (PublicUrl,
    /// else the request's own scheme and host): 403 before the upgrade otherwise. Then JSON messages as docs/03 §11.4.
    /// </summary>
    [HttpGet("live")]
    public async Task Live(CancellationToken ct)
    {
        if (!options.Value.Live.Enabled)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "live_disabled", "The live channel is switched off (Sync:Live:Enabled).");
        }
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "websocket_required", "The live channel is a WebSocket.");
        }
        if (!OriginAllowed(Request, configuration["PublicUrl"]))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "origin_not_allowed", "The live channel accepts this server's own pages only.");
        }
        var viewer = await Viewer.OfAsync(HttpContext, ct);
        var sessions = HttpContext.RequestServices.GetRequiredService<SessionCache>();
        var db = HttpContext.RequestServices.GetRequiredService<Data.PgpmDbContext>();
        var entry = await sessions.GetAsync(viewer.UserId, () => SessionCache.LoadAsync(db, viewer.UserId, ct)) ?? throw ApiException.Unauthorized();
        var session = new LiveSession(viewer, User.FindFirst(SessionClaims.SecurityStamp)?.Value, SessionRules.Time(User, SessionClaims.SignedIn), entry,
            Request.Headers.UserAgent.ToString());
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await hub.RunAsync(socket, session, HttpContext.RequestAborted);
    }

    /// <summary>Cross-site WebSocket hijacking (docs/03 §11.4): the page's origin must be this server's.</summary>
    internal static bool OriginAllowed(HttpRequest request, string? publicUrl)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }
        var expected = HostingSetup.WebSocketOrigin(publicUrl) is { } ws
            ? (ws.StartsWith("wss://", StringComparison.Ordinal) ? "https://" + ws[6..] : "http://" + ws[5..])
            : HostingSetup.WebSocketOrigin(request) is { } own
                ? (own.StartsWith("wss://", StringComparison.Ordinal) ? "https://" + own[6..] : "http://" + own[5..])
                : null;
        return expected is not null && string.Equals(origin.TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Sync:MaxBodyBytes for the push (8 MB): set before the body is read.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SyncBodyLimitAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = context.HttpContext.RequestServices.GetRequiredService<IOptions<SyncOptions>>().Value.MaxBodyBytes;
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

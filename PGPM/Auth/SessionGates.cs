using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PGPM.Api;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Auth;

/// <summary>
/// What a session must do before anything else (PNMTD, docs/03 §4.2). Every other /api/v1 call is 403 with the code:
/// <list type="bullet">
/// <item>must_change_password (a temporary password, or the seeded admin's): it may only change it, sign out and read who
///   it is. The claim goes when the password changes and the cookie is issued anew.</item>
/// <item>totp_setup_required (Auth:TotpPolicy, and no 2FA yet): it may also set 2FA up. The password comes first, so this
///   one allows changing it.</item>
/// <item>totp_due: the day's code. Only who it is, signing out, and the code itself.</item>
/// </list>
/// A Microsoft session never has these claims (docs/03 §5.2). A request that passes them is the person being online:
/// <see cref="LastSeen"/>.
/// </summary>
public static class SessionGates
{
    private static readonly (string Claim, string Code, string Detail, string[] Allowed)[] Gates =
    [
        (SessionClaims.MustChangePassword, "must_change_password", "Change your password first.",
            ["/api/v1/auth", "/api/v1/account/password", "/api/v1/info"]),
        (SessionClaims.TotpSetupPending, "totp_setup_required", "Set up two-factor authentication first.",
            ["/api/v1/auth", "/api/v1/account/password", "/api/v1/account/totp", "/api/v1/info"]),
        (SessionClaims.TotpDue, SessionRules.TotpDueCode, "Enter your 2FA code for today.",
            ["/api/v1/auth", "/api/v1/info"]),
    ];

    public static IApplicationBuilder UseSessionGates(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments(ApiPaths.V1, StringComparison.OrdinalIgnoreCase))
            {
                var gated = false;
                foreach (var (claim, code, detail, allowed) in Gates)
                {
                    if (!context.User.HasClaim(claim, "true"))
                    {
                        continue;
                    }
                    gated = true;
                    if (!allowed.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase)))
                    {
                        await Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: detail,
                            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);
                        return;
                    }
                }
                if (!gated)
                {
                    await LastSeen.TouchAsync(context);
                }
            }
            await next(context);
        });
}

/// <summary>
/// <c>users.last_seen_at</c> (docs/03 §4.2): a request of the web UI (or, from phase 2, the sync) that no gate holds up.
/// Written at most every <see cref="Every"/> per user, so a page's polling costs one small update in five minutes.
/// Only people's sessions count: not MCP tokens, not the CLI.
/// </summary>
public static class LastSeen
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>The authentication types that are a person in the web UI (and the tests' sign-in, which stands for it).</summary>
    public static bool IsPerson(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true, AuthenticationType: { } type }
        && (type == CookieAuthenticationDefaults.AuthenticationScheme || type == "Test");

    public static async Task TouchAsync(HttpContext context)
    {
        if (!IsPerson(context.User) || context.User.UserId() is not { } id)
        {
            return;
        }
        var cache = context.RequestServices.GetRequiredService<IMemoryCache>();
        var key = $"last-seen:{id}";
        var now = Clock.UtcNow;
        // Compared on PGPM's clock, not the cache's own, so the rule follows Clock like every other one.
        if (cache.TryGetValue(key, out DateTime written) && now - written < Every)
        {
            return;
        }
        cache.Set(key, now, TimeSpan.FromHours(1));
        // A bulk update: no tracked change, so no audit entry for being online.
        await context.RequestServices.GetRequiredService<PgpmDbContext>().Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now), context.RequestAborted);
    }
}

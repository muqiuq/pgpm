using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Auth;

/// <summary>
/// What a session is checked against on every request (PNMTD, docs/03 §4.2): the user's stamp and active state, cached
/// for up to a minute, and what it may do: the admin flag and the levels of its groups, and the groups themselves (the
/// sharing checks of phase 2). This server forgets an entry the moment it changes the user (<see cref="Forget"/>), so its
/// own changes apply at once; a `pgpm users …` command (another process) takes up to the minute.
/// </summary>
public sealed class SessionCache(IMemoryCache cache)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    /// <summary><see cref="AccessEpoch"/>: the live channel resets the user's clients when it changes (docs/03 §11.6).</summary>
    public sealed record Entry(string Stamp, bool IsActive, bool IsTotpEnabled, UserRights Rights, IReadOnlyList<Guid> GroupIds, int AccessEpoch = 1);

    /// <summary>The user's entry from the database: the flag, the highest level their groups give per area, their groups.</summary>
    public static async Task<Entry?> LoadAsync(PgpmDbContext db, Guid id, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new { u.SecurityStamp, u.IsActive, u.IsTotpEnabled, u.IsAdmin, u.AccessEpoch }).SingleOrDefaultAsync(ct);
        if (user is null)
        {
            return null;
        }
        var groupIds = await db.UserGroups.AsNoTracking().Where(m => m.UserId == id).Select(m => m.GroupId).ToListAsync(ct);
        var levels = await db.GroupRights.AsNoTracking().Where(r => groupIds.Contains(r.GroupId)).Select(r => new { r.Area, r.Level }).ToListAsync(ct);
        return new Entry(user.SecurityStamp, user.IsActive, user.IsTotpEnabled,
            new UserRights(user.IsAdmin, UserRights.Combine(levels.Select(l => (l.Area, l.Level)))), groupIds, user.AccessEpoch);
    }

    public async Task<Entry?> GetAsync(Guid userId, Func<Task<Entry?>> load) =>
        await cache.GetOrCreateAsync(Key(userId), e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            return load();
        });

    /// <summary>
    /// After a change of the user's stamp, flags, groups or rights: the next request loads them again, and the user's live
    /// connections check their session at once (<see cref="Forgotten"/>: <c>session</c> messages, <c>reset</c> after an
    /// access epoch change, docs/03 §11.6).
    /// </summary>
    public void Forget(Guid userId)
    {
        cache.Remove(Key(userId));
        Forgotten?.Invoke(userId);
    }

    /// <summary>Raised by <see cref="Forget"/> (the live sync hub listens).</summary>
    public event Action<Guid>? Forgotten;

    private static string Key(Guid userId) => $"session:{userId}";

    /// <summary>
    /// The cookie handler's OnValidatePrincipal: a session whose user changed, was deactivated or whose 30 days ended is
    /// signed out. Otherwise the request gets the rights, the groups, and the gates' claims.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var userId = principal?.UserId();
        var stamp = principal?.FindFirstValue(SessionClaims.SecurityStamp);
        Entry? entry = null;
        var services = context.HttpContext.RequestServices;
        if (userId is { } id && stamp is not null)
        {
            entry = await services.GetRequiredService<SessionCache>().GetAsync(id,
                () => LoadAsync(services.GetRequiredService<PgpmDbContext>(), id, context.HttpContext.RequestAborted));
        }
        if (entry is null || !entry.IsActive || entry.Stamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        var identity = (ClaimsIdentity)principal!.Identity!;
        var options = services.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;
        var now = Clock.UtcNow;
        var signedIn = SessionRules.Time(principal, SessionClaims.SignedIn);
        var totpAt = SessionRules.Time(principal, SessionClaims.TotpAt);
        // Every PGPM session says when it signed in; one that doesn't is no session of this server's.
        if (signedIn is null || SessionRules.LoginEnds(signedIn.Value, options) is { } ends && ends <= now)
        {
            if (signedIn is not null)
            {
                context.HttpContext.Items[SessionRules.LoginExpiredItem] = true;
            }
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        foreach (var claim in identity.Claims.Where(c => SessionClaims.PerRequest.Contains(c.Type)).ToList())
        {
            identity.RemoveClaim(claim);
        }
        identity.AddClaims(entry.Rights.Claims());
        identity.AddClaims(entry.GroupIds.Select(g => new Claim(SessionClaims.Group, g.ToString())));
        // A Microsoft session's second factor is Entra's (docs/03 §4.2): no TOTP gates.
        if (principal.Method() == AuthMethod.Microsoft)
        {
            return;
        }
        var policy = AuthService.Policy(services.GetRequiredService<IConfiguration>());
        if (!entry.IsTotpEnabled && AuthService.TotpRequired(policy, entry.Rights.IsAdmin))
        {
            identity.AddClaim(new Claim(SessionClaims.TotpSetupPending, "true"));
        }
        if (entry.IsTotpEnabled && options.TotpDays > 0 && (totpAt is null || SessionRules.TotpDue(totpAt.Value, options) <= now))
        {
            identity.AddClaim(new Claim(SessionClaims.TotpDue, "true"));
        }
    }
}

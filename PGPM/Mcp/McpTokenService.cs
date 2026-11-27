using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Mcp;

/// <summary>
/// What the user behind a token is, as far as the token's state needs it: a write token needs a second factor – TOTP, or
/// a linked Microsoft account (docs/03 §15 #12) –, and every token needs <i>MCP: Use</i>.
/// </summary>
public sealed record McpTokenUser(Guid Id, string Username, string DisplayName, bool IsActive, string SecurityStamp, bool IsTotpEnabled,
    bool HasMicrosoftAccount, DateTime? LastSeenAt, bool CanUse = true)
{
    public bool HasSecondFactor => IsTotpEnabled || HasMicrosoftAccount;
}

/// <summary>
/// Whether a token is on, until when, and if not, why (docs/03 §7.1, PNMTD's McpTokenState). <see cref="Why"/>: <c>on</c>,
/// <c>off</c>, <c>no_value</c>, <c>expired</c>, <c>blocked</c>, <c>stamp</c>, <c>inactive</c>, <c>totp</c>, <c>no_right</c>,
/// <c>mcp_off</c>. Evaluated per request; <see cref="McpTokenExpiryTask"/> only records ends.
/// </summary>
public sealed record McpTokenState(bool On, DateTime? Until, string Why)
{
    public static McpTokenState Of(McpTokenEntity token, McpTokenUser user, McpOptions options, DateTime now)
    {
        if (!options.Enabled)
        {
            return new(false, null, "mcp_off");
        }
        if (token.BlockedAt is not null)
        {
            return new(false, null, "blocked");
        }
        if (token.OnSince is not { } since)
        {
            return new(false, null, "off");
        }
        if (token.Hash is null)
        {
            return new(false, null, "no_value");
        }
        if (!user.IsActive)
        {
            return new(false, null, "inactive");
        }
        if (token.OnStamp != user.SecurityStamp)
        {
            return new(false, null, "stamp");
        }
        if (token.Kind == McpTokenKind.Write && options.WriteRequiresTotp && !user.HasSecondFactor)
        {
            return new(false, null, "totp");
        }
        var until = token.Kind == McpTokenKind.Write
            ? token.OnUntil ?? since.AddHours(options.WriteHours)
            : (user.LastSeenAt is { } seen && seen > since ? seen : since).AddDays(options.ReadDaysAfterOnline);
        if (until <= now)
        {
            return new(false, until, "expired");
        }
        // Without MCP: Use the token is refused, but stays on: it works again when the right comes back.
        return user.CanUse ? new(true, until, "on") : new(false, until, "no_right");
    }

    /// <summary>The end to record for a token that went off without anyone switching it off.</summary>
    public string? End => Why switch
    {
        "expired" => McpTokenEnds.Expired,
        "stamp" => McpTokenEnds.Stamp,
        "inactive" => McpTokenEnds.UserInactive,
        "totp" => McpTokenEnds.TotpOff,
        _ => null,
    };
}

/// <summary>A token checked for a request: its row, its user, and what MCP lets it do.</summary>
public sealed record McpCaller(McpTokenEntity Token, McpTokenUser User, UserRights Rights);

/// <summary>
/// The tokens (docs/03 §7.1, PNMTD's McpTokenService): a user's own two, switched on, off and regenerated on Account →
/// Security; an admin's <i>off with a reason</i> and <i>Revoke all</i>; the check of a presented value. Every change is an
/// audit event <c>mcp.*</c>.
/// </summary>
public sealed class McpTokenService(PgpmDbContext db, IOptionsMonitor<McpOptions> options, IHttpContextAccessor accessor)
{
    private McpOptions Options => options.CurrentValue;

    private AuditWho Who => AuditWho.From(accessor);

    public static string KindLabel(McpTokenKind kind) => kind == McpTokenKind.Write ? "write" : "read";

    public static AuditTarget Target(McpTokenEntity token, string username) =>
        new(AuditSubjects.McpToken, token.Id, $"{username}: {KindLabel(token.Kind)} token");

    /// <summary>Both rows of a user, made if missing.</summary>
    public async Task<(McpTokenEntity Read, McpTokenEntity Write)> RowsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.McpTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        if (rows.Count < 2)
        {
            Guid read = Ids.New(), write = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO mcp_tokens (id, user_id, kind, delete_count, strikes)
                VALUES ({read}, {userId}, 'read', 0, ARRAY[]::timestamp with time zone[]),
                       ({write}, {userId}, 'write', 0, ARRAY[]::timestamp with time zone[])
                ON CONFLICT (user_id, kind) DO NOTHING
                """, ct);
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
            rows = await db.McpTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        }
        return (rows.Single(t => t.Kind == McpTokenKind.Read), rows.Single(t => t.Kind == McpTokenKind.Write));
    }

    /// <summary>The user as the token's state needs them, with <i>MCP: Use</i> from their groups.</summary>
    public async Task<McpTokenUser> UserAsync(Guid userId, CancellationToken ct) => (await UserAndRightsAsync(userId, ct)).User;

    /// <summary>The user and their rights (the admin flag and their groups' levels), read now.</summary>
    private async Task<(McpTokenUser User, UserRights Rights)> UserAndRightsAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new McpTokenUser(u.Id, u.Username, u.DisplayName, u.IsActive, u.SecurityStamp, u.IsTotpEnabled, u.MicrosoftObjectId != null,
                u.LastSeenAt, true))
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("User");
        var rights = (await SessionCache.LoadAsync(db, userId, ct))?.Rights ?? UserRights.None;
        return (user with { CanUse = rights.Has(Area.Mcp, Level.Use) }, rights);
    }

    /// <summary>Every user with tokens, for the admin's list, the status and the expiry task.</summary>
    public static async Task<Dictionary<Guid, McpTokenUser>> UsersAsync(PgpmDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new { u.Id, u.Username, u.DisplayName, u.IsActive, u.SecurityStamp, u.IsTotpEnabled, u.MicrosoftObjectId, u.LastSeenAt, u.IsAdmin })
            .ToListAsync(ct);
        // Every MCP level includes Use: an admin, or a member of a group with any of them.
        var holders = (await db.UserGroups.AsNoTracking().Where(m => list.Contains(m.UserId))
            .Join(db.GroupRights.Where(r => r.Area == Area.Mcp), m => m.GroupId, r => r.GroupId, (m, r) => m.UserId).Distinct().ToListAsync(ct))
            .ToHashSet();
        return users.ToDictionary(u => u.Id, u => new McpTokenUser(u.Id, u.Username, u.DisplayName, u.IsActive, u.SecurityStamp, u.IsTotpEnabled,
            u.MicrosoftObjectId != null, u.LastSeenAt, u.IsAdmin || holders.Contains(u.Id)));
    }

    private async Task<(McpTokenEntity Token, McpTokenUser User)> MineAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        if (!Options.Enabled)
        {
            throw ApiException.Conflict("mcp_off", "The MCP server is off (Mcp:Enabled).");
        }
        var (read, write) = await RowsAsync(userId, ct);
        return (kind == McpTokenKind.Write ? write : read, await UserAsync(userId, ct));
    }

    /// <summary>
    /// Switches a token on, or extends the write token's hours. Returns the value if one was made now: the first time, or
    /// after <i>Revoke all</i>.
    /// </summary>
    public async Task<(McpTokenEntity Token, McpTokenUser User, string? Value)> TurnOnAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, kind, ct);
        if (kind == McpTokenKind.Write && Options.WriteRequiresTotp && !user.HasSecondFactor)
        {
            throw ApiException.Conflict("totp_required",
                "The write token needs two-factor authentication on your account, or a linked Microsoft account.");
        }
        var now = Clock.UtcNow;
        string? value = null;
        if (token.Hash is null)
        {
            value = Issue(token, now);
        }
        var extended = McpTokenState.Of(token, user, Options, now).On;
        token.OnSince = now;
        token.OnUntil = kind == McpTokenKind.Write ? now.AddHours(Options.WriteHours) : null;
        token.OnStamp = user.SecurityStamp;
        token.BlockedAt = null;
        token.Strikes = [];
        token.EndedAt = null;
        token.EndedWhy = null;
        token.EndedNote = null;
        var until = McpTokenState.Of(token, user, Options, now).Until;
        AuditLog.Record(db, AuditEvents.McpTokenOn,
            (kind == McpTokenKind.Write
                ? $"{(extended ? "Extended" : "On")} for {Options.WriteHours} hours, until {Clock.ToLocal(until ?? now):yyyy-MM-dd HH:mm}"
                : $"On until {Options.ReadDaysAfterOnline} days after last online") + (value is null ? "" : "; a new value was made"),
            Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user, value);
    }

    public async Task<(McpTokenEntity Token, McpTokenUser User)> TurnOffAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, kind, ct);
        End(token, McpTokenEnds.Off, Clock.UtcNow);
        AuditLog.Record(db, AuditEvents.McpTokenOff, "Off", Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user);
    }

    /// <summary>A new value; the old one stops working at once. The token stays on or off as it was.</summary>
    public async Task<(McpTokenEntity Token, McpTokenUser User, string Value)> RegenerateAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, kind, ct);
        var hadOne = token.Hash is not null;
        var value = Issue(token, Clock.UtcNow);
        AuditLog.Record(db, AuditEvents.McpTokenRegenerated, hadOne ? "A new value; the old one stopped working" : "The first value",
            Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user, value);
    }

    /// <summary>An admin with <i>MCP: Manage</i> switches someone's token off, with a reason the user reads.</summary>
    public async Task<(McpTokenEntity Token, McpTokenUser User)> TurnOffByAdminAsync(Guid tokenId, string? reason, CancellationToken ct)
    {
        var why = Reason(reason, "the token's user reads it.");
        var token = await db.McpTokens.SingleOrDefaultAsync(t => t.Id == tokenId, ct) ?? throw ApiException.NotFound("Token");
        var user = await UserAsync(token.UserId, ct);
        End(token, McpTokenEnds.Admin, Clock.UtcNow, why);
        AuditLog.Record(db, AuditEvents.McpTokenOffByAdmin, why, Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user);
    }

    /// <summary>
    /// <i>Revoke all tokens</i> (MCP: Manage): every value stops working for good, every token goes off. Its user makes a new
    /// value by switching it on again. For a leak, or a change of the rules.
    /// </summary>
    public async Task<int> RevokeAllAsync(string? reason, CancellationToken ct)
    {
        var why = Reason(reason, "every user reads it.");
        var now = Clock.UtcNow;
        var tokens = await db.McpTokens.Where(t => t.Hash != null || t.OnSince != null).ToListAsync(ct);
        foreach (var token in tokens)
        {
            End(token, McpTokenEnds.Admin, now, why);
            token.Hash = null;
            token.Hint = null;
            token.IssuedAt = null;
            token.BlockedAt = null;
            token.Strikes = [];
        }
        AuditLog.Record(db, AuditEvents.McpTokensRevoked, $"{tokens.Count} tokens revoked: {why}", Who);
        await db.SaveChangesAsync(ct);
        return tokens.Count;
    }

    private static string Reason(string? reason, string reader)
    {
        var why = reason?.Trim() ?? "";
        new ApiValidator().Check(why.Length is >= 3 and <= 300, "reason", $"Why, in 3 to 300 characters: {reader}").ThrowIfInvalid();
        return why;
    }

    /// <summary>
    /// The check of a presented value (docs/03 §7.2): shaped like a token, its hash found, its user, and on. The caller (with
    /// why not) when the token is known; null when it isn't. Writes nothing: the request goes into the MCP log.
    /// </summary>
    public async Task<(McpCaller? Caller, string Why)> AuthenticateAsync(string? value, CancellationToken ct)
    {
        if (!Options.Enabled)
        {
            return (null, "mcp_off");
        }
        if (!McpTokenValues.LooksLikeOne(value))
        {
            return (null, "unknown");
        }
        var hash = McpTokenValues.Hash(value!);
        var token = await db.McpTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Hash == hash, ct);
        if (token is null)
        {
            return (null, "unknown");
        }
        var (user, rights) = await UserAndRightsAsync(token.UserId, ct);
        var state = McpTokenState.Of(token, user, Options, Clock.UtcNow);
        return state.On ? (new McpCaller(token, user, McpRights.For(rights, token.Kind)), "on") : (new McpCaller(token, user, UserRights.None), state.Why);
    }

    /// <summary>Who used it last, at most once a minute.</summary>
    public async Task TouchAsync(McpTokenEntity token, string? from, string? client, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        if (token.LastUsedAt is { } last && now - last < TimeSpan.FromMinutes(1) && token.LastUsedFrom == from && token.LastClient == client)
        {
            return;
        }
        await db.McpTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.LastUsedAt, now)
            .SetProperty(t => t.LastUsedFrom, McpLog.Cut(from, 64))
            .SetProperty(t => t.LastClient, McpLog.Cut(client, 200)), ct);
    }

    private static string Issue(McpTokenEntity token, DateTime now)
    {
        var value = McpTokenValues.New(token.Kind);
        token.Hash = McpTokenValues.Hash(value);
        token.Hint = McpTokenValues.Hint(value);
        token.IssuedAt = now;
        return value;
    }

    internal static void End(McpTokenEntity token, string why, DateTime at, string? note = null)
    {
        token.OnSince = null;
        token.OnUntil = null;
        token.OnStamp = null;
        token.DeleteUntil = null;
        token.EndedAt = at;
        token.EndedWhy = why;
        token.EndedNote = note;
    }
}

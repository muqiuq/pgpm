using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Mcp;
using PGPM.Mcp.Tools;

namespace PGPM.Api.V1;

/// <summary>
/// One's own MCP tokens (docs/03 §7.1): the MCP card on Account → Security. Every signed-in user has both rows; switching one
/// on or regenerating it needs <i>MCP: Use</i>, switching it off never does. The value is in a response once, never again
/// (<c>Cache-Control: no-store</c>).
/// </summary>
[Route("api/v1/account/mcp")]
[Authorize]
public sealed class AccountMcpController(McpTokenService tokens, IOptionsMonitor<McpOptions> options) : V1ControllerBase
{
    private Guid Me => User.UserId() ?? throw ApiException.Unauthorized();

    [HttpGet]
    public async Task<McpAccountItem> Get(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var (read, write) = await tokens.RowsAsync(Me, ct);
        var user = await tokens.UserAsync(Me, ct);
        return new McpAccountItem(o.Enabled, $"{McpCalls.BaseUrl(HttpContext)}{McpSetup.Path}", o.WriteHours, o.ReadDaysAfterOnline, o.WriteRequiresTotp,
            user.HasSecondFactor, user.CanUse, Item(read, user), Item(write, user));
    }

    /// <summary>Switches a token on, or extends the write token's hours. The value comes along if one was made now. 409 totp_required, mcp_off.</summary>
    [HttpPost("{kind}/on")]
    [RequireRight(Area.Mcp, Level.Use)]
    [Audit(AuditEvents.McpTokenOn)]
    public async Task<McpTokenIssued> TurnOn(string kind, CancellationToken ct)
    {
        var (token, user, value) = await tokens.TurnOnAsync(Me, KindOf(kind), ct);
        NoStore();
        return new McpTokenIssued(Item(token, user), value);
    }

    [HttpPost("{kind}/off")]
    [Audit(AuditEvents.McpTokenOff)]
    public async Task<McpTokenItem> TurnOff(string kind, CancellationToken ct)
    {
        var (token, user) = await tokens.TurnOffAsync(Me, KindOf(kind), ct);
        return Item(token, user);
    }

    /// <summary>A new value, shown once; the old one stops working at once. On or off stays as it was.</summary>
    [HttpPost("{kind}/regenerate")]
    [RequireRight(Area.Mcp, Level.Use)]
    [Audit(AuditEvents.McpTokenRegenerated)]
    public async Task<McpTokenIssued> Regenerate(string kind, CancellationToken ct)
    {
        var (token, user, value) = await tokens.RegenerateAsync(Me, KindOf(kind), ct);
        NoStore();
        return new McpTokenIssued(Item(token, user), value);
    }

    /// <summary><c>read</c> or <c>write</c>; anything else is no token (404).</summary>
    internal static McpTokenKind KindOf(string kind) => kind switch
    {
        "read" => McpTokenKind.Read,
        "write" => McpTokenKind.Write,
        _ => throw ApiException.NotFound("Token kind"),
    };

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private McpTokenItem Item(McpTokenEntity t, McpTokenUser user) =>
        Item(t, user, options.CurrentValue, McpRights.For(SessionRights(), t.Kind).Levels);

    /// <summary>The user's own rights as the session has them (the token's cap is applied on top).</summary>
    private UserRights SessionRights() => User.Rights();

    internal static McpTokenItem Item(McpTokenEntity t, McpTokenUser user, McpOptions options, IReadOnlyDictionary<Area, Level> rights)
    {
        var now = Clock.UtcNow;
        var state = McpTokenState.Of(t, user, options, now);
        return new McpTokenItem(t.Kind, state.On, state.Why, state.Until, t.Hash is not null, t.Hint, t.IssuedAt, t.LastUsedAt, t.LastUsedFrom, t.LastClient,
            t.EndedAt, t.EndedWhy, t.EndedNote, t.BlockedAt, t.Strikes.Count(s => s > now.AddHours(-24)), rights, McpRights.AccessCap(t.Kind));
    }
}

/// <summary>
/// Administration → MCP (docs/03 §7.1): the catalog for every signed-in user; the log, the status and everyone's tokens with
/// <i>MCP: View</i>; switching a token off and revoking all with <i>MCP: Manage</i>. An entry of one's own log is readable
/// without the right.
/// </summary>
[Route("api/v1/mcp")]
[Authorize]
public sealed class McpAdminController(PgpmDbContext db, IOptionsMonitor<McpOptions> options) : V1ControllerBase
{
    [HttpGet("catalog")]
    public McpCatalog Catalog()
    {
        var o = options.CurrentValue;
        var services = HttpContext.RequestServices;
        var ceiling = RightsCatalog.Areas.Select(a => new McpCeilingRow(a.Area, a.Label, McpRights.Ceiling.TryGetValue(a.Area, out var rw) ? rw : null,
            McpRights.ReadOnlyCap.TryGetValue(a.Area, out var ro) ? ro : null)).ToList();
        var tools = ToolCatalog.All.Select(t => new McpToolDoc(t.Name, t.Title, t.Description, t.Kind, t.RightText,
            ToolCatalog.KindAllows(t, McpTokenKind.Read), t.Kind == ToolKind.Delete || t.Destructive, t.InputSchema,
            t.Available?.Invoke(services) ?? true)).ToList();
        return new McpCatalog(o.Enabled, $"{McpCalls.BaseUrl(HttpContext)}{McpSetup.Path}", McpInstructions.Text, o.WriteHours, o.ReadDaysAfterOnline,
            o.WriteRequiresTotp, o.CallsPerMinute, o.WritesPerMinute, o.MaxResultChars, o.StrikesToBlock, o.LogEntries, ceiling, tools);
    }

    /// <summary>The tokens and the calls at a glance.</summary>
    [HttpGet("status")]
    [RequireRight(Area.Mcp, Level.View)]
    public async Task<McpStatus> Status(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        var tokens = await db.McpTokens.AsNoTracking().ToListAsync(ct);
        var users = await McpTokenService.UsersAsync(db, tokens.Select(t => t.UserId).ToList(), ct);
        var states = tokens.Where(t => users.ContainsKey(t.UserId)).Select(t => (Token: t, State: McpTokenState.Of(t, users[t.UserId], o, now))).ToList();
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);
        var calls = db.McpLog.AsNoTracking().Where(l => l.Method == "tools/call");
        var outcomes = await calls.Where(l => l.At >= day).GroupBy(l => l.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var tools = await calls.Where(l => l.At >= week && l.Tool != null).GroupBy(l => l.Tool!).Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(10).ToListAsync(ct);
        var people = await calls.Where(l => l.At >= week).GroupBy(l => l.Username).Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(10).ToListAsync(ct);
        var recent = await calls.Where(l => l.At >= week).Select(l => new { l.At, Ok = l.Outcome == McpOutcomes.Ok }).ToListAsync(ct);
        var today = DateOnly.FromDateTime(Clock.ToLocal(now));
        var days = Enumerable.Range(0, 7).Select(i => today.AddDays(-6 + i))
            .Select(d => new McpDay(d, recent.Count(r => DateOnly.FromDateTime(Clock.ToLocal(r.At)) == d),
                recent.Count(r => !r.Ok && DateOnly.FromDateTime(Clock.ToLocal(r.At)) == d))).ToList();
        return new McpStatus(o.Enabled,
            states.Count(s => s.State.On && s.Token.Kind == McpTokenKind.Read), states.Count(s => s.State.On && s.Token.Kind == McpTokenKind.Write),
            tokens.Count(t => t.BlockedAt is not null), tokens.Count(t => t.Hash is not null),
            outcomes.Sum(x => x.Count), recent.Count, await db.McpLog.CountAsync(l => l.Strike && l.At >= day, ct),
            await calls.MaxAsync(l => (DateTime?)l.At, ct),
            [.. outcomes.OrderByDescending(x => x.Count).Select(x => new McpCount(x.Key, x.Count))],
            [.. tools.Select(x => new McpCount(x.Key, x.Count))], [.. people.Select(x => new McpCount(x.Key, x.Count))], days);
    }

    [HttpGet("log")]
    [RequireRight(Area.Mcp, Level.View)]
    public async Task<Paged<McpLogItem>> Log([FromQuery] McpLogQuery query, CancellationToken ct) => await PageAsync(db, query, ct);

    /// <summary>An entry with its arguments, result and changes: with <i>MCP: View</i>, or one's own.</summary>
    [HttpGet("log/{id:long}")]
    public async Task<McpLogDetail> Entry(long id, CancellationToken ct)
    {
        var e = await db.McpLog.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
        if (e is null || (!User.Has(Area.Mcp, Level.View) && e.UserId != User.UserId()))
        {
            throw ApiException.NotFound("MCP log entry");
        }
        var audit = e.CorrelationId.Length == 0 ? []
            : await db.AuditLog.AsNoTracking().Where(l => l.CorrelationId == e.CorrelationId).OrderBy(l => l.At).ToListAsync(ct);
        // The History of the call: what its writes changed, row by row (the item's History shows the same with the source MCP).
        var rows = e.CorrelationId.Length == 0 ? []
            : await db.ChangeLog.AsNoTracking().Where(c => c.CorrelationId == e.CorrelationId).OrderBy(c => c.Id).Take(200)
                .Select(c => new
                {
                    c.Id, c.At, c.UserId, UserName = db.Users.Where(u => u.Id == c.UserId).Select(u => u.DisplayName).FirstOrDefault(), c.Source, c.Entity,
                    c.EntityId, c.Action, c.Changes, c.ClientChangedAt,
                })
                .ToListAsync(ct);
        var history = rows.Select(c => new HistoryEntryItem(c.Id, c.At, c.UserId, c.UserName, c.Source.ToString().ToLowerInvariant(), c.Entity, c.EntityId,
            c.Action, JsonNode.Parse(c.Changes), c.ClientChangedAt)).ToList();
        return new McpLogDetail(ItemOf(e), e.CorrelationId, e.UserAgent, e.Arguments, e.Result is null ? null : McpLog.Unpack(e.Result), e.Truncated, e.Error,
            e.Targets, [.. audit.Select(AuditController.ToItem)], history);
    }

    [HttpGet("tokens")]
    [RequireRight(Area.Mcp, Level.View)]
    public async Task<IReadOnlyList<McpAdminTokenItem>> Tokens(CancellationToken ct)
    {
        var tokens = await db.McpTokens.AsNoTracking().ToListAsync(ct);
        var users = await McpTokenService.UsersAsync(db, tokens.Select(t => t.UserId).ToList(), ct);
        var o = options.CurrentValue;
        return [.. tokens.Where(t => users.ContainsKey(t.UserId)).Select(t => new McpAdminTokenItem(t.Id, t.UserId, users[t.UserId].Username,
                users[t.UserId].DisplayName, AccountMcpController.Item(t, users[t.UserId], o, new Dictionary<Area, Level>())))
            .OrderByDescending(t => t.Token.BlockedAt is not null).ThenByDescending(t => t.Token.On).ThenBy(t => t.Username).ThenBy(t => t.Token.Kind)];
    }

    /// <summary>Switches someone's token off, with a reason they read (3 to 300 characters).</summary>
    [HttpPost("tokens/{id:guid}/off")]
    [RequireRight(Area.Mcp, Level.Manage)]
    [Audit(AuditEvents.McpTokenOffByAdmin)]
    public async Task<McpAdminTokenItem> TurnOff(Guid id, McpTurnOffRequest request, [FromServices] McpTokenService tokens, CancellationToken ct)
    {
        var (token, user) = await tokens.TurnOffByAdminAsync(id, request.Reason, ct);
        return new McpAdminTokenItem(token.Id, user.Id, user.Username, user.DisplayName,
            AccountMcpController.Item(token, user, options.CurrentValue, new Dictionary<Area, Level>()));
    }

    /// <summary><i>Revoke all tokens</i>: every value stops working; everyone makes a new one by switching theirs on.</summary>
    [HttpPost("tokens/revoke-all")]
    [RequireRight(Area.Mcp, Level.Manage)]
    [Audit(AuditEvents.McpTokensRevoked)]
    public async Task<McpRevokeAllResult> RevokeAll(McpRevokeAllRequest request, [FromServices] McpTokenService tokens, CancellationToken ct) =>
        new(await tokens.RevokeAllAsync(request.Reason, ct));

    internal static McpLogItem ItemOf(McpLogEntity e) => new(e.Id, e.At, e.TokenId, e.Kind, e.UserId, e.Username, e.Ip, e.Client, e.Method, e.Tool, e.ToolKind,
        e.Outcome, e.ErrorCode, e.Summary, e.Strike, e.DurationMs);

    internal static async Task<Paged<McpLogItem>> PageAsync(PgpmDbContext db, McpLogQuery query, CancellationToken ct)
    {
        var q = db.McpLog.AsNoTracking();
        if (query.UserId is { } user)
        {
            q = q.Where(l => l.UserId == user);
        }
        if (query.TokenId is { } token)
        {
            q = q.Where(l => l.TokenId == token);
        }
        if (query.Kind is { } kind)
        {
            q = q.Where(l => l.Kind == kind);
        }
        if (!string.IsNullOrWhiteSpace(query.Tool))
        {
            q = q.Where(l => l.Tool == query.Tool);
        }
        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            q = q.Where(l => l.Outcome == query.Outcome);
        }
        if (query.From is { } from)
        {
            var f = Clock.AsUtc(from);
            q = q.Where(l => l.At >= f);
        }
        if (query.To is { } to)
        {
            var t = Clock.AsUtc(to);
            q = q.Where(l => l.At < t);
        }
        if (SearchText.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(l => EF.Functions.ILike(l.Summary, pattern) || EF.Functions.ILike(l.Username, pattern) || (l.Tool != null && EF.Functions.ILike(l.Tool, pattern)));
        }
        var page = await Paging.PageAsync(q.OrderByDescending(l => l.Id), query, ct);
        return new Paged<McpLogItem>([.. page.Items.Select(ItemOf)], page.Page, page.PageSize, page.TotalCount);
    }
}

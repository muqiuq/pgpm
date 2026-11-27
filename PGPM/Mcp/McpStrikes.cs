using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Mcp;

/// <summary>
/// Calls per token and minute (docs/03 §7.2, PNMTD's McpLimits): <c>Mcp:CallsPerMinute</c> (120) in all,
/// <c>Mcp:WritesPerMinute</c> (20) of them changes. In memory: a restart forgets them, which is fine.
/// </summary>
public sealed class McpLimits(IOptionsMonitor<McpOptions> options)
{
    private readonly ConcurrentDictionary<(Guid Token, bool Write), Queue<DateTime>> windows = new();

    /// <summary>Null when the call may go ahead (and is counted); otherwise how long to wait.</summary>
    public TimeSpan? Take(Guid token, bool write)
    {
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        var all = windows.GetOrAdd((token, false), _ => new Queue<DateTime>());
        var writes = windows.GetOrAdd((token, true), _ => new Queue<DateTime>());
        lock (all)
        {
            lock (writes)
            {
                Prune(all, now);
                Prune(writes, now);
                if (all.Count >= Math.Max(1, o.CallsPerMinute))
                {
                    return all.Peek().AddMinutes(1) - now;
                }
                if (write && writes.Count >= Math.Max(1, o.WritesPerMinute))
                {
                    return writes.Peek().AddMinutes(1) - now;
                }
                all.Enqueue(now);
                if (write)
                {
                    writes.Enqueue(now);
                }
                return null;
            }
        }
    }

    private static void Prune(Queue<DateTime> window, DateTime now)
    {
        while (window.Count > 0 && window.Peek() <= now.AddMinutes(-1))
        {
            window.Dequeue();
        }
    }
}

/// <summary>
/// Strikes (docs/03 §7.1, PNMTD's McpStrikes): a call to a tool the token isn't offered shows intent.
/// <c>Mcp:StrikesToBlock</c> (3) within 24 hours switch the token off and block it, with an audit entry for the admins
/// (<c>mcp.token_blocked</c>, on Administration → Audit log and MCP). The user can switch it on again.
/// </summary>
public sealed class McpStrikes(IDbContextFactory<PgpmDbContext> dbs, IOptionsMonitor<McpOptions> options)
{
    /// <summary>Counts a strike; true when it blocked the token.</summary>
    public async Task<bool> RecordAsync(Guid tokenId, string correlationId)
    {
        await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
        var token = await db.McpTokens.SingleOrDefaultAsync(t => t.Id == tokenId, CancellationToken.None);
        if (token is null)
        {
            return false;
        }
        var now = Clock.UtcNow;
        token.Strikes = [.. token.Strikes.Where(s => s > now.AddHours(-24)), now];
        var blocked = false;
        if (token.BlockedAt is null && token.Strikes.Count >= Math.Max(1, options.CurrentValue.StrikesToBlock))
        {
            blocked = true;
            McpTokenService.End(token, McpTokenEnds.Strikes, now);
            token.BlockedAt = now;
            var user = await db.Users.AsNoTracking().Where(u => u.Id == token.UserId).Select(u => u.Username).SingleAsync(CancellationToken.None);
            var since = now.AddHours(-24);
            var refused = await db.McpLog.AsNoTracking().Where(l => l.TokenId == tokenId && l.Strike && l.At >= since).OrderBy(l => l.Id).Take(10)
                .Select(l => l.Tool ?? l.Method).ToListAsync(CancellationToken.None);
            db.AuditLog.Add(AuditLog.Entry(AuditEvents.McpTokenBlocked,
                AuditWho.System with { ActorRef = McpPrincipal.Reference(token.Id, token.Kind) },
                $"Blocked after {token.Strikes.Count} refused calls in 24 hours ({string.Join(", ", refused)}). {user} can switch it on again.",
                McpTokenService.Target(token, user), correlationId: correlationId));
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return blocked;
    }
}

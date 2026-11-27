using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Mcp;

/// <summary>
/// Every 5 minutes (docs/03 §7.1, §9, PNMTD's McpTokenExpiryTask): records the end of tokens that went off by themselves –
/// their time, their user's stamp, deactivation, the second factor gone –, so Account → Security and the audit say when.
/// Requests never wait for it: they check the state themselves. Its last run is in <c>key_values</c> (task health).
/// </summary>
public sealed class McpTokenExpiryTask(IDbContextFactory<PgpmDbContext> dbs, IOptionsMonitor<McpOptions> options, ILogger<McpTokenExpiryTask> logger)
    : BackgroundService
{
    public const string HealthKey = "task.mcp_token_expiry";

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(Every);
            do
            {
                try
                {
                    await RunAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "The MCP tokens' expiry failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One run: the number of tokens whose end it recorded.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var now = Clock.UtcNow;
        var tokens = await db.McpTokens.Where(t => t.OnSince != null).ToListAsync(ct);
        var users = await McpTokenService.UsersAsync(db, tokens.Select(t => t.UserId).ToList(), ct);
        var ended = 0;
        foreach (var token in tokens)
        {
            if (!users.TryGetValue(token.UserId, out var user))
            {
                continue;
            }
            var state = McpTokenState.Of(token, user, settings, now);
            if (!state.On && state.End is { } why)
            {
                McpTokenService.End(token, why, state.Until is { } at && at <= now ? at : now);
                db.AuditLog.Add(AuditLog.Entry(AuditEvents.McpTokenExpired, AuditWho.System with { ActorRef = "mcp-expiry" }, $"Off by itself: {Reason(why)}",
                    McpTokenService.Target(token, user.Username)));
                ended++;
            }
        }
        await db.SaveChangesAsync(ct);
        var summary = $"{ended} tokens ended";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO key_values (key, value, updated_at) VALUES ({HealthKey}, {summary}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """, ct);
        return ended;
    }

    private static string Reason(string why) => why switch
    {
        McpTokenEnds.Expired => "its time is over",
        McpTokenEnds.Stamp => "its user's password, 2FA or sessions changed",
        McpTokenEnds.UserInactive => "its user was deactivated",
        McpTokenEnds.TotpOff => "its user has no second factor any more",
        _ => why,
    };
}

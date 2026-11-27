using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Sync;

/// <summary>
/// Drops tombstones of <c>other</c> delete batches (a removed share, a deleted setting; not the Trash's items and maps)
/// older than Sync:TombstoneDays (docs/03 §9, PLM's TombstonePruner), under the counter's lock like every push, and raises
/// the tombstone horizon to the highest seq it removed: a client that was away longer resets.
/// </summary>
public static class TombstonePruner
{
    public const string HealthKey = "task.tombstone_pruner";

    public static async Task<int> RunOnceAsync(PgpmDbContext db, SyncOptions options, CancellationToken ct)
    {
        var cutoff = Clock.UtcNow - TimeSpan.FromDays(options.TombstoneDays);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SyncCounter.LockAsync(db, ct);
        var batches = await db.DeleteBatches.Where(b => b.RootKind == DeleteRootKind.Other && b.DeletedAt < cutoff && b.RestoredAt == null && b.PurgedAt == null)
            .Select(b => b.Id).ToListAsync(ct);
        var removed = 0;
        long horizon = 0;
        if (batches.Count > 0)
        {
            // Children first, as a purge.
            foreach (var table in SyncTables.All.Reverse())
            {
                // The table's name from the model; the ids a parameter.
                var sql = $"DELETE FROM {table.SqlTable(db)} WHERE deleted AND delete_batch_id = ANY(@ids) RETURNING seq AS \"Value\"";
                var seqs = await db.Database.SqlQueryRaw<long>(sql, new NpgsqlParameter("ids", batches.ToArray())).ToListAsync(ct);
                removed += seqs.Count;
                horizon = Math.Max(horizon, seqs.DefaultIfEmpty(0).Max());
            }
            var now = Clock.UtcNow;
            await db.DeleteBatches.Where(b => batches.Contains(b.Id)).ExecuteUpdateAsync(s => s.SetProperty(b => b.PurgedAt, now), ct);
            await SyncCounter.RaiseHorizonAsync(db, horizon, ct);
        }
        await transaction.CommitAsync(ct);
        return removed;
    }
}

/// <summary>
/// Keeps the sync's logs small (docs/03 §9): <c>sync_mutations</c> after 30 days (a client retrying later gets the
/// mutation applied again, which the per-field checks make harmless), <c>sync_evictions</c> after 90 days – with the
/// horizon raised past them, so a client that would miss one resets.
/// </summary>
public static class LogPruner
{
    public const string HealthKey = "task.log_pruner";

    public static readonly TimeSpan MutationsFor = TimeSpan.FromDays(30);

    public static readonly TimeSpan EvictionsFor = TimeSpan.FromDays(90);

    public static async Task<(int Mutations, int Evictions)> RunOnceAsync(PgpmDbContext db, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var mutationsBefore = now - MutationsFor;
        var mutations = await db.SyncMutations.Where(m => m.ProcessedAt < mutationsBefore).ExecuteDeleteAsync(ct);
        var evictionsBefore = now - EvictionsFor;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SyncCounter.LockAsync(db, ct);
        var seqs = await db.Database.SqlQuery<long>($"DELETE FROM sync_evictions WHERE at < {evictionsBefore} RETURNING seq AS \"Value\"").ToListAsync(ct);
        if (seqs.Count > 0)
        {
            await SyncCounter.RaiseHorizonAsync(db, seqs.Max(), ct);
        }
        await transaction.CommitAsync(ct);
        return (mutations, seqs.Count);
    }
}

/// <summary>
/// A background task (docs/03 §9): every <paramref name="interval"/> (first after a minute), each run with a context of its
/// own; errors are logged; the last run's summary goes into <c>key_values</c> (task health).
/// </summary>
public abstract class PeriodicSyncTask(string name, TimeSpan interval, IDbContextFactory<PgpmDbContext> dbs, ILogger logger) : BackgroundService
{
    protected abstract Task<string> RunAsync(PgpmDbContext db, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(interval);
            do
            {
                try
                {
                    await using var db = await dbs.CreateDbContextAsync(stoppingToken);
                    var summary = await RunAsync(db, stoppingToken);
                    var at = Clock.UtcNow;
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO key_values (key, value, updated_at) VALUES ({name}, {summary}, {at})
                        ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
                        """, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "{Task} failed", name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

/// <summary><see cref="TombstonePruner"/> hourly.</summary>
public sealed class TombstonePrunerTask(IDbContextFactory<PgpmDbContext> dbs, IOptions<SyncOptions> options, ILogger<TombstonePrunerTask> logger)
    : PeriodicSyncTask(TombstonePruner.HealthKey, TimeSpan.FromHours(1), dbs, logger)
{
    protected override async Task<string> RunAsync(PgpmDbContext db, CancellationToken ct) =>
        $"{await TombstonePruner.RunOnceAsync(db, options.Value, ct)} tombstones pruned";
}

/// <summary><see cref="LogPruner"/> daily, and the MCP log's newest <c>Mcp:LogEntries</c> (docs/03 §9).</summary>
public sealed class LogPrunerTask(IDbContextFactory<PgpmDbContext> dbs, IOptionsMonitor<Mcp.McpOptions> mcp, ILogger<LogPrunerTask> logger)
    : PeriodicSyncTask(LogPruner.HealthKey, TimeSpan.FromDays(1), dbs, logger)
{
    protected override async Task<string> RunAsync(PgpmDbContext db, CancellationToken ct)
    {
        var (mutations, evictions) = await LogPruner.RunOnceAsync(db, ct);
        var calls = await Mcp.McpLog.TrimAsync(db, mcp.CurrentValue.LogEntries, ct);
        return $"{mutations} sync mutations, {evictions} evictions, {calls} MCP log entries pruned";
    }
}

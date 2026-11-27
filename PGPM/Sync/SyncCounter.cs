using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PGPM.Api;
using PGPM.Data;

namespace PGPM.Sync;

/// <summary>
/// A context's part in the global counter: whether its transaction holds the counter's lock, the last seq it handed out
/// (published when the transaction commits), and the transaction <see cref="SyncStampInterceptor"/> opened itself.
/// </summary>
public sealed class SyncSession
{
    internal DbTransaction? LockedTransaction { get; set; }

    /// <summary>The highest seq handed out in the running transaction, published by <see cref="SyncCommitInterceptor"/>.</summary>
    internal long? PendingHead { get; set; }

    internal IDbContextTransaction? OwnedTransaction { get; set; }

    internal void Reset()
    {
        LockedTransaction = null;
        PendingHead = null;
    }
}

/// <summary>
/// The global change counter (docs/02 §11.1, PLM's SequenceAllocator with one row for everybody): every transaction that
/// writes synced rows locks the row <c>FOR UPDATE</c> before it writes, keeps it until commit, and takes one number per
/// changed row. Writers are serialized, so a seq becomes visible together with its row and in counter order – a pull
/// never moves its cursor past a number still to come. Not a PostgreSQL sequence: that hands out numbers outside commit
/// order. Waiting longer than <see cref="LockTimeout"/> for any lock is 503 <c>busy</c>.
/// </summary>
public static class SyncCounter
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>PostgreSQL's lock_not_available: the lock timeout passed.</summary>
    public const string LockNotAvailable = "55P03";

    public static bool HoldsLock(PgpmDbContext db) =>
        db.Database.CurrentTransaction?.GetDbTransaction() is { } transaction && ReferenceEquals(db.SyncSession.LockedTransaction, transaction);

    /// <summary>Takes the counter's lock in the context's transaction (which must exist), unless it holds it already.</summary>
    public static async Task LockAsync(PgpmDbContext db, CancellationToken ct)
    {
        if (HoldsLock(db))
        {
            return;
        }
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("The sync counter is locked inside a transaction.");
        var timeout = $"{(long)LockTimeout.TotalMilliseconds}ms";
        try
        {
            // set_config(…, true) is SET LOCAL, with the value as a parameter.
            await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {timeout}, true)", ct);
            await db.Database.ExecuteSqlAsync($"SELECT value FROM sync_counter WHERE id = 1 FOR UPDATE", ct);
        }
        catch (PostgresException e) when (e.SqlState == LockNotAvailable)
        {
            throw Busy();
        }
        db.SyncSession.LockedTransaction = transaction;
    }

    /// <summary><see cref="LockAsync"/> for a synchronous SaveChanges.</summary>
    public static void Lock(PgpmDbContext db)
    {
        if (HoldsLock(db))
        {
            return;
        }
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("The sync counter is locked inside a transaction.");
        var timeout = $"{(long)LockTimeout.TotalMilliseconds}ms";
        try
        {
            db.Database.ExecuteSql($"SELECT set_config('lock_timeout', {timeout}, true)");
            db.Database.ExecuteSql($"SELECT value FROM sync_counter WHERE id = 1 FOR UPDATE");
        }
        catch (PostgresException e) when (e.SqlState == LockNotAvailable)
        {
            throw Busy();
        }
        db.SyncSession.LockedTransaction = transaction;
    }

    /// <summary>
    /// <paramref name="count"/> numbers, in the locked transaction: returns the last; the first is <c>last - count + 1</c>.
    /// ToListAsync: EF cannot compose over UPDATE … RETURNING.
    /// </summary>
    public static async Task<long> NextAsync(PgpmDbContext db, int count, CancellationToken ct)
    {
        await LockAsync(db, ct);
        var last = (await db.Database.SqlQuery<long>($"UPDATE sync_counter SET value = value + {count} WHERE id = 1 RETURNING value AS \"Value\"")
            .ToListAsync(ct)).Single();
        db.SyncSession.PendingHead = last;
        return last;
    }

    public static long Next(PgpmDbContext db, int count)
    {
        Lock(db);
        var last = db.Database.SqlQuery<long>($"UPDATE sync_counter SET value = value + {count} WHERE id = 1 RETURNING value AS \"Value\"")
            .ToList().Single();
        db.SyncSession.PendingHead = last;
        return last;
    }

    /// <summary>The last number handed out (and committed, outside the lock): the pull's <c>head</c>.</summary>
    public static async Task<long> HeadAsync(PgpmDbContext db, CancellationToken ct) =>
        await db.SyncCounter.AsNoTracking().Where(c => c.Id == 1).Select(c => c.Value).SingleAsync(ct);

    public static async Task<long> HorizonAsync(PgpmDbContext db, CancellationToken ct) =>
        await db.SyncState.AsNoTracking().Where(s => s.Id == 1).Select(s => s.TombstoneHorizonSeq).SingleAsync(ct);

    /// <summary>Raises the tombstone horizon (never lowers it): cursors below it reset (docs/02 §11.3).</summary>
    public static Task RaiseHorizonAsync(PgpmDbContext db, long seq, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"UPDATE sync_state SET tombstone_horizon_seq = GREATEST(tombstone_horizon_seq, {seq}) WHERE id = 1", ct);

    public static ApiException Busy() =>
        new(StatusCodes.Status503ServiceUnavailable, "busy", "The server is busy with other changes. Try again.");

    /// <summary>A lock wait that ran out (55P03), wrapped or not: 503 busy.</summary>
    public static bool IsLockTimeout(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: LockNotAvailable })
            {
                return true;
            }
        }
        return false;
    }
}

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Sync;

/// <summary>
/// Stamps every synced row a <c>SaveChanges</c> adds or modifies (docs/02 §1.1, docs/03 §11.5 step 3): a new
/// <c>seq</c> from the locked counter, <c>rev</c> 1 or +1, <c>created_*</c> and <c>updated_*</c> from the
/// <see cref="ChangeContext"/>. A save outside a transaction gets one of its own (committed after the save), so the
/// counter's lock lasts until the rows are committed. Bulk writes (<c>ExecuteUpdate</c>) bypass it: the code that uses
/// them for synced rows (re-stamp, purge) takes numbers from <see cref="SyncCounter"/> itself.
/// </summary>
public sealed class SyncStampInterceptor : SaveChangesInterceptor
{
    public static readonly SyncStampInterceptor Instance = new();

    private SyncStampInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is PgpmDbContext db && Changed(db) is { Count: > 0 } entries)
        {
            if (db.Database.CurrentTransaction is null)
            {
                db.SyncSession.OwnedTransaction = db.Database.BeginTransaction();
            }
            Stamp(entries, SyncCounter.Next(db, entries.Count));
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PgpmDbContext db && Changed(db) is { Count: > 0 } entries)
        {
            if (db.Database.CurrentTransaction is null)
            {
                db.SyncSession.OwnedTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
            }
            Stamp(entries, await SyncCounter.NextAsync(db, entries.Count, cancellationToken));
        }
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is PgpmDbContext { SyncSession.OwnedTransaction: { } owned } db)
        {
            db.SyncSession.OwnedTransaction = null;
            owned.Commit();
            owned.Dispose();
        }
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PgpmDbContext { SyncSession.OwnedTransaction: { } owned } db)
        {
            db.SyncSession.OwnedTransaction = null;
            await owned.CommitAsync(cancellationToken);
            await owned.DisposeAsync();
        }
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is PgpmDbContext { SyncSession.OwnedTransaction: { } owned } db)
        {
            db.SyncSession.OwnedTransaction = null;
            owned.Dispose();
            db.SyncSession.Reset();
        }
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PgpmDbContext { SyncSession.OwnedTransaction: { } owned } db)
        {
            db.SyncSession.OwnedTransaction = null;
            await owned.DisposeAsync();
            db.SyncSession.Reset();
        }
    }

    private static List<EntityEntry<ISyncedEntity>> Changed(PgpmDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        return db.ChangeTracker.Entries<ISyncedEntity>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
    }

    private static void Stamp(List<EntityEntry<ISyncedEntity>> entries, long last)
    {
        var context = ChangeContext.Current;
        var now = Clock.UtcNow;
        var seq = last - entries.Count + 1;
        foreach (var entry in entries)
        {
            var row = entry.Entity;
            row.Seq = seq++;
            row.UpdatedAt = now;
            row.UpdatedBy = context.UserId;
            if (entry.State == EntityState.Added)
            {
                row.Rev = 1;
                if (row.CreatedAt == default)
                {
                    row.CreatedAt = now;
                }
                row.CreatedBy ??= context.UserId;
            }
            else
            {
                row.Rev = (int)entry.Property(nameof(ISyncedEntity.Rev)).OriginalValue! + 1;
            }
        }
    }
}

/// <summary>
/// After the commit of a transaction that handed out seqs (a push, an admin change, a seed): <see cref="ChangeFeed.Publish"/>,
/// which wakes the live connections (docs/03 §11.5 step 4). Whatever the source, as no write of a synced row can bypass the
/// counter.
/// </summary>
public sealed class SyncCommitInterceptor(ChangeFeed? feed) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData.Context);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(eventData.Context);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Forget(eventData.Context);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private void Committed(DbContext? context)
    {
        if (context is PgpmDbContext db)
        {
            var head = db.SyncSession.PendingHead;
            db.SyncSession.Reset();
            if (head is { } h)
            {
                feed?.Publish(h);
            }
        }
    }

    private static void Forget(DbContext? context)
    {
        if (context is PgpmDbContext db)
        {
            db.SyncSession.Reset();
        }
    }
}

/// <summary>
/// The History of synced rows (docs/02 §12, docs/03 §3.4), in PNMTD's audit style: before each <c>SaveChanges</c>, the added
/// and modified rows of tables with <see cref="SyncTable.History"/> become <c>change_log</c> entries of the same save –
/// <c>create</c>, <c>update</c> (the changed fields, old → new, by wire name), <c>delete</c>, <c>restore</c>. The rest
/// (<c>share</c>, <c>conflict</c>, <c>conflict_resolved</c>) the services write themselves (<see cref="SyncWrite.Log"/>).
/// </summary>
public sealed class ChangeLogInterceptor : SaveChangesInterceptor
{
    public static readonly ChangeLogInterceptor Instance = new();

    /// <summary>Longer texts are cut (the History shows the start; the row holds the whole).</summary>
    public const int MaxText = 2000;

    private ChangeLogInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Collect(DbContext? context)
    {
        if (context is not PgpmDbContext db)
        {
            return;
        }
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries<ISyncedEntity>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        if (entries.Count == 0)
        {
            return;
        }
        var who = ChangeContext.Current;
        var now = Clock.UtcNow;
        foreach (var entry in entries)
        {
            if (SyncTables.ByType(entry.Entity.GetType()) is not { History: true } table)
            {
                continue;
            }
            var columns = table.Columns(db).Where(c =>
                c.Key != "id" && !SyncedEntity.WireColumns.Contains(c.Key) && !table.Hidden.Contains(c.Key) && !table.NotInHistory.Contains(c.Key));
            string action;
            var changes = new JsonArray();
            if (entry.State == EntityState.Added)
            {
                action = ChangeActions.Create;
                foreach (var column in columns)
                {
                    if (Value(column.Get(entry.Entity), column) is { } value)
                    {
                        changes.Add(new JsonObject { ["field"] = column.Key, ["old"] = null, ["new"] = value });
                    }
                }
            }
            else
            {
                var deleted = entry.Property(nameof(ISyncedEntity.Deleted));
                if (deleted.IsModified && !Equals(deleted.OriginalValue, deleted.CurrentValue))
                {
                    action = entry.Entity.Deleted ? ChangeActions.Delete : ChangeActions.Restore;
                }
                else
                {
                    action = ChangeActions.Update;
                    foreach (var column in columns)
                    {
                        var property = entry.Property(column.PropertyName);
                        if (property.IsModified && !WireJson.Same(Value(property.OriginalValue, column), Value(property.CurrentValue, column)))
                        {
                            changes.Add(new JsonObject
                            {
                                ["field"] = column.Key, ["old"] = Value(property.OriginalValue, column), ["new"] = Value(property.CurrentValue, column),
                            });
                        }
                    }
                    if (changes.Count == 0)
                    {
                        continue;
                    }
                }
            }
            db.ChangeLog.Add(new ChangeLogEntity
            {
                At = now,
                UserId = who.UserId,
                Source = who.Source,
                Entity = table.Name,
                EntityId = entry.Entity.Id,
                ItemId = table.ItemIdOf(db, entry.Entity),
                MapId = table.MapIdOf(db, entry.Entity),
                Action = action,
                Changes = changes.ToJsonString(WireJson.Options),
                ClientChangedAt = who.ClientChangedAt,
                CorrelationId = who.CorrelationId,
            });
        }
    }

    private static JsonNode? Value(object? value, WireColumn column)
    {
        var node = WireJson.ToNode(value, column);
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > MaxText } text)
        {
            return JsonValue.Create(text[..(MaxText - 1)] + "…");
        }
        return node;
    }
}

/// <summary>
/// The in-process signal after every commit of synced rows (docs/03 §11.6): the live connections whose cursor is behind
/// pull. One server instance; scale-out would make this Postgres LISTEN/NOTIFY with the head as payload.
/// </summary>
public sealed class ChangeFeed
{
    private long head;

    public long Head => Interlocked.Read(ref head);

    public event Action<long>? Published;

    public void Publish(long value)
    {
        long seen;
        do
        {
            seen = Interlocked.Read(ref head);
        }
        while (value > seen && Interlocked.CompareExchange(ref head, value, seen) != seen);
        Published?.Invoke(value);
    }
}

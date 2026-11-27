using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sharing;

namespace PGPM.Sync;

/// <summary>
/// The Trash (docs/01 §4.9, docs/03 §11.6), online: the delete batches of items (maps in phase 5) the user may manage,
/// restore through a <see cref="SyncWrite"/> (new seqs, revs + 1, rows whose parent is gone and duplicates of unique pairs
/// stay deleted), and the admin's purge, which removes the rows and raises the tombstone horizon (clients behind it reset).
/// </summary>
public sealed class TrashService(PgpmDbContext db, SyncWriter writer, IHttpContextAccessor accessor)
{
    public async Task<Paged<TrashItem>> ListAsync(Viewer viewer, PageQuery query, CancellationToken ct)
    {
        var me = viewer.UserId;
        var groups = viewer.GroupIds.ToList();
        var admin = viewer.IsAdmin;
        var entries =
            from b in db.DeleteBatches.AsNoTracking()
            where b.RootKind == DeleteRootKind.Item && b.RestoredAt == null && b.PurgedAt == null
            join i in db.Items.AsNoTracking() on b.RootId equals i.Id
            where i.Deleted && i.DeleteBatchId == b.Id
                && (admin || i.OwnerUserId == me
                    || db.ItemShares.Any(s => s.ItemId == i.Id && !s.Deleted && s.Access == ItemAccess.Manage && groups.Contains(s.GroupId)))
            orderby b.DeletedAt descending, b.Id descending
            select new
            {
                b.Id, b.RootId, i.Type, i.Title, b.DeletedAt, b.DeletedBy,
                Name = db.Users.Where(u => u.Id == b.DeletedBy).Select(u => u.DisplayName).FirstOrDefault(),
            };
        var page = await Paging.PageAsync(entries, query, ct);
        return new Paged<TrashItem>(page.Items.Select(t => new TrashItem(t.Id, "item", t.RootId, t.Type.ToString().ToLowerInvariant(), t.Title,
            t.DeletedAt, t.DeletedBy, t.Name)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>Un-deletes the batch's rows, parents first; 404 for a batch that isn't in the trash, 403 without the right to delete its root.</summary>
    public async Task<TrashRestoreResult> RestoreAsync(Viewer viewer, Guid batchId, CancellationToken ct)
    {
        await using var write = await writer.BeginAsync(viewer, ChangeSource.Restore, accessor.HttpContext?.TraceIdentifier, ct);
        using var _ = ChangeContext.Use(write.Context());
        var batch = await db.DeleteBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.RestoredAt == null && b.PurgedAt == null, ct)
            ?? throw ApiException.NotFound("Trash entry");
        var rootTable = SyncTables.Find(batch.RootTable) ?? throw ApiException.NotFound("Trash entry");
        var root = await rootTable.FindAsync(db, batch.RootId, ct) ?? throw ApiException.NotFound("Trash entry");
        try
        {
            await rootTable.AuthorizeDeleteAsync(write, root, ct);
        }
        catch (SyncRejection e)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, e.Code, e.Message);
        }
        var restored = 0;
        var skipped = 0;
        var reshared = new HashSet<Guid>();
        foreach (var table in SyncTables.All)
        {
            foreach (var row in await table.InBatchAsync(db, batchId, ct))
            {
                if (await table.ParentGoneAsync(db, row, ct) || await table.HasLiveDuplicateAsync(db, row, ct))
                {
                    skipped++;
                    continue;
                }
                row.Deleted = false;
                row.DeleteBatchId = null;
                restored++;
                if (row is ItemShareEntity share)
                {
                    reshared.Add(share.ItemId);
                }
            }
            // Saved per table: the children's parent checks see their parents restored.
            await db.SaveChangesAsync(ct);
        }
        // Shares back (a removal undone): whoever got access again needs the item's rows.
        foreach (var itemId in reshared)
        {
            await write.Access.RestampAsync("items", itemId, ct);
        }
        batch.RestoredAt = Clock.UtcNow;
        batch.RestoredBy = viewer.UserId;
        if (root is ItemEntity item)
        {
            AuditLog.Record(db, AuditEvents.TrashRestored, $"{ItemEntity.Prefix(item.Type)}-{item.Number} {item.Title}: {restored} row(s) restored"
                + (skipped > 0 ? $", {skipped} left in the trash" : ""), AuditWho.From(accessor), AuditTarget.Item(item.Id, item.Title));
        }
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return new TrashRestoreResult(batchId, restored, skipped);
    }

    /// <summary>
    /// Deletes the batch's rows for good, with the rows below them that weren't deleted with them (an item's shares), and
    /// raises the tombstone horizon to the highest seq removed: a client whose cursor is below it may have missed a
    /// tombstone and resets. Admins only (the endpoint checks).
    /// </summary>
    public async Task PurgeAsync(Viewer viewer, Guid batchId, CancellationToken ct)
    {
        await using var write = await writer.BeginAsync(viewer, ChangeSource.Admin, accessor.HttpContext?.TraceIdentifier, ct);
        var batch = await db.DeleteBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.RestoredAt == null && b.PurgedAt == null, ct)
            ?? throw ApiException.NotFound("Trash entry");
        var root = await (SyncTables.Find(batch.RootTable)?.FindAsync(db, batch.RootId, ct) ?? Task.FromResult<ISyncedEntity?>(null));
        var removed = new List<ISyncedEntity>();
        foreach (var table in SyncTables.All)
        {
            foreach (var row in await table.InBatchAsync(db, batchId, ct))
            {
                await CollectAsync(table, row, removed, ct);
            }
        }
        var horizon = removed.Count == 0 ? 0 : removed.Max(r => r.Seq);
        foreach (var row in removed.DistinctBy(r => r.Id))
        {
            db.Remove(row);
        }
        batch.PurgedAt = Clock.UtcNow;
        batch.PurgedBy = viewer.UserId;
        var title = root is ItemEntity item ? $"{ItemEntity.Prefix(item.Type)}-{item.Number} {item.Title}" : $"{batch.RootTable} {batch.RootId}";
        AuditLog.Record(db, AuditEvents.TrashPurged, $"{title}: {removed.Count} row(s) deleted permanently", AuditWho.From(accessor),
            root is ItemEntity i ? AuditTarget.Item(i.Id, i.Title) : null);
        await db.SaveChangesAsync(ct);
        await SyncCounter.RaiseHorizonAsync(db, horizon, ct);
        // No seq is spent, but the live connections should look: those behind the horizon reset.
        db.SyncSession.PendingHead = await SyncCounter.HeadAsync(db, ct);
        await write.CommitAsync(ct);
    }

    /// <summary>The row and everything below it, whatever its state (a purged item takes its shares).</summary>
    private async Task CollectAsync(SyncTable table, ISyncedEntity row, List<ISyncedEntity> removed, CancellationToken ct)
    {
        removed.Add(row);
        foreach (var (child, parent) in SyncTables.ChildrenOf(table.Name))
        {
            foreach (var below in await child.ChildrenAsync(db, parent, row.Id, liveOnly: false, ct))
            {
                await CollectAsync(child, below, removed, ct);
            }
        }
    }
}

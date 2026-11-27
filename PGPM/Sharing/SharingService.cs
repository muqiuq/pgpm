using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sync;

namespace PGPM.Sharing;

/// <summary>
/// Sharing and ownership of items (docs/03 §6.3), online: <c>PUT /api/v1/items/{id}/shares</c> (the full list) and
/// <c>POST /api/v1/items/{id}/owner</c>, both with manage access. Each writes the share rows (or the owner) through a
/// <see cref="SyncWrite"/>, re-stamps the item's subtree so every client gets it again with its new <c>access</c> (and
/// users who just got access get it at all), evicts it from users who lost access entirely, and records the change in the
/// History (<c>share</c>) and the audit log.
/// </summary>
public sealed class SharingService(PgpmDbContext db, SyncWriter writer, IHttpContextAccessor accessor)
{
    public async Task<IReadOnlyList<ItemShareItem>> ListAsync(Viewer viewer, Guid itemId, CancellationToken ct)
    {
        await new AccessService(db).RequireItemAsync(viewer, itemId, ItemAccess.Read, ct);
        return await SharesAsync(itemId, ct);
    }

    public async Task<IReadOnlyList<ItemShareItem>> SetSharesAsync(Viewer viewer, Guid itemId, IReadOnlyList<ItemShareRequest>? request,
        CancellationToken ct)
    {
        var wanted = request ?? [];
        var v = new ApiValidator()
            .Check(wanted.All(s => s.GroupId is { } g && g != Guid.Empty), "groupId", "A group for every share.")
            .Check(wanted.All(s => s.Access is ItemAccess.Read or ItemAccess.Edit or ItemAccess.Manage), "access", "Read, Edit or Manage.")
            .Check(wanted.Select(s => s.GroupId).Distinct().Count() == wanted.Count, "groupId", "Each group once.");
        v.ThrowIfInvalid();
        await using var write = await writer.BeginAsync(viewer, Sync.ChangeSource.Ui, accessor.HttpContext?.TraceIdentifier, ct);
        using var _ = ChangeContext.Use(write.Context());
        var item = await ItemAsync(write, viewer, itemId, ct);
        var groupIds = wanted.Select(s => s.GroupId!.Value).ToList();
        var known = await db.Groups.Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
        new ApiValidator().Check(known.Count == groupIds.Count, "groupId", "Unknown group.").ThrowIfInvalid();

        var before = await write.Access.UsersWithItemAccessAsync(itemId, ct);
        var current = await db.ItemShares.Where(s => s.ItemId == itemId && !s.Deleted).ToListAsync(ct);
        var changes = new JsonArray();
        DeleteBatchEntity? batch = null;
        foreach (var share in current)
        {
            var target = wanted.FirstOrDefault(w => w.GroupId == share.GroupId);
            if (target is null)
            {
                batch ??= write.NewBatch(DeleteRootKind.Other, "items", itemId);
                share.Deleted = true;
                share.DeleteBatchId = batch.Id;
                changes.Add(ShareChange(share.GroupId, share.Access, null));
            }
            else if (target.Access != share.Access)
            {
                changes.Add(ShareChange(share.GroupId, share.Access, target.Access));
                share.Access = target.Access!.Value;
            }
        }
        foreach (var share in wanted.Where(w => current.All(c => c.GroupId != w.GroupId)))
        {
            db.ItemShares.Add(new ItemShareEntity { Id = Ids.New(), ItemId = itemId, GroupId = share.GroupId!.Value, Access = share.Access!.Value });
            changes.Add(ShareChange(share.GroupId!.Value, null, share.Access));
        }
        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await AfterAccessChangeAsync(write, itemId, before, ct);
            write.Log(SyncTables.Get("items"), itemId, itemId, ChangeActions.Share, changes);
            AuditLog.Record(db, AuditEvents.ItemSharesChanged, $"{Key(item)} {item.Title}: {await DescribeAsync(changes, ct)}", AuditWho.From(accessor),
                AuditTarget.Item(item.Id, item.Title));
            await db.SaveChangesAsync(ct);
        }
        await write.CommitAsync(ct);
        return await SharesAsync(itemId, ct);
    }

    /// <summary>The owner becomes another active user; the old owner is evicted when no share gives them access.</summary>
    public async Task TransferOwnerAsync(Viewer viewer, Guid itemId, Guid? userId, CancellationToken ct)
    {
        await using var write = await writer.BeginAsync(viewer, Sync.ChangeSource.Ui, accessor.HttpContext?.TraceIdentifier, ct);
        using var _ = ChangeContext.Use(write.Context());
        var item = await ItemAsync(write, viewer, itemId, ct);
        var owner = userId is { } id ? await db.Users.AsNoTracking().Where(u => u.Id == id && u.IsActive).Select(u => new { u.Id, u.Username })
            .FirstOrDefaultAsync(ct) : null;
        new ApiValidator().Check(owner is not null, "userId", "An active user.").ThrowIfInvalid();
        if (item.OwnerUserId == owner!.Id)
        {
            return;
        }
        var before = await write.Access.UsersWithItemAccessAsync(itemId, ct);
        var previous = await db.Users.AsNoTracking().Where(u => u.Id == item.OwnerUserId).Select(u => u.Username).FirstOrDefaultAsync(ct);
        item.OwnerUserId = owner.Id;
        await db.SaveChangesAsync(ct);
        await AfterAccessChangeAsync(write, itemId, before, ct);
        AuditLog.Record(db, AuditEvents.ItemOwnerChanged, $"{Key(item)} {item.Title}: owner {previous ?? "(deleted user)"} → {owner.Username}",
            AuditWho.From(accessor), AuditTarget.Item(item.Id, item.Title));
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
    }

    /// <summary>A live item the user manages, tracked; 404 otherwise (403 without manage access).</summary>
    private async Task<ItemEntity> ItemAsync(SyncWrite write, Viewer viewer, Guid itemId, CancellationToken ct)
    {
        await write.Access.RequireItemAsync(viewer, itemId, ItemAccess.Manage, ct);
        var item = await db.Items.FirstAsync(i => i.Id == itemId, ct);
        if (item.Deleted)
        {
            throw ApiException.Conflict("item_deleted", "The item is in the trash: restore it first.");
        }
        return item;
    }

    /// <summary>The re-stamp of the item's subtree and the evictions of users who could read it and can't any more.</summary>
    private static async Task AfterAccessChangeAsync(SyncWrite write, Guid itemId, HashSet<Guid> before, CancellationToken ct)
    {
        await write.Access.RestampAsync("items", itemId, ct);
        var after = await write.Access.UsersWithItemAccessAsync(itemId, ct);
        await write.Access.EvictAsync([.. before.Except(after)], DeleteRootKind.Item, itemId, ct);
    }

    private async Task<IReadOnlyList<ItemShareItem>> SharesAsync(Guid itemId, CancellationToken ct) =>
        (await db.ItemShares.AsNoTracking().Where(s => s.ItemId == itemId && !s.Deleted)
            .Join(db.Groups, s => s.GroupId, g => g.Id, (s, g) => new { s.Id, s.GroupId, g.Name, s.Access })
            .OrderBy(s => s.Name).ToListAsync(ct)).Select(s => new ItemShareItem(s.Id, s.GroupId, s.Name, s.Access)).ToList();

    /// <summary>A <c>share</c> entry's change: <c>{"field": "share", "groupId", "old": "read" | null, "new": "edit" | null}</c>.</summary>
    public static JsonObject ShareChange(Guid groupId, ItemAccess? old, ItemAccess? @new) => new()
    {
        ["field"] = "share",
        ["groupId"] = groupId.ToString(),
        ["old"] = old?.ToString().ToLowerInvariant(),
        ["new"] = @new?.ToString().ToLowerInvariant(),
    };

    private async Task<string> DescribeAsync(JsonArray changes, CancellationToken ct)
    {
        var ids = changes.Select(c => Guid.Parse(c!["groupId"]!.GetValue<string>())).ToList();
        var names = await db.Groups.IgnoreQueryFilters().AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return string.Join(", ", changes.Select(c =>
        {
            var name = names.GetValueOrDefault(Guid.Parse(c!["groupId"]!.GetValue<string>()), "?");
            return $"{name} {c["old"]?.GetValue<string>() ?? "–"} → {c["new"]?.GetValue<string>() ?? "–"}";
        }));
    }

    private static string Key(ItemEntity item) => $"{ItemEntity.Prefix(item.Type)}-{item.Number}";
}

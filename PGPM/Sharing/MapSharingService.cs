using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Maps;
using PGPM.Sync;

namespace PGPM.Sharing;

/// <summary>
/// Sharing and ownership of maps (docs/03 §6.3), as <see cref="SharingService"/> does them for items:
/// <c>PUT /api/v1/maps/{id}/shares</c> (the full list) and <c>POST /api/v1/maps/{id}/owner</c>, both with manage access; the
/// map's subtree re-stamped, evictions for users who lost access entirely, a <c>share</c> change-log entry and the audit log.
/// </summary>
public sealed class MapSharingService(PgpmDbContext db, SyncWriter writer, IHttpContextAccessor accessor)
{
    public async Task<IReadOnlyList<ItemShareItem>> SetSharesAsync(Viewer viewer, Guid mapId, IReadOnlyList<ItemShareRequest>? request, CancellationToken ct)
    {
        var wanted = request ?? [];
        new ApiValidator()
            .Check(wanted.All(s => s.GroupId is { } g && g != Guid.Empty), "groupId", "A group for every share.")
            .Check(wanted.All(s => s.Access is ItemAccess.Read or ItemAccess.Edit or ItemAccess.Manage), "access", "Read, Edit or Manage.")
            .Check(wanted.Select(s => s.GroupId).Distinct().Count() == wanted.Count, "groupId", "Each group once.")
            .ThrowIfInvalid();
        await using var write = await writer.BeginAsync(viewer, ChangeSource.Ui, accessor.HttpContext?.TraceIdentifier, ct);
        using var _ = ChangeContext.Use(write.Context());
        var map = await MapAsync(write, viewer, mapId, ct);
        var groupIds = wanted.Select(s => s.GroupId!.Value).ToList();
        var known = await db.Groups.Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
        new ApiValidator().Check(known.Count == groupIds.Count, "groupId", "Unknown group.").ThrowIfInvalid();

        var before = await write.Access.UsersWithMapAccessAsync(mapId, ct);
        var current = await db.MapShares.Where(s => s.MapId == mapId && !s.Deleted).ToListAsync(ct);
        var changes = new JsonArray();
        DeleteBatchEntity? batch = null;
        foreach (var share in current)
        {
            var target = wanted.FirstOrDefault(w => w.GroupId == share.GroupId);
            if (target is null)
            {
                batch ??= write.NewBatch(DeleteRootKind.Other, "maps", mapId);
                share.Deleted = true;
                share.DeleteBatchId = batch.Id;
                changes.Add(SharingService.ShareChange(share.GroupId, share.Access, null));
            }
            else if (target.Access != share.Access)
            {
                changes.Add(SharingService.ShareChange(share.GroupId, share.Access, target.Access));
                share.Access = target.Access!.Value;
            }
        }
        foreach (var share in wanted.Where(w => current.All(c => c.GroupId != w.GroupId)))
        {
            db.MapShares.Add(new MapShareEntity { Id = Ids.New(), MapId = mapId, GroupId = share.GroupId!.Value, Access = share.Access!.Value });
            changes.Add(SharingService.ShareChange(share.GroupId!.Value, null, share.Access));
        }
        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await AfterAccessChangeAsync(write, mapId, before, ct);
            write.Log(SyncTables.Get("maps"), mapId, null, ChangeActions.Share, changes, mapId);
            AuditLog.Record(db, AuditEvents.MapSharesChanged, $"Map {map.Name}: {changes.Count} share(s) changed", AuditWho.From(accessor),
                AuditTarget.Map(map.Id, map.Name));
            await db.SaveChangesAsync(ct);
        }
        await write.CommitAsync(ct);
        return await SharesAsync(mapId, ct);
    }

    public async Task TransferOwnerAsync(Viewer viewer, Guid mapId, Guid? userId, CancellationToken ct)
    {
        await using var write = await writer.BeginAsync(viewer, ChangeSource.Ui, accessor.HttpContext?.TraceIdentifier, ct);
        using var _ = ChangeContext.Use(write.Context());
        var map = await MapAsync(write, viewer, mapId, ct);
        var owner = userId is { } id ? await db.Users.AsNoTracking().Where(u => u.Id == id && u.IsActive).Select(u => new { u.Id, u.Username })
            .FirstOrDefaultAsync(ct) : null;
        new ApiValidator().Check(owner is not null, "userId", "An active user.").ThrowIfInvalid();
        if (map.OwnerUserId == owner!.Id)
        {
            return;
        }
        var before = await write.Access.UsersWithMapAccessAsync(mapId, ct);
        var previous = await db.Users.AsNoTracking().Where(u => u.Id == map.OwnerUserId).Select(u => u.Username).FirstOrDefaultAsync(ct);
        map.OwnerUserId = owner.Id;
        await db.SaveChangesAsync(ct);
        await AfterAccessChangeAsync(write, mapId, before, ct);
        AuditLog.Record(db, AuditEvents.MapOwnerChanged, $"Map {map.Name}: owner {previous ?? "(deleted user)"} → {owner.Username}",
            AuditWho.From(accessor), AuditTarget.Map(map.Id, map.Name));
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
    }

    private async Task<MapEntity> MapAsync(SyncWrite write, Viewer viewer, Guid mapId, CancellationToken ct)
    {
        await write.Access.RequireMapAsync(viewer, mapId, ItemAccess.Manage, ct);
        var map = await db.Maps.FirstAsync(m => m.Id == mapId, ct);
        if (map.Deleted)
        {
            throw ApiException.Conflict("map_deleted", "The map is in the trash: restore it first.");
        }
        return map;
    }

    private static async Task AfterAccessChangeAsync(SyncWrite write, Guid mapId, HashSet<Guid> before, CancellationToken ct)
    {
        await write.Access.RestampAsync("maps", mapId, ct);
        var after = await write.Access.UsersWithMapAccessAsync(mapId, ct);
        await write.Access.EvictAsync([.. before.Except(after)], DeleteRootKind.Map, mapId, ct);
    }

    private async Task<IReadOnlyList<ItemShareItem>> SharesAsync(Guid mapId, CancellationToken ct) =>
        (await db.MapShares.AsNoTracking().Where(s => s.MapId == mapId && !s.Deleted)
            .Join(db.Groups, s => s.GroupId, g => g.Id, (s, g) => new { s.Id, s.GroupId, g.Name, s.Access })
            .OrderBy(s => s.Name).ToListAsync(ct)).Select(s => new ItemShareItem(s.Id, s.GroupId, s.Name, s.Access)).ToList();
}

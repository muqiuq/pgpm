using Microsoft.EntityFrameworkCore;
using Npgsql;
using PGPM.Api;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sync;

namespace PGPM.Sharing;

/// <summary>
/// Who reads and writes: the user, the admin flag and the rights of the session, and the user's groups (from
/// <see cref="SessionCache"/>, so a membership change applies at the next request). <see cref="MaxAccess"/> caps the
/// access to every item: an MCP token's (docs/03 §7.3) – read for the read token, edit for the write token.
/// </summary>
public sealed record Viewer(Guid UserId, bool IsAdmin, IReadOnlyList<Guid> GroupIds, UserRights Rights)
{
    public ItemAccess MaxAccess { get; init; } = ItemAccess.Manage;

    /// <summary>
    /// The request's user; 401 when signed out. An MCP token's principal never carries the admin flag (its rights are
    /// capped), but sees what its user sees – an admin's everything –, capped at its kind's access.
    /// </summary>
    public static async Task<Viewer> OfAsync(HttpContext http, CancellationToken ct)
    {
        var id = http.User.UserId() ?? throw ApiException.Unauthorized();
        var services = http.RequestServices;
        var entry = await services.GetRequiredService<SessionCache>().GetAsync(id,
            () => SessionCache.LoadAsync(services.GetRequiredService<PgpmDbContext>(), id, ct));
        var rights = http.User.Rights();
        if (Mcp.McpPrincipal.KindOf(http.User) is { } kind)
        {
            return new Viewer(id, entry?.Rights.IsAdmin == true, entry?.GroupIds ?? [], rights) { MaxAccess = Mcp.McpRights.AccessCap(kind) };
        }
        return new Viewer(id, rights.IsAdmin, entry?.GroupIds ?? [], rights);
    }
}

/// <summary>
/// Object sharing (docs/03 §6.3, docs/02 §4): access of a user to an item (admins and the owner manage, everybody else the
/// highest level of their groups' shares), the predicates the pull filters by, and what follows a change of access – the
/// re-stamp of a subtree (new seqs, same revs, so every client receives it again with its computed <c>access</c>),
/// evictions for users who lost access, and <c>access_epoch</c> bumps (a reset of the user's clients).
/// </summary>
public sealed class AccessService(PgpmDbContext db)
{
    /// <summary>Items the user may read, tombstones included (a deleted item keeps its shares).</summary>
    public IQueryable<ItemEntity> ReadableItems(Viewer viewer) => Readable(db.Items, viewer);

    /// <summary>
    /// <paramref name="items"/> filtered to those the user may read (docs/03 §6.3): <c>is_admin OR owner_user_id = @me OR
    /// EXISTS (a live share with one of the user's groups)</c>.
    /// </summary>
    public IQueryable<ItemEntity> Readable(IQueryable<ItemEntity> items, Viewer viewer)
    {
        if (viewer.IsAdmin)
        {
            return items;
        }
        var me = viewer.UserId;
        var groups = viewer.GroupIds.ToList();
        return items.Where(i => i.OwnerUserId == me || db.ItemShares.Any(s => s.ItemId == i.Id && !s.Deleted && groups.Contains(s.GroupId)));
    }

    /// <summary>The user's access to an item; <see cref="ItemAccess.None"/> for one that doesn't exist.</summary>
    public async Task<ItemAccess> ItemAccessAsync(Viewer viewer, Guid itemId, CancellationToken ct) =>
        (await ItemAccessAsync(viewer, [itemId], ct)).GetValueOrDefault(itemId, ItemAccess.None);

    public async Task<Dictionary<Guid, ItemAccess>> ItemAccessAsync(Viewer viewer, IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        var owners = await db.Items.IgnoreQueryFilters().AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.OwnerUserId })
            .ToListAsync(ct);
        var groups = viewer.GroupIds.ToList();
        var shares = viewer.IsAdmin || groups.Count == 0 ? []
            : await db.ItemShares.AsNoTracking().Where(s => ids.Contains(s.ItemId) && !s.Deleted && groups.Contains(s.GroupId))
                .Select(s => new { s.ItemId, s.Access }).ToListAsync(ct);
        var byItem = shares.ToLookup(s => s.ItemId, s => s.Access);
        return owners.ToDictionary(o => o.Id, o =>
        {
            var access = viewer.IsAdmin || o.OwnerUserId == viewer.UserId ? ItemAccess.Manage : byItem[o.Id].DefaultIfEmpty(ItemAccess.None).Max();
            return access > viewer.MaxAccess ? viewer.MaxAccess : access;
        });
    }

    /// <summary>403 <c>missing_access</c> below <paramref name="needed"/>; 404 for an item that doesn't exist.</summary>
    public async Task RequireItemAsync(Viewer viewer, Guid itemId, ItemAccess needed, CancellationToken ct)
    {
        if (!await db.Items.IgnoreQueryFilters().AnyAsync(i => i.Id == itemId, ct))
        {
            throw ApiException.NotFound("Item");
        }
        if (await ItemAccessAsync(viewer, itemId, ct) < needed)
        {
            throw MissingAccess(needed);
        }
    }

    public static ApiException MissingAccess(ItemAccess needed) =>
        new(StatusCodes.Status403Forbidden, SyncCodes.MissingAccess, $"You need {needed.ToString().ToLowerInvariant()} access.",
            extensions: new Dictionary<string, object?> { ["level"] = needed.ToString().ToLowerInvariant() });

    /// <summary>Every user who may read the item now (admins, the owner, the members of its shares' groups).</summary>
    public async Task<HashSet<Guid>> UsersWithItemAccessAsync(Guid itemId, CancellationToken ct)
    {
        var owner = await db.Items.IgnoreQueryFilters().Where(i => i.Id == itemId).Select(i => (Guid?)i.OwnerUserId).FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            return [];
        }
        var groups = db.ItemShares.Where(s => s.ItemId == itemId && !s.Deleted).Select(s => s.GroupId);
        return [.. await db.Users.Where(u => u.IsAdmin || u.Id == owner || db.UserGroups.Any(m => m.UserId == u.Id && groups.Contains(m.GroupId)))
            .Select(u => u.Id).ToListAsync(ct)];
    }

    /// <summary>
    /// Re-stamps an item and every live row below it (docs/02 §11.5): new seqs from the counter, in the locked transaction,
    /// revs unchanged. A client replaces rows of an equal rev, so it takes the new computed <c>access</c>; a user who just got
    /// access receives the whole subtree.
    /// </summary>
    public async Task RestampAsync(string table, Guid rootId, CancellationToken ct)
    {
        var levels = new List<(SyncTable Table, List<Guid> Ids)> { (SyncTables.Get(table), [rootId]) };
        for (var i = 0; i < levels.Count; i++)
        {
            var (parent, parentIds) = levels[i];
            foreach (var (child, link) in SyncTables.ChildrenOf(parent.Name))
            {
                // Table and column names from the model; the values are parameters.
                var sql = $"SELECT id AS \"Value\" FROM {child.SqlTable(db)} WHERE {child.Column(db, link.ForeignKey)!.ColumnName} = ANY(@ids) "
                    + "AND NOT deleted ORDER BY seq";
                var ids = await db.Database.SqlQueryRaw<Guid>(sql, new NpgsqlParameter("ids", parentIds.ToArray())).ToListAsync(ct);
                if (ids.Count > 0)
                {
                    levels.Add((child, ids));
                }
            }
        }
        var total = levels.Sum(l => l.Ids.Count);
        var next = await SyncCounter.NextAsync(db, total, ct) - total + 1;
        foreach (var (rows, ids) in levels)
        {
            var seqs = Enumerable.Range(0, ids.Count).Select(n => next + n).ToArray();
            next += ids.Count;
            var sql = $"UPDATE {rows.SqlTable(db)} AS t SET seq = d.seq FROM unnest(@ids, @seqs) AS d(id, seq) WHERE t.id = d.id";
            await db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("ids", ids.ToArray()), new NpgsqlParameter("seqs", seqs)], ct);
        }
    }

    /// <summary>Evictions (docs/02 §11.4) for users who lost access to a root, each with a seq of its own. Saved by the caller.</summary>
    public async Task EvictAsync(IReadOnlyCollection<Guid> userIds, DeleteRootKind kind, Guid rootId, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return;
        }
        var seq = await SyncCounter.NextAsync(db, userIds.Count, ct) - userIds.Count + 1;
        var now = Clock.UtcNow;
        foreach (var user in userIds)
        {
            db.SyncEvictions.Add(new SyncEvictionEntity { UserId = user, Seq = seq++, RootKind = kind, RootId = rootId, At = now });
        }
    }

    /// <summary>
    /// A structural change of what the users may see (group membership, the admin flag): their clients reset (docs/02
    /// §11.5). The caller forgets their sessions afterwards, which tells their live connections.
    /// </summary>
    public static async Task BumpAccessEpochAsync(PgpmDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return;
        }
        var ids = userIds.Distinct().ToList();
        await db.Users.Where(u => ids.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessEpoch, u => u.AccessEpoch + 1), ct);
    }
}

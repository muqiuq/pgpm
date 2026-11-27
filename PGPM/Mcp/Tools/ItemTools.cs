using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sync;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// The item tools (docs/03 §7.3), generic over <see cref="ItemKinds"/>: <c>search_items</c>, <c>get_item</c>,
/// <c>create_{kind}</c> for every type the sync accepts, <c>update_item</c>. Reading follows the token's access (sharing,
/// capped at read or edit); every change is a mutation through <see cref="SyncWriter"/> with the source <c>mcp</c>.
/// </summary>
public static class ItemTools
{
    private const string ItemArg = "The item: its id, or its key (N-12)";

    public static IReadOnlyList<ToolSpec> All =>
    [
        new("search_items", "Search items",
            "Items you may see, newest change first (pinned notes first): by type, text in the title or description, owner, a group they are shared " +
            "with" + (ItemKinds.AnyStatus ? ", status" : "") + ". Results carry the id, key, title, owner, your access and a link.",
            Schema([
                Choice("type", "Only items of this type", ItemKinds.Names),
                Text("text", "Words in the title or the description"),
                Text("owner", "\"me\", or a user's id (search_persons gives userId)"),
                Text("sharedWith", "A group's id or name (list_config has the groups)"),
                .. ItemKinds.AnyStatus ? [Id("status", "A status's id (list_config)", required: false)] : Array.Empty<Field>(),
                .. PageArgs,
            ]),
            null, Level.None, ToolKind.Read, SearchAsync),
        new("get_item", "Get an item",
            "One item with its text, owner, who changed it when, its shares and your access. Pass rev back as expectedRev to update_item.",
            Schema([Text("id", ItemArg, required: true)]), null, Level.None, ToolKind.Read, GetAsync),
        .. ItemKinds.All.Where(k => k.Creatable).Select(Create),
        new("update_item", "Update an item",
            "Changes the given fields of an item you may edit; the others stay. With expectedRev (the rev you read) a change made meanwhile is " +
            "refused with changed_meanwhile and the current values, so you can read again and decide.",
            Schema([
                Text("id", ItemArg, required: true),
                .. ItemKinds.Updatable.Select(f => f.Schema),
                Int("expectedRev", "The rev you read (get_item, search_items)"),
            ]),
            null, Level.None, ToolKind.Write, UpdateAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
    ];

    private static JsonObject Schema(IReadOnlyList<Field> fields) => ToolArgs.Schema([.. fields]);

    /// <summary><c>create_{kind}</c>: the kind's fields and <c>share_with</c>; needs the type's <i>Create</i>.</summary>
    public static ToolSpec Create(ItemKind kind) => new($"create_{kind.Name}", $"Create a {kind.Name}",
        $"{kind.Description} You become its owner. share_with gives groups read or edit access (list_config has the groups); without it only " +
        "you and the admins see it.",
        Schema([
            ItemKinds.Title.Schema with { Required = true },
            .. ItemKinds.Common.Skip(1).Concat(kind.Fields).Select(f => f.Schema),
            Objects("share_with", "Groups to share it with, at most 20",
                Text("group", "A group's id or name", required: true), Choice("access", "read or edit; edit when left out", ["read", "edit"])),
        ]),
        kind.Area, Level.Create, ToolKind.Write, (args, ctx, db, ct) => CreateAsync(kind, args, ctx, db, ct))
    {
        AuditEvents = [AuditEvents.McpItemCreated],
    };

    private static async Task<object> SearchAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var paging = ToolPaging.From(args);
        var kind = OptionalChoice(args, "type", ItemKinds.Names) is { } name ? ItemKinds.Find(name) : null;
        var types = kind is null ? ItemKinds.All.Select(k => k.Type).ToList() : [kind.Type];
        var items = ctx.Access(db).Readable(db.Items.AsNoTracking(), ctx.Viewer).Where(i => !i.Deleted && types.Contains(i.Type));
        if (SearchText.Pattern(OptionalText(args, "text", 200)) is { } pattern)
        {
            items = items.Where(i => EF.Functions.ILike(i.Title, pattern) || (i.Description != null && EF.Functions.ILike(i.Description, pattern)));
        }
        if (OptionalText(args, "owner", 64) is { Length: > 0 } owner)
        {
            var ownerId = owner.Equals("me", StringComparison.OrdinalIgnoreCase) ? ctx.Viewer.UserId
                : Guid.TryParse(owner, out var o) ? o : throw ToolException.Invalid("owner is \"me\" or a user's id.");
            items = items.Where(i => i.OwnerUserId == ownerId);
        }
        if (OptionalText(args, "sharedWith", 200) is { Length: > 0 } group)
        {
            var groupId = await GroupIdAsync(db, group, ct);
            items = items.Where(i => db.ItemShares.Any(s => s.ItemId == i.Id && !s.Deleted && s.GroupId == groupId));
        }
        if (ItemKinds.AnyStatus && OptionalId(args, "status") is { } status)
        {
            items = items.Where(i => i.StatusId == status);
        }
        var page = await paging.ReadAsync(items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.UpdatedAt).ThenBy(i => i.Id), ct);
        var views = await ItemViews.SummariesAsync(page.Items, ctx, db, ct);
        ctx.Summary = $"{page.Total} items" + (kind is null ? "" : $" ({kind.Name}s)");
        return new { items = views, nextCursor = page.NextCursor, total = page.Total };
    }

    private static async Task<object> GetAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var item = await ItemViews.FindAsync(args, ctx, db, ct);
        ctx.Summary = $"{ItemViews.KeyOf(item)} {item.Title}";
        ctx.Target("item", item.Id, item.Title);
        return await ItemViews.DetailAsync(item, ctx, db, ct);
    }

    private static async Task<object> CreateAsync(ItemKind kind, JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var row = new JsonObject
        {
            ["type"] = kind.Type.ToString().ToLowerInvariant(),
            ["title"] = ItemKinds.Title.Parse(args, "title"),
            ["tags"] = new JsonArray(),
        };
        foreach (var field in ItemKinds.Common.Skip(1).Concat(kind.Fields).Where(f => Has(args, f.Arg)))
        {
            row[field.WireKey] = field.Parse(args, field.Arg);
        }
        var shares = new List<(Guid Group, string Access)>();
        foreach (var share in ObjectsOf(args, "share_with", 20))
        {
            var group = await GroupIdAsync(db, OptionalText(share, "group", 200) ?? throw ToolException.Invalid("share_with: each entry names a group."), ct);
            var access = OptionalChoice(share, "access", ["read", "edit"]) ?? "edit";
            if (shares.All(s => s.Group != group))
            {
                shares.Add((group, access));
            }
        }
        var id = Ids.New();
        var batch = Ids.New();
        await ctx.WriteAsync(db, async write =>
        {
            await ToolContext.ApplyAsync(write, new SyncMutationIn { Batch = batch, Table = "items", Id = id, Op = SyncOp.Insert, Row = row }, ct);
            foreach (var (group, access) in shares)
            {
                await ToolContext.ApplyAsync(write, new SyncMutationIn
                {
                    Batch = batch, Table = "item_shares", Id = Ids.New(), Op = SyncOp.Insert,
                    Row = new JsonObject { ["itemId"] = id.ToString(), ["groupId"] = group.ToString(), ["access"] = access },
                }, ct);
            }
            return true;
        }, ct);
        var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == id, ct);
        var key = ItemViews.KeyOf(item);
        ctx.Summary = $"created {key} {item.Title}" + (shares.Count == 0 ? "" : $", shared with {shares.Count} groups");
        ctx.Target("item", item.Id, item.Title);
        AuditLog.Target(AuditTarget.Item(item.Id, item.Title));
        AuditLog.Summary($"{key} {item.Title}" + (shares.Count == 0 ? "" : $", shared with {shares.Count} groups"));
        return await ItemViews.DetailAsync(item, ctx, db, ct);
    }

    private static async Task<object> UpdateAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var expected = OptionalInt(args, "expectedRev");
        var given = ItemKinds.Updatable.Where(f => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(f.Arg, out _)).ToList();
        if (given.Count == 0)
        {
            throw ToolException.Invalid($"Nothing to change: give one of {string.Join(", ", ItemKinds.Updatable.Select(f => f.Arg))}.");
        }
        var found = await ItemViews.FindAsync(args, ctx, db, ct);
        var kind = ItemKinds.Of(found.Type)!;
        var foreign = given.Where(f => !ItemKinds.Common.Contains(f) && !kind.Fields.Contains(f)).Select(f => f.Arg).ToList();
        if (foreign.Count > 0)
        {
            throw ToolException.Invalid($"A {kind.Name} has no {string.Join(", ", foreign)}.");
        }
        var applied = await ctx.WriteAsync(db, async write =>
        {
            // Read again inside the write unit (the counter's lock): these are the values the patch says it saw.
            var item = await write.Db.Items.AsNoTracking().SingleAsync(i => i.Id == found.Id, ct);
            if (await write.Access.ItemAccessAsync(write.Viewer, item.Id, ct) < ItemAccess.Edit)
            {
                throw new ToolException(SyncCodes.MissingAccess, $"You need edit access to {ItemViews.KeyOf(item)}.");
            }
            if (item.Deleted)
            {
                throw ToolException.NotFound("item");
            }
            if (expected is { } rev && rev != item.Rev)
            {
                throw new ToolException("changed_meanwhile",
                    $"{ItemViews.KeyOf(item)} changed since rev {rev}: it is at rev {item.Rev} now. Read it again and decide.",
                    new Dictionary<string, object?> { ["current"] = await ItemViews.DetailAsync(item, ctx, write.Db, ct) });
            }
            var wire = SyncTables.Get("items").ToWire(write.Db, item);
            var changes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var field in given)
            {
                var value = Has(args, field.Arg) ? field.Parse(args, field.Arg) : null;
                if (field == ItemKinds.Title && value is null)
                {
                    throw ToolException.Invalid("title can't be removed.");
                }
                changes[field.WireKey] = new JsonObject { ["old"] = wire[field.WireKey]?.DeepClone(), ["new"] = value };
            }
            await ToolContext.ApplyAsync(write, new SyncMutationIn
            {
                Batch = Ids.New(), Table = "items", Id = item.Id, Op = SyncOp.Patch, BaseRev = item.Rev, Changes = changes,
            }, ct);
            return changes.Keys.ToList();
        }, ct);
        var updated = await db.Items.AsNoTracking().SingleAsync(i => i.Id == found.Id, ct);
        var key = ItemViews.KeyOf(updated);
        ctx.Summary = $"updated {key} {updated.Title}: {string.Join(", ", applied)}";
        ctx.Target("item", updated.Id, updated.Title);
        AuditLog.Target(AuditTarget.Item(updated.Id, updated.Title));
        AuditLog.Summary($"{key} {updated.Title}: {string.Join(", ", applied)}");
        var detail = await ItemViews.DetailAsync(updated, ctx, db, ct);
        detail["changed"] = new JsonArray([.. applied.Select(k => (JsonNode)k)]);
        return detail;
    }

    /// <summary>A group by id or name (not case-sensitive).</summary>
    public static async Task<Guid> GroupIdAsync(PgpmDbContext db, string group, CancellationToken ct)
    {
        if (Guid.TryParse(group, out var id) && await db.Groups.AnyAsync(g => g.Id == id, ct))
        {
            return id;
        }
        var normalized = Auth.GroupEntity.Normalize(group);
        return await db.Groups.Where(g => g.NormalizedName == normalized).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct)
            ?? throw ToolException.NotFound($"group {group}");
    }
}

/// <summary>
/// What a tool says about an item: a summary in lists, and the detail of <c>get_item</c> – the summary, the text, who created
/// and changed it, and the <see cref="ItemDetails.Parts"/> (its shares now; phase 3 adds persons, links and attachments,
/// phase 4 fields, scores, milestones and gates).
/// </summary>
public static class ItemViews
{
    public static string KeyOf(ItemEntity item) => ItemKinds.Of(item.Type)?.Key(item.Number) ?? $"{ItemEntity.Prefix(item.Type)}-{item.Number}";

    /// <summary>The item of the <c>id</c> argument (an id, or a key such as N-12), if the token may see it.</summary>
    public static async Task<ItemEntity> FindAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var value = TextOf(args, "id", 1, 64);
        var items = ctx.Access(db).Readable(db.Items.AsNoTracking(), ctx.Viewer).Where(i => !i.Deleted);
        ItemEntity? item;
        if (Guid.TryParse(value, out var id))
        {
            item = await items.FirstOrDefaultAsync(i => i.Id == id, ct);
        }
        else if (ItemKinds.KeyPattern().Match(value) is { Success: true } match
                 && ItemKinds.All.FirstOrDefault(k => string.Equals(ItemEntity.Prefix(k.Type), match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is { } kind)
        {
            var number = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            item = await items.FirstOrDefaultAsync(i => i.Type == kind.Type && i.Number == number, ct);
        }
        else
        {
            throw ToolException.Invalid("id is an item's id, or its key (N-12).");
        }
        return item is not null && ItemKinds.Of(item.Type) is not null ? item : throw ToolException.NotFound($"item {value}");
    }

    public static async Task<List<JsonObject>> SummariesAsync(IReadOnlyList<ItemEntity> items, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var access = await ctx.Access(db).ItemAccessAsync(ctx.Viewer, items.Select(i => i.Id).ToList(), ct);
        var names = await NamesAsync(db, items.Select(i => (Guid?)i.OwnerUserId), ct);
        return [.. items.Select(i => Summary(i, access.GetValueOrDefault(i.Id), names, ctx, db))];
    }

    private static JsonObject Summary(ItemEntity item, ItemAccess access, IReadOnlyDictionary<Guid, string> names, ToolContext ctx, PgpmDbContext db)
    {
        var kind = ItemKinds.Of(item.Type)!;
        var wire = SyncTables.Get("items").ToWire(db, item);
        var json = new JsonObject
        {
            ["id"] = item.Id.ToString(),
            ["key"] = KeyOf(item),
            ["type"] = kind.Name,
            ["title"] = item.Title,
            ["color"] = item.Color,
        };
        foreach (var field in kind.Fields)
        {
            json[field.Arg] = wire[field.WireKey]?.DeepClone();
        }
        json["owner"] = new JsonObject { ["userId"] = item.OwnerUserId.ToString(), ["name"] = names.GetValueOrDefault(item.OwnerUserId) };
        json["updatedAt"] = item.UpdatedAt;
        json["rev"] = item.Rev;
        json["access"] = access.ToString().ToLowerInvariant();
        json["url"] = ctx.Link($"{kind.Page}/{item.Id}");
        return json;
    }

    public static async Task<JsonObject> DetailAsync(ItemEntity item, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var access = await ctx.Access(db).ItemAccessAsync(ctx.Viewer, item.Id, ct);
        var names = await NamesAsync(db, [item.OwnerUserId, item.CreatedBy, item.UpdatedBy], ct);
        var json = Summary(item, access, names, ctx, db);
        json["description"] = item.Description;
        json["createdAt"] = item.CreatedAt;
        json["createdBy"] = item.CreatedBy is { } c ? names.GetValueOrDefault(c) : null;
        json["updatedBy"] = item.UpdatedBy is { } u ? names.GetValueOrDefault(u) : null;
        foreach (var part in ItemDetails.Parts)
        {
            json[part.Key] = await part.Read(item, ctx, db, ct);
        }
        return json;
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(PgpmDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
}

/// <summary>A part of <c>get_item</c>'s answer: its key and how it is read for an item the token may see.</summary>
public sealed record ItemDetailPart(string Key, Func<ItemEntity, ToolContext, PgpmDbContext, CancellationToken, Task<JsonNode?>> Read);

/// <summary>
/// The parts of an item's detail beyond its own columns. Phase 3 adds <c>persons</c> (assignments with roles), <c>links</c>
/// and <c>attachments</c>; phase 4 <c>fields</c> (with scores, as the client computes them), <c>milestones</c> and gates.
/// </summary>
public static class ItemDetails
{
    public static readonly ItemDetailPart Shares = new("shares", async (item, ctx, db, ct) =>
    {
        var shares = await db.ItemShares.AsNoTracking().Where(s => s.ItemId == item.Id && !s.Deleted)
            .Join(db.Groups, s => s.GroupId, g => g.Id, (s, g) => new { s.GroupId, g.Name, s.Access }).OrderBy(s => s.Name).ToListAsync(ct);
        return new JsonArray([.. shares.Select(s => (JsonNode)new JsonObject
        {
            ["groupId"] = s.GroupId.ToString(), ["group"] = s.Name, ["access"] = s.Access.ToString().ToLowerInvariant(),
        })]);
    });

    public static readonly IReadOnlyList<ItemDetailPart> Parts = [Shares];
}

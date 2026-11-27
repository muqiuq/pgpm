using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sync;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// The item tools (docs/03 §7.3), generic over <see cref="ItemKinds"/>: <c>search_items</c>, <c>get_item</c>,
/// <c>create_{kind}</c> for every type the sync accepts, <c>update_item</c>. Reading follows the token's access (sharing,
/// capped at read or edit) and section visibility; every change is a mutation through <see cref="SyncWriter"/> with the
/// source <c>mcp</c>.
/// </summary>
public static class ItemTools
{
    public const string ItemArg = "The item: its id, or its key (I-12)";

    /// <summary>At most this many items are weighed by a field range or a quadrant (they are computed, not queried).</summary>
    public const int MaxComputed = 5000;

    private static readonly Field FieldsArg = Map("fields",
        "Ideas and projects: field values by the field's key (or label) from list_config – a rating 0…max or \"na\", a select's option " +
        "(label or id), a number, a date yyyy-MM-dd, a text, true/false, persons (ids or e-mails); null removes a value. Scores are computed.");

    public static IReadOnlyList<ToolSpec> All =>
    [
        new("search_items", "Search items",
            "Items you may see, newest change first (pinned notes first): by type, text in the title or description, status, owner, an " +
            "assigned person, a group they are shared with, field or score ranges (fields: [{field, min, max}]), or the quadrant of a matrix " +
            "map (map + quadrant). Results carry the id, key, title, status, owner, your access and a link.",
            Schema([
                Choice("type", "Only items of this type", ItemKinds.Names),
                Text("text", "Words in the title or the description"),
                Text("status", "A status's id or name (list_config)"),
                Text("owner", "\"me\", or a user's id (search_persons gives userId)"),
                Text("person", "Items a person is assigned to: a person's id or e-mail, or \"me\""),
                Text("sharedWith", "A group's id or name (list_config has the groups)"),
                Objects("fields", "Field or score ranges, all must hold: a number, rating, select (its points) or score at least min and at most max",
                    Text("field", "A field's key or label", required: true), Number("min", "At least"), Number("max", "At most")),
                Id("map", "A matrix map (list_maps): only the items it shows, with quadrant", required: false),
                Choice("quadrant", "With map: the quadrant – tr, br, tl, bl (top right … bottom left)", ["tl", "tr", "bl", "br"]),
                .. PageArgs,
            ]),
            null, Level.None, ToolKind.Read, SearchAsync),
        new("get_item", "Get an item",
            "One item with its text, status, owner, who changed it when, its shares and your access; its assigned persons, links and " +
            "attachments; for ideas and projects its sections with fields, values and computed scores; for projects its milestones and gates " +
            "with their DoD criteria and decisions. Pass rev back as expectedRev to update_item.",
            Schema([Text("id", ItemArg, required: true)]), null, Level.None, ToolKind.Read, GetAsync),
        .. ItemKinds.All.Where(k => k.Creatable).Select(Create),
        new("update_item", "Update an item",
            "Changes the given fields of an item you may edit; the others stay: its own fields, status (id or name; a project's must be allowed " +
            "by its template) and, for ideas and projects, field values in sections you may edit. With expectedRev (the rev you read) a change " +
            "made meanwhile is refused with changed_meanwhile and the current values, so you can read again and decide.",
            Schema([
                Text("id", ItemArg, required: true),
                .. ItemKinds.Updatable.Select(f => f.Schema),
                Text("status", "Ideas, leads, projects: a status's id or name (list_config)"),
                FieldsArg,
                Int("expectedRev", "The rev you read (get_item, search_items)"),
            ]),
            null, Level.None, ToolKind.Write, UpdateAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
    ];

    private static JsonObject Schema(IReadOnlyList<Field> fields) => ToolArgs.Schema([.. fields]);

    /// <summary><c>create_{kind}</c>: the kind's fields, status, field values, a project's template, and <c>share_with</c>; needs the type's <i>Create</i>.</summary>
    public static ToolSpec Create(ItemKind kind) => new($"create_{kind.Name}", $"Create a{(kind.Name[0] is 'i' ? "n" : "")} {kind.Name}",
        $"{kind.Description} You become its owner. share_with gives groups read or edit access (list_config has the groups); without it only " +
        "you and the admins see it" + (kind.Type == ItemType.Project ? " – or, from a template, the template's default shares" : "") + "." +
        (ItemKinds.HasSections(kind.Type) ? " It gets its sections and their default values as in the browser." : "") +
        (kind.Type == ItemType.Project
            ? " template applies a project template: its allowed and default status, sections, field defaults, and milestones and gates due " +
              "startDate (default today) + their offsets, with their DoD criteria."
            : ""),
        Schema([
            ItemKinds.Title.Schema with { Required = true },
            .. ItemKinds.Common.Skip(1).Concat(kind.Fields).Select(f => f.Schema),
            .. kind.HasStatus ? [Text("status", "A status's id or name (list_config); the default when left out")] : Array.Empty<Field>(),
            .. kind.Type == ItemType.Project ? [Text("template", "A project template's id or name (list_config)")] : Array.Empty<Field>(),
            .. ItemKinds.HasSections(kind.Type) ? [FieldsArg] : Array.Empty<Field>(),
            Objects("share_with", "Groups to share it with, at most 20",
                Text("group", "A group's id or name", required: true), Choice("access", "read or edit; edit when left out", ["read", "edit"])),
        ]),
        kind.Area, Level.Create, ToolKind.Write, (args, ctx, db, ct) => CreateAsync(kind, args, ctx, db, ct))
    {
        AuditEvents = [AuditEvents.McpItemCreated],
    };

    // --- search -------------------------------------------------------------------------------------------------------

    private static async Task<object> SearchAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var paging = ToolPaging.From(args);
        var config = await ctx.ConfigAsync(db, ct);
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
        if (OptionalText(args, "status", 200) is { Length: > 0 } status)
        {
            var ids = config.Statuses.Where(s => types.Contains(s.ItemType)
                && (Guid.TryParse(status, out var sid) ? s.Id == sid : string.Equals(s.Name, status.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Select(s => s.Id).ToList();
            if (ids.Count == 0)
            {
                throw ToolException.Invalid($"No status {status}{(kind is null ? "" : $" of {kind.Name}s")} (list_config has them).");
            }
            items = items.Where(i => i.StatusId != null && ids.Contains(i.StatusId.Value));
        }
        if (OptionalText(args, "person", 200) is { Length: > 0 } person)
        {
            var personId = await ToolConfig.PersonIdAsync(db, ctx, person, ct);
            items = items.Where(i => db.Assignments.Any(a => a.PersonId == personId && !a.Deleted
                && (a.ItemId == i.Id || db.Milestones.Any(m => m.Id == a.MilestoneId && m.ItemId == i.Id && !m.Deleted))));
        }
        var ranges = ObjectsOf(args, "fields", 20).Select(r =>
        {
            var field = config.ResolveField(TextOf(r, "field", 1, 200), kind?.Type);
            var (min, max) = (OptionalNumber(r, "min"), OptionalNumber(r, "max"));
            return min is null && max is null ? throw ToolException.Invalid($"fields: {field.Key} needs min or max.") : (Field: field, Min: min, Max: max);
        }).ToList();
        MatrixView? matrix = null;
        if (OptionalId(args, "map") is { } mapId)
        {
            matrix = await MapTools.MatrixAsync(ctx, db, mapId, ct);
            var candidates = matrix.Candidates.ToList();
            items = items.Where(i => candidates.Contains(i.Id));
        }
        var quadrant = OptionalChoice(args, "quadrant", ["tl", "tr", "bl", "br"]);
        if (quadrant is not null && matrix is null)
        {
            throw ToolException.Invalid("quadrant needs map, a matrix map's id.");
        }
        var ordered = items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.UpdatedAt).ThenBy(i => i.Id);
        ToolPage<ItemEntity> page;
        Dictionary<Guid, Dictionary<string, JsonElement>>? values = null;
        if (ranges.Count == 0 && matrix is null)
        {
            page = await paging.ReadAsync(ordered, ct);
        }
        else
        {
            // Field values and scores are computed as the devices compute them: the candidates are weighed one by one.
            var all = await ordered.Take(MaxComputed + 1).ToListAsync(ct);
            if (all.Count > MaxComputed)
            {
                throw ToolException.Invalid($"More than {MaxComputed:N0} items to weigh: narrow the search (type, status, text).");
            }
            values = await config.ValuesAsync(db, all.Select(i => i.Id).ToList(), ct);
            var kept = all.Where(i => ranges.All(r => config.Numeric(r.Field, values[i.Id]) is { } n && (r.Min is not { } lo || n >= lo)
                && (r.Max is not { } hi || n <= hi))
                && (matrix is null || quadrant is null || matrix.Place(i, values[i.Id])?.Quadrant == quadrant)).ToList();
            var more = kept.Count > paging.Offset + paging.Limit;
            page = new ToolPage<ItemEntity>([.. kept.Skip(paging.Offset).Take(paging.Limit)], more ? ToolPaging.Cursor(paging.Offset + paging.Limit) : null,
                kept.Count);
        }
        var views = await ItemViews.SummariesAsync(page.Items, ctx, db, ct);
        if (values is not null)
        {
            foreach (var (item, view) in page.Items.Zip(views))
            {
                if (ranges.Count > 0)
                {
                    view["values"] = new JsonObject(ranges.DistinctBy(r => r.Field.Key).Select(r => KeyValuePair.Create(r.Field.Key,
                        config.Numeric(r.Field, values[item.Id]) is { } n ? (JsonNode?)JsonValue.Create(n) : null)));
                }
                if (matrix?.Place(item, values[item.Id]) is { } place)
                {
                    view["quadrant"] = place.Quadrant;
                    view["quadrantLabel"] = place.Label;
                }
            }
        }
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

    // --- create -------------------------------------------------------------------------------------------------------

    private static async Task<object> CreateAsync(ItemKind kind, JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var config = await ctx.ConfigAsync(db, ct);
        var row = new JsonObject
        {
            ["type"] = ToolConfig.EnumText(kind.Type),
            ["title"] = ItemKinds.Title.Parse(args, "title"),
            ["tags"] = new JsonArray(),
        };
        foreach (var field in ItemKinds.Common.Skip(1).Concat(kind.Fields).Where(f => Has(args, f.Arg)))
        {
            row[field.WireKey] = field.Parse(args, field.Arg);
        }
        var template = kind.Type == ItemType.Project && OptionalText(args, "template", 200) is { Length: > 0 } t
            ? await ItemSetup.TemplateAsync(db, config, t, ct)
            : null;
        Guid? statusId = kind.HasStatus && OptionalText(args, "status", 200) is { Length: > 0 } s ? config.ResolveStatus(kind.Type, s).Id
            : kind.Type == ItemType.Project ? ItemSetup.ProjectStatus(config, template)
            : null;
        if (statusId is { } status)
        {
            row["statusId"] = status.ToString();
        }
        DateOnly? start = null;
        if (kind.Type == ItemType.Project)
        {
            row["projectTemplateId"] = template?.Template.Id.ToString();
            start = OptionalDate(args, "startDate") ?? (template is null ? null : Today());
            row["projectStartDate"] = start?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        var fields = await ItemSetup.FieldsArgAsync(args, ctx, db, kind.Type, ct);
        var shares = await SharesAsync(args, db, ct);
        if (!Has(args, "share_with") && template is not null)
        {
            shares = await ItemSetup.DefaultSharesAsync(db, template, ct);
        }
        var id = Ids.New();
        var batch = Ids.New();
        var setup = await ctx.WriteAsync(db, async write =>
        {
            await ToolWrites.InsertAsync(write, "items", row, batch, ct, id);
            await InsertSharesAsync(write, id, shares, batch, ct);
            return await ItemSetup.SetUpAsync(write, config, id, kind.Type, template, start, ItemSetup.Given(fields, null), batch, ct);
        }, ct);
        var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == id, ct);
        var key = ItemViews.KeyOf(item);
        var made = new List<string>();
        if (shares.Count > 0)
        {
            made.Add($"shared with {shares.Count} groups");
        }
        if (template is not null)
        {
            made.Add($"from the template {template.Template.Name}");
        }
        if (setup.Milestones > 0)
        {
            made.Add($"{setup.Milestones} milestones");
        }
        var summary = $"{key} {item.Title}" + (made.Count == 0 ? "" : ", " + string.Join(", ", made));
        ctx.Summary = $"created {summary}";
        ctx.Target("item", item.Id, item.Title);
        AuditLog.Target(AuditTarget.Item(item.Id, item.Title));
        AuditLog.Summary(summary);
        return await ItemViews.DetailAsync(item, ctx, db, ct);
    }

    public static DateOnly Today() => DateOnly.FromDateTime(Clock.ToLocal(Clock.UtcNow));

    /// <summary>The <c>share_with</c> argument: groups by id or name, read or edit (edit when left out), each once.</summary>
    public static async Task<List<(Guid Group, string Access)>> SharesAsync(JsonElement args, PgpmDbContext db, CancellationToken ct)
    {
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
        return shares;
    }

    /// <summary>A new item's initial shares, in its batch (the shares' same-batch rule).</summary>
    public static async Task InsertSharesAsync(SyncWrite write, Guid itemId, IEnumerable<(Guid Group, string Access)> shares, Guid batch, CancellationToken ct)
    {
        foreach (var (group, access) in shares)
        {
            await ToolWrites.InsertAsync(write, "item_shares", new JsonObject
            {
                ["itemId"] = itemId.ToString(), ["groupId"] = group.ToString(), ["access"] = access,
            }, batch, ct);
        }
    }

    // --- update -------------------------------------------------------------------------------------------------------

    private static async Task<object> UpdateAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var expected = OptionalInt(args, "expectedRev");
        var given = ItemKinds.Updatable.Where(f => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(f.Arg, out _)).ToList();
        var statusArg = OptionalText(args, "status", 200);
        if (given.Count == 0 && statusArg is null && !Has(args, "fields"))
        {
            throw ToolException.Invalid(
                $"Nothing to change: give one of {string.Join(", ", ItemKinds.Updatable.Select(f => f.Arg))}, status or fields.");
        }
        var found = await ItemViews.FindAsync(args, ctx, db, ct);
        var kind = ItemKinds.Of(found.Type)!;
        var foreign = given.Where(f => !ItemKinds.Common.Contains(f) && !kind.Fields.Contains(f)).Select(f => f.Arg).ToList();
        if (statusArg is not null && !kind.HasStatus)
        {
            foreign.Add("status");
        }
        if (foreign.Count > 0)
        {
            throw ToolException.Invalid($"A {kind.Name} has no {string.Join(", ", foreign)}.");
        }
        var config = await ctx.ConfigAsync(db, ct);
        var status = statusArg is { Length: > 0 } ? config.ResolveStatus(found.Type, statusArg) : null;
        var fieldValues = await ItemSetup.FieldsArgAsync(args, ctx, db, found.Type, ct);
        var applied = await ctx.WriteAsync(db, async write =>
        {
            // Read again inside the write unit (the counter's lock): these are the values the patch says it saw.
            var item = await RequireEditableAsync(write, found.Id, ct);
            if (expected is { } rev && rev != item.Rev)
            {
                throw new ToolException("changed_meanwhile",
                    $"{ItemViews.KeyOf(item)} changed since rev {rev}: it is at rev {item.Rev} now. Read it again and decide.",
                    new Dictionary<string, object?> { ["current"] = await ItemViews.DetailAsync(item, ctx, write.Db, ct) });
            }
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var field in given)
            {
                var value = Has(args, field.Arg) ? field.Parse(args, field.Arg) : null;
                if (field == ItemKinds.Title && value is null)
                {
                    throw ToolException.Invalid("title can't be removed.");
                }
                values[field.WireKey] = value;
            }
            if (status is not null)
            {
                values["statusId"] = status.Id.ToString();
            }
            var changed = (await ToolWrites.PatchAsync(write, "items", item, values, ct)).Select(k => k == "statusId" ? "status" : k).ToList();
            changed.AddRange(await ItemSetup.ApplyFieldsAsync(write, config, item.Id, fieldValues, ct));
            return changed;
        }, ct);
        var updated = await db.Items.AsNoTracking().SingleAsync(i => i.Id == found.Id, ct);
        var key = ItemViews.KeyOf(updated);
        var what = applied.Count == 0 ? "nothing changed" : string.Join(", ", applied);
        ctx.Summary = $"updated {key} {updated.Title}: {what}";
        ctx.Target("item", updated.Id, updated.Title);
        AuditLog.Target(AuditTarget.Item(updated.Id, updated.Title));
        AuditLog.Summary($"{key} {updated.Title}: {what}");
        var detail = await ItemViews.DetailAsync(updated, ctx, db, ct);
        detail["changed"] = new JsonArray([.. applied.Select(k => (JsonNode)k)]);
        return detail;
    }

    /// <summary>The live item, read inside the write unit, if the token may edit it: <c>missing_access</c> below edit.</summary>
    public static async Task<ItemEntity> RequireEditableAsync(SyncWrite write, Guid id, CancellationToken ct)
    {
        var item = await write.Db.Items.AsNoTracking().SingleAsync(i => i.Id == id, ct);
        if (await write.Access.ItemAccessAsync(write.Viewer, item.Id, ct) < ItemAccess.Edit)
        {
            throw new ToolException(SyncCodes.MissingAccess, $"You need edit access to {ItemViews.KeyOf(item)}.");
        }
        return item.Deleted ? throw ToolException.NotFound("item") : item;
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
/// and changed it, and the <see cref="ItemDetails.Parts"/>.
/// </summary>
public static class ItemViews
{
    public static string KeyOf(ItemEntity item) => ItemKinds.Of(item.Type)?.Key(item.Number) ?? $"{ItemEntity.Prefix(item.Type)}-{item.Number}";

    /// <summary>The item of the <c>id</c> argument (an id, or a key such as N-12), if the token may see it.</summary>
    public static Task<ItemEntity> FindAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct) =>
        FindAsync(TextOf(args, "id", 1, 64), ctx, db, ct);

    /// <summary>An item by id or key (N-12), if the token may see it.</summary>
    public static async Task<ItemEntity> FindAsync(string value, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        value = value.Trim();
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
            throw ToolException.Invalid("An item is given by its id, or its key (I-12).");
        }
        return item is not null && ItemKinds.Of(item.Type) is not null ? item : throw ToolException.NotFound($"item {value}");
    }

    public static async Task<List<JsonObject>> SummariesAsync(IReadOnlyList<ItemEntity> items, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var access = await ctx.Access(db).ItemAccessAsync(ctx.Viewer, items.Select(i => i.Id).ToList(), ct);
        var names = await NamesAsync(db, items.Select(i => (Guid?)i.OwnerUserId), ct);
        var config = await ctx.ConfigAsync(db, ct);
        var templates = await TemplateNamesAsync(db, items, ct);
        return [.. items.Select(i => Summary(i, access.GetValueOrDefault(i.Id), names, config, templates, ctx, db))];
    }

    private static async Task<Dictionary<Guid, string>> TemplateNamesAsync(PgpmDbContext db, IEnumerable<ItemEntity> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.ProjectTemplateId).OfType<Guid>().Distinct().ToList();
        return ids.Count == 0 ? [] : await db.ProjectTemplates.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
    }

    private static JsonObject Summary(ItemEntity item, ItemAccess access, IReadOnlyDictionary<Guid, string> names, ToolConfig config,
        IReadOnlyDictionary<Guid, string> templates, ToolContext ctx, PgpmDbContext db)
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
        if (kind.HasStatus && config.Status(item.StatusId) is { } status)
        {
            json["status"] = new JsonObject
            {
                ["id"] = status.Id.ToString(), ["name"] = status.Name, ["category"] = ToolConfig.EnumText(status.Category),
            };
        }
        foreach (var field in kind.Fields)
        {
            json[field.Arg] = wire[field.WireKey]?.DeepClone();
        }
        if (item.ProjectTemplateId is { } template)
        {
            json["template"] = new JsonObject { ["id"] = template.ToString(), ["name"] = templates.GetValueOrDefault(template) };
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
        var json = Summary(item, access, names, await ctx.ConfigAsync(db, ct), await TemplateNamesAsync(db, [item], ct), ctx, db);
        json["description"] = item.Description;
        json["createdAt"] = item.CreatedAt;
        json["createdBy"] = item.CreatedBy is { } c ? names.GetValueOrDefault(c) : null;
        json["updatedBy"] = item.UpdatedBy is { } u ? names.GetValueOrDefault(u) : null;
        if (item.OriginItemId is { } origin)
        {
            json["origin"] = await ItemDetails.RefAsync(origin, ctx, db, ct);
        }
        if (item.ConvertedToItemId is { } converted)
        {
            json["convertedTo"] = await ItemDetails.RefAsync(converted, ctx, db, ct);
        }
        foreach (var part in ItemDetails.Parts)
        {
            if (await part.Read(item, ctx, db, ct) is { } value)
            {
                json[part.Key] = value;
            }
        }
        return json;
    }

    public static async Task<Dictionary<Guid, string>> NamesAsync(PgpmDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
}

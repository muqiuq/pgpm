using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Config;
using PGPM.Data;
using PGPM.Files;
using PGPM.Items;
using PGPM.Lib.Helper;

namespace PGPM.Mcp.Tools;

/// <summary>A part of <c>get_item</c>'s answer: its key and how it is read for an item the token may see (null: not for this item).</summary>
public sealed record ItemDetailPart(string Key, Func<ItemEntity, ToolContext, PgpmDbContext, CancellationToken, Task<JsonNode?>> Read);

/// <summary>
/// The parts of an item's detail beyond its own columns (docs/03 §7.3 <c>get_item</c>): shares, assigned persons, links,
/// attachments; for ideas and projects the sections with their fields, values and computed scores – visible sections only –;
/// for projects the milestones and gates with their DoD criteria and decisions.
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

    public static readonly ItemDetailPart Persons = new("persons", async (item, ctx, db, ct) => await AssignedAsync(db, a => a.ItemId == item.Id, ct));

    /// <summary>Links from the item and to it (from items the token reads); an unreadable item is only its id.</summary>
    public static readonly ItemDetailPart Links = new("links", async (item, ctx, db, ct) =>
    {
        var readable = ctx.Access(db).Readable(db.Items.AsNoTracking(), ctx.Viewer);
        var links = await db.ItemLinks.AsNoTracking()
            .Where(l => !l.Deleted && (l.FromItemId == item.Id || (l.ToItemId == item.Id && readable.Any(i => i.Id == l.FromItemId))))
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var refs = await RefsAsync(links.Select(l => l.FromItemId == item.Id ? l.ToItemId : l.FromItemId), ctx, db, ct);
        return new JsonArray([.. links.Select(l => (JsonNode)new JsonObject
        {
            ["id"] = l.Id.ToString(),
            ["direction"] = l.FromItemId == item.Id ? "out" : "in",
            ["relation"] = ToolConfig.EnumText(l.Relation),
            ["item"] = refs[l.FromItemId == item.Id ? l.ToItemId : l.FromItemId].DeepClone(),
        })]);
    });

    public static readonly ItemDetailPart Attachments = new("attachments", async (item, ctx, db, ct) =>
    {
        var files = await db.Files.AsNoTracking().Where(f => f.ItemId == item.Id && !f.Deleted && f.Purpose != FilePurpose.Embedded)
            .OrderBy(f => f.CreatedAt).ToListAsync(ct);
        return new JsonArray([.. files.Select(f => (JsonNode)new JsonObject
        {
            ["id"] = f.Id.ToString(),
            ["fileName"] = f.FileName,
            ["contentType"] = f.ContentType,
            ["sizeBytes"] = f.SizeBytes,
            ["purpose"] = ToolConfig.EnumText(f.Purpose),
            ["cover"] = f.Id == item.ImageFileId,
            ["status"] = ToolConfig.EnumText(f.Status),
        })]);
    });

    /// <summary>
    /// The sections the item shows (and those it has values in), each with its fields: values, a select's label, a score
    /// computed from the values the user sees, a rating's or score's maximum; <c>canEdit</c> per section.
    /// </summary>
    public static readonly ItemDetailPart Sections = new("sections", async (item, ctx, db, ct) =>
    {
        if (!ItemKinds.HasSections(item.Type))
        {
            return null;
        }
        var config = await ctx.ConfigAsync(db, ct);
        var values = (await config.ValuesAsync(db, [item.Id], ct))[item.Id];
        var shown = await db.ItemSections.AsNoTracking().Where(s => s.ItemId == item.Id && !s.Deleted).OrderBy(s => s.SortOrder).ToListAsync(ct);
        var sectionIds = shown.Select(s => s.SectionId).Where(config.Sections.ContainsKey).ToList();
        foreach (var key in values.Keys)
        {
            var section = config.FieldByKey[key].SectionId;
            if (!sectionIds.Contains(section))
            {
                sectionIds.Add(section);
            }
        }
        var persons = await ToolConfig.PersonNamesAsync(db, values.Values.Where(v => v.ValueKind == JsonValueKind.Array)
            .SelectMany(v => v.EnumerateArray()).Where(v => v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out _))
            .Select(v => Guid.Parse(v.GetString()!)), ct);
        var editable = await ctx.Access(db).ItemAccessAsync(ctx.Viewer, item.Id, ct) >= ItemAccess.Edit;
        return new JsonArray([.. sectionIds.Select(id =>
        {
            var section = config.Sections[id];
            return (JsonNode)new JsonObject
            {
                ["id"] = section.Id.ToString(),
                ["name"] = section.Name,
                ["onItem"] = shown.Any(s => s.SectionId == id),
                ["canEdit"] = editable && config.EditableSections.Contains(id),
                ["fields"] = new JsonArray([.. config.Fields.Where(f => f.SectionId == id && (!f.IsArchived || values.ContainsKey(f.Key)))
                    .Select(f => (JsonNode)config.Describe(f, values, persons))]),
            };
        })]);
    });

    public static readonly ItemDetailPart Milestones = new("milestones", async (item, ctx, db, ct) =>
        item.Type == ItemType.Project ? await MilestonesAsync(db, ctx, item.Id, null, ct) : null);

    public static readonly IReadOnlyList<ItemDetailPart> Parts = [Shares, Persons, Links, Attachments, Sections, Milestones];

    /// <summary>A project's milestones and gates (or one of them), in order, with DoD criteria, progress, decision and persons.</summary>
    public static async Task<JsonArray> MilestonesAsync(PgpmDbContext db, ToolContext ctx, Guid projectId, Guid? only, CancellationToken ct)
    {
        var config = await ctx.ConfigAsync(db, ct);
        var milestones = (await db.Milestones.AsNoTracking().Where(m => m.ItemId == projectId && !m.Deleted && (only == null || m.Id == only)).ToListAsync(ct))
            .OrderBy(m => m.SortOrder, StringComparer.Ordinal).ThenBy(m => m.Id).ToList();
        var ids = milestones.Select(m => m.Id).ToList();
        var dod = (await db.DodItems.AsNoTracking().Where(d => ids.Contains(d.MilestoneId) && !d.Deleted).ToListAsync(ct))
            .OrderBy(d => d.SortOrder, StringComparer.Ordinal).ThenBy(d => d.Id).ToLookup(d => d.MilestoneId);
        var deciders = await ItemViews.NamesAsync(db, milestones.Select(m => m.DecidedBy), ct);
        var result = new JsonArray();
        foreach (var m in milestones)
        {
            var criteria = dod[m.Id].ToList();
            var json = new JsonObject
            {
                ["id"] = m.Id.ToString(),
                ["kind"] = ToolConfig.EnumText(m.Kind),
                ["title"] = m.Title,
                ["description"] = m.Description,
                ["dueDate"] = m.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["status"] = ToolConfig.EnumText(m.Status),
                ["completedAt"] = m.CompletedAt,
                ["rev"] = m.Rev,
            };
            if (m.Kind == MilestoneKind.Gate)
            {
                json["decision"] = m.Decision is { } d ? ToolConfig.EnumText(d) : "pending";
                json["decisionNote"] = m.DecisionNote;
                json["decidedAt"] = m.DecidedAt;
                json["decidedBy"] = m.DecidedBy is { } by ? deciders.GetValueOrDefault(by) : null;
                json["onGoStatus"] = config.Status(m.OnGoStatusId)?.Name;
                json["onNoGoStatus"] = config.Status(m.OnNoGoStatusId)?.Name;
            }
            var done = criteria.Count(c => c.State != DodState.Open);
            json["dodProgress"] = $"{done}/{criteria.Count}";
            json["dod"] = new JsonArray([.. criteria.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id.ToString(), ["text"] = c.Text, ["state"] = ToolConfig.EnumText(c.State), ["note"] = c.Note,
            })]);
            json["persons"] = await AssignedAsync(db, a => a.MilestoneId == m.Id, ct);
            result.Add(json);
        }
        return result;
    }

    private static async Task<JsonArray> AssignedAsync(PgpmDbContext db, System.Linq.Expressions.Expression<Func<AssignmentEntity, bool>> which,
        CancellationToken ct)
    {
        var assignments = (await db.Assignments.AsNoTracking().Where(a => !a.Deleted).Where(which)
                .Join(db.Persons, a => a.PersonId, p => p.Id, (a, p) => new { a.Id, a.PersonId, a.Role, a.SortOrder, p.DisplayName, p.Email })
                .ToListAsync(ct))
            .OrderBy(a => a.SortOrder, StringComparer.Ordinal).ThenBy(a => a.Id).ToList();
        return new JsonArray([.. assignments.Select(a => (JsonNode)new JsonObject
        {
            ["assignmentId"] = a.Id.ToString(), ["personId"] = a.PersonId.ToString(), ["name"] = a.DisplayName, ["email"] = a.Email, ["role"] = a.Role,
        })]);
    }

    /// <summary>Items as references: id, key, type, title and link for those the token reads, else only the id and <c>access: none</c>.</summary>
    public static async Task<Dictionary<Guid, JsonObject>> RefsAsync(IEnumerable<Guid> ids, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var items = await ctx.Access(db).Readable(db.Items.AsNoTracking(), ctx.Viewer).Where(i => list.Contains(i.Id) && !i.Deleted).ToListAsync(ct);
        var result = list.ToDictionary(id => id, id => new JsonObject { ["id"] = id.ToString(), ["access"] = "none" });
        foreach (var item in items)
        {
            var kind = ItemKinds.Of(item.Type)!;
            result[item.Id] = new JsonObject
            {
                ["id"] = item.Id.ToString(),
                ["key"] = ItemViews.KeyOf(item),
                ["type"] = kind.Name,
                ["title"] = item.Title,
                ["url"] = ctx.Link($"{kind.Page}/{item.Id}"),
            };
        }
        return result;
    }

    public static async Task<JsonObject> RefAsync(Guid id, ToolContext ctx, PgpmDbContext db, CancellationToken ct) => (await RefsAsync([id], ctx, db, ct))[id];

    /// <summary>The order key after the last of these (none: the first).</summary>
    public static string NextKey(IEnumerable<string> keys) => FractionalKey.After(keys.Where(FractionalKey.IsValid).Order(StringComparer.Ordinal).LastOrDefault());
}

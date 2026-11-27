using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Data;
using PGPM.Files;
using PGPM.Items;
using PGPM.Lib.Helper;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// <c>convert_to_project</c> (docs/03 §7.3, docs/01 §8–9): what the browser's conversion does (PGPM.WebUI/src/local/items.ts
/// <c>convertToProject</c>), in one write unit – a project with the source's title, description and colour, its shares (at most
/// edit), its
/// cover and attachments as new <c>files</c> rows sharing the stored bytes, the sections and values that apply to projects
/// (not those of sections the user may not edit), the template's sections, defaults and milestones, the assignments and
/// outgoing links, a <i>related</i> link back, <c>originItemId</c> (the server marks the source converted) and the source
/// moved to <i>Selected</i> / <i>Won</i> where that status exists. Needs edit on the source and <i>Projects: Create</i>.
/// </summary>
public static class ConvertTools
{
    public static IReadOnlyList<ToolSpec> All =>
    [
        new("convert_to_project", "Convert to a project",
            "Turns an idea or a lead you may edit into a project, as the browser does: the project takes the title, text, colour, shares, cover " +
            "and attachments, the persons and links, and the field values that apply to projects; with template also the template's sections, " +
            "defaults, milestones and gates (due startDate + offsets). The idea moves to Selected, the lead to Won (where those statuses exist) " +
            "and links to the project.",
            ToolArgs.Schema(
                Text("id", "The idea or lead: id or key (I-12, L-3)", required: true),
                Text("title", "The project's title; the source's when left out"),
                Text("template", "A project template's id or name (list_config)"),
                Text("status", "The project's status (id or name); the template's or the default when left out"),
                Text("startDate", "yyyy-MM-dd: the start its milestones count from (default today with a template)")),
            null, Level.None, ToolKind.Action, ConvertAsync)
        {
            Requires = [(Area.Projects, Level.Create)],
            AuditEvents = [AuditEvents.McpItemConverted],
        },
    ];

    private static async Task<object> ConvertAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var source = await ItemViews.FindAsync(args, ctx, db, ct);
        if (source.Type is not (ItemType.Idea or ItemType.Lead))
        {
            throw ToolException.Invalid("Only ideas and leads are converted.");
        }
        if (await ConvertedAsync(db, source, ct) is { } done)
        {
            throw new ToolException("already_converted", $"{ItemViews.KeyOf(source)} was converted already (project: see project).",
                new Dictionary<string, object?> { ["project"] = await ItemDetails.RefAsync(done.Id, ctx, db, ct) });
        }
        var config = await ctx.ConfigAsync(db, ct);
        var template = OptionalText(args, "template", 200) is { Length: > 0 } t ? await ItemSetup.TemplateAsync(db, config, t, ct) : null;
        var statusId = OptionalText(args, "status", 200) is { Length: > 0 } s ? config.ResolveStatus(ItemType.Project, s).Id : ItemSetup.ProjectStatus(config, template);
        if (statusId is null)
        {
            throw ToolException.Invalid("There is no project status yet (Administration → Statuses).");
        }
        var start = OptionalDate(args, "startDate") ?? (template is null ? null : ItemTools.Today());
        var title = OptionalText(args, "title", 300) is { Length: > 0 } given ? given : source.Title;
        var projectId = Ids.New();
        var batch = Ids.New();
        var copied = await ctx.WriteAsync(db, async write =>
        {
            var item = await ItemTools.RequireEditableAsync(write, source.Id, ct);
            var shares = await write.Db.ItemShares.AsNoTracking().Where(x => x.ItemId == item.Id && !x.Deleted).ToListAsync(ct);
            await ToolWrites.InsertAsync(write, "items", new JsonObject
            {
                ["type"] = "project",
                ["title"] = title,
                ["description"] = item.Description,
                ["color"] = item.Color,
                ["statusId"] = statusId.ToString(),
                ["tags"] = new JsonArray(),
                ["originItemId"] = item.Id.ToString(),
                ["projectTemplateId"] = template?.Template.Id.ToString(),
                ["projectStartDate"] = start?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }, batch, ct, projectId);
            // The source's shares, at most edit: a token grants no more than it has (docs/03 §15 #71).
            await ItemTools.InsertSharesAsync(write, projectId,
                shares.Select(x => (x.GroupId, x.Access >= ItemAccess.Edit ? "edit" : ToolConfig.EnumText(x.Access))), batch, ct);

            // The cover and the attachments: new rows with the same bytes, which the server shares (nothing to upload).
            var files = (await write.Db.Files.AsNoTracking().Where(f => f.ItemId == item.Id && !f.Deleted && f.Status == FileStatus.Available
                    && (f.Purpose == FilePurpose.Attachment || f.Id == item.ImageFileId)).ToListAsync(ct))
                .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToList();
            Guid? cover = null;
            foreach (var file in files)
            {
                var copy = await ToolWrites.InsertAsync(write, "files", new JsonObject
                {
                    ["itemId"] = projectId.ToString(),
                    ["purpose"] = ToolConfig.EnumText(file.Purpose),
                    ["fileName"] = file.FileName,
                    ["contentType"] = file.ContentType,
                    ["sizeBytes"] = file.SizeBytes,
                    ["sha256"] = file.Sha256,
                    ["width"] = file.Width,
                    ["height"] = file.Height,
                    ["thumbnail"] = file.Thumbnail is { } thumb ? Convert.ToBase64String(thumb) : null,
                }, batch, ct);
                if (file.Id == item.ImageFileId)
                {
                    cover = copy;
                }
            }
            if (cover is { } coverId)
            {
                var project = await write.Db.Items.AsNoTracking().SingleAsync(i => i.Id == projectId, ct);
                await ToolWrites.PatchAsync(write, "items", project, new Dictionary<string, JsonNode?> { ["imageFileId"] = coverId.ToString() }, ct, batch);
            }

            // The sections and values that apply to projects (ideas only; leads have none), as the user sees them.
            bool ForProjects(Guid sectionId) => config.Sections.GetValueOrDefault(sectionId) is { } section && ToolConfig.AppliesTo(section, ItemType.Project);
            var sectionIds = (await write.Db.ItemSections.AsNoTracking().Where(x => x.ItemId == item.Id && !x.Deleted).OrderBy(x => x.SortOrder)
                .Select(x => x.SectionId).ToListAsync(ct)).Where(ForProjects).ToList();
            var values = new Dictionary<Guid, JsonNode?>();
            var fieldIds = config.Fields.Where(f => f.Type != FieldType.Score && ForProjects(f.SectionId)).Select(f => f.Id).ToList();
            foreach (var value in await write.Db.FieldValues.AsNoTracking().Where(v => v.ItemId == item.Id && !v.Deleted && fieldIds.Contains(v.FieldId))
                .ToListAsync(ct))
            {
                values[value.FieldId] = JsonNode.Parse(value.Value);
            }
            var setup = await ItemSetup.SetUpAsync(write, config, projectId, ItemType.Project, template, start, new Carried(sectionIds, values), batch, ct);

            var assignments = (await write.Db.Assignments.AsNoTracking().Where(a => a.ItemId == item.Id && !a.Deleted).ToListAsync(ct))
                .OrderBy(a => a.SortOrder, StringComparer.Ordinal).ThenBy(a => a.Id).ToList();
            string? last = null;
            foreach (var a in assignments)
            {
                last = FractionalKey.After(last);
                await ToolWrites.InsertAsync(write, "assignments", new JsonObject
                {
                    ["itemId"] = projectId.ToString(), ["personId"] = a.PersonId.ToString(), ["role"] = a.Role, ["sortOrder"] = last,
                }, batch, ct);
            }

            // Outgoing links to items the user still reads (one they can't is left behind, as the device's would be refused).
            var links = 0;
            foreach (var link in await write.Db.ItemLinks.AsNoTracking().Where(l => l.FromItemId == item.Id && !l.Deleted).ToListAsync(ct))
            {
                if (await ToolWrites.TryInsertAsync(write, "item_links", new JsonObject
                    {
                        ["fromItemId"] = projectId.ToString(), ["toItemId"] = link.ToItemId.ToString(), ["relation"] = ToolConfig.EnumText(link.Relation),
                    }, batch, ct) is not null)
                {
                    links++;
                }
            }
            await ToolWrites.InsertAsync(write, "item_links", new JsonObject
            {
                ["fromItemId"] = projectId.ToString(), ["toItemId"] = item.Id.ToString(), ["relation"] = "related",
            }, batch, ct);

            var next = ConvertedStatus(config, item.Type);
            if (next is not null && next.Id != item.StatusId)
            {
                var current = await write.Db.Items.AsNoTracking().SingleAsync(i => i.Id == item.Id, ct);
                await ToolWrites.PatchAsync(write, "items", current, new Dictionary<string, JsonNode?> { ["statusId"] = next.Id.ToString() }, ct, batch);
            }
            return new
            {
                shares = shares.Count, assignments = assignments.Count, links, files = files.Count, sections = setup.Sections, values = setup.Values,
                milestones = setup.Milestones, sourceStatus = next?.Name,
            };
        }, ct);
        var created = await db.Items.AsNoTracking().SingleAsync(i => i.Id == projectId, ct);
        var summary = $"{ItemViews.KeyOf(source)} {source.Title} → {ItemViews.KeyOf(created)} {created.Title}";
        ctx.Summary = $"converted {summary}";
        ctx.Target("item", source.Id, source.Title);
        ctx.Target("item", created.Id, created.Title);
        AuditLog.Target(AuditTarget.Item(created.Id, created.Title));
        AuditLog.Summary(summary);
        var detail = await ItemViews.DetailAsync(created, ctx, db, ct);
        detail["copied"] = JsonSerializer.SerializeToNode(copied, ToolResults.Json);
        return detail;
    }

    /// <summary>The project the item was converted into, if it lives: its <c>convertedToItemId</c>, or a project naming it as origin.</summary>
    private static async Task<ItemEntity?> ConvertedAsync(PgpmDbContext db, ItemEntity source, CancellationToken ct) =>
        await db.Items.AsNoTracking().Where(i => !i.Deleted && i.Type == ItemType.Project && (i.Id == source.ConvertedToItemId || i.OriginItemId == source.Id))
            .OrderBy(i => i.CreatedAt).FirstOrDefaultAsync(ct);

    /// <summary>The status an idea or lead moves to when converted: <i>Selected</i> (ideas), <i>Won</i> (leads), if the type has it.</summary>
    public static StatusEntity? ConvertedStatus(ToolConfig config, ItemType type)
    {
        var name = type switch { ItemType.Idea => "selected", ItemType.Lead => "won", _ => null };
        return name is null ? null : config.StatusesOf(type).FirstOrDefault(s => s.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

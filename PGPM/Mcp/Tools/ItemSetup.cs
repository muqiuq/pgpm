using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Config;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sync;

namespace PGPM.Mcp.Tools;

/// <summary>A project template with its parts as a device has them (the client's <c>TemplateParts</c>): defaults of visible fields only.</summary>
public sealed record TemplateParts(
    ProjectTemplateEntity Template,
    IReadOnlyList<Guid> StatusIds,
    IReadOnlyList<TemplateSectionEntity> Sections,
    IReadOnlyList<TemplateFieldDefaultEntity> Defaults,
    IReadOnlyList<TemplateMilestoneEntity> Milestones);

/// <summary>What the set-up made: sections, values, milestones.</summary>
public sealed record SetupResult(int Sections, int Values, int Milestones);

/// <summary>The source's sections and values a conversion carries (by field id).</summary>
public sealed record Carried(IReadOnlyList<Guid> SectionIds, IReadOnlyDictionary<Guid, JsonNode?> Values);

/// <summary>
/// What the client does in the create dialog's batch (PGPM.WebUI/src/local/templates.ts <c>setUp</c>), on the server for the
/// MCP tools, through the same write unit: a new idea gets the sections <i>default for ideas</i>, a project without template
/// those <i>default for projects</i>, a project from a template its sections, field defaults, milestones and gates (due =
/// start + offset, the DoD criteria, the gates' status suggestions). Values go in only where the user may edit the section;
/// carried and given values come before the template's defaults, those before the fields' own.
/// </summary>
public static class ItemSetup
{
    /// <summary>A live template by id or name (not case-sensitive), not archived.</summary>
    public static async Task<TemplateParts> TemplateAsync(PgpmDbContext db, ToolConfig config, string value, CancellationToken ct)
    {
        var name = value.Trim();
        var templates = db.ProjectTemplates.AsNoTracking().Where(t => !t.Deleted && !t.IsArchived);
        var template = (Guid.TryParse(name, out var id) ? await templates.FirstOrDefaultAsync(t => t.Id == id, ct) : null)
            ?? (await templates.ToListAsync(ct)).FirstOrDefault(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"project template {value} (list_config has them)");
        return await PartsAsync(db, config, template, ct);
    }

    public static async Task<TemplateParts> PartsAsync(PgpmDbContext db, ToolConfig config, ProjectTemplateEntity template, CancellationToken ct)
    {
        var statuses = await db.TemplateStatuses.AsNoTracking().Where(s => s.TemplateId == template.Id && !s.Deleted).Select(s => s.StatusId).ToListAsync(ct);
        var sections = await db.TemplateSections.AsNoTracking().Where(s => s.TemplateId == template.Id && !s.Deleted).OrderBy(s => s.SortOrder)
            .ToListAsync(ct);
        var defaults = (await db.TemplateFieldDefaults.AsNoTracking().Where(d => d.TemplateId == template.Id && !d.Deleted).ToListAsync(ct))
            .Where(d => config.FieldById.ContainsKey(d.FieldId)).ToList();
        var milestones = (await db.TemplateMilestones.AsNoTracking().Where(m => m.TemplateId == template.Id && !m.Deleted).ToListAsync(ct))
            .OrderBy(m => m.SortOrder, StringComparer.Ordinal).ThenBy(m => m.Id).ToList();
        return new TemplateParts(template, statuses, sections, defaults, milestones);
    }

    /// <summary>
    /// A new project's status (the client's <c>templateStatus</c>): of the allowed ones, the template's default, else the
    /// project default, else the first.
    /// </summary>
    public static Guid? ProjectStatus(ToolConfig config, TemplateParts? template)
    {
        var allowed = config.StatusesOf(ItemType.Project).Where(s => template is not { StatusIds.Count: > 0 } t || t.StatusIds.Contains(s.Id)).ToList();
        return (allowed.FirstOrDefault(s => s.Id == template?.Template.DefaultStatusId) ?? allowed.FirstOrDefault(s => s.IsDefault) ?? allowed.FirstOrDefault())?.Id;
    }

    /// <summary>The template's default shares, at most edit (an MCP token shares no more, docs/03 §15 #60), of groups that exist.</summary>
    public static async Task<List<(Guid Group, string Access)>> DefaultSharesAsync(PgpmDbContext db, TemplateParts template, CancellationToken ct)
    {
        var shares = new List<(Guid, string)>();
        if (JsonNode.Parse(template.Template.DefaultShares) is not JsonArray list)
        {
            return shares;
        }
        var groups = (await db.Groups.AsNoTracking().Select(g => g.Id).ToListAsync(ct)).ToHashSet();
        foreach (var entry in list.OfType<JsonObject>())
        {
            if (entry["groupId"] is JsonValue g && g.GetValueKind() == JsonValueKind.String && Guid.TryParse(g.GetValue<string>(), out var group)
                && groups.Contains(group) && shares.All(s => s.Item1 != group))
            {
                var access = entry["access"] is JsonValue a && a.GetValueKind() == JsonValueKind.String ? a.GetValue<string>() : "edit";
                shares.Add((group, access == "read" ? "read" : "edit"));
            }
        }
        return shares;
    }

    private static bool Fits(SectionEntity? section, ItemType type) => section is not null && !section.IsArchived && ToolConfig.AppliesTo(section, type);

    /// <summary>The sections, values and milestones of a new idea or project, in the write unit of its insert (the same batch).</summary>
    public static async Task<SetupResult> SetUpAsync(SyncWrite write, ToolConfig config, Guid itemId, ItemType type, TemplateParts? template, DateOnly? start,
        Carried? carried, Guid batch, CancellationToken ct)
    {
        if (!ItemKinds.HasSections(type))
        {
            return new SetupResult(0, 0, 0);
        }
        var wanted = template is not null
            ? template.Sections.Select(t => config.Sections.GetValueOrDefault(t.SectionId)).Where(s => Fits(s, type)).Select(s => s!).ToList()
            : config.SectionList.Where(s => Fits(s, type) && s.DefaultFor.Contains(ToolConfig.EnumText(type))).ToList();
        foreach (var id in carried?.SectionIds ?? [])
        {
            if (config.Sections.GetValueOrDefault(id) is { } section && Fits(section, type) && wanted.All(s => s.Id != id))
            {
                wanted.Add(section);
            }
        }
        var present = (await write.Db.ItemSections.AsNoTracking().Where(s => s.ItemId == itemId && !s.Deleted).Select(s => s.SectionId).ToListAsync(ct))
            .ToHashSet();
        var sections = 0;
        var order = 0;
        foreach (var section in wanted)
        {
            order += 10;
            if (present.Add(section.Id))
            {
                await ToolWrites.InsertAsync(write, "item_sections", new JsonObject
                {
                    ["itemId"] = itemId.ToString(), ["sectionId"] = section.Id.ToString(), ["sortOrder"] = order,
                }, batch, ct);
                sections++;
            }
        }

        var values = new Dictionary<Guid, JsonNode?>();
        foreach (var d in template?.Defaults ?? [])
        {
            values[d.FieldId] = JsonNode.Parse(d.Value);
        }
        foreach (var (fieldId, value) in carried?.Values ?? new Dictionary<Guid, JsonNode?>())
        {
            values[fieldId] = value?.DeepClone();
        }
        var all = wanted.ToList();
        foreach (var fieldId in carried?.Values.Keys ?? [])
        {
            if (config.FieldById.GetValueOrDefault(fieldId) is { } field && config.Sections.GetValueOrDefault(field.SectionId) is { } section
                && Fits(section, type) && all.All(s => s.Id != section.Id))
            {
                all.Add(section);
            }
        }
        var filled = await FillAsync(write, config, itemId, all, values, batch, ct);

        var milestones = 0;
        if (template is not null)
        {
            string? last = null;
            foreach (var m in template.Milestones)
            {
                last = FractionalKey.After(last);
                var gate = m.Kind == MilestoneKind.Gate;
                var milestone = await ToolWrites.InsertAsync(write, "milestones", new JsonObject
                {
                    ["itemId"] = itemId.ToString(),
                    ["kind"] = ToolConfig.EnumText(m.Kind),
                    ["title"] = m.Title.Trim(),
                    ["description"] = m.Description,
                    ["dueDate"] = start is { } s && m.OffsetDays is { } offset ? s.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                    ["status"] = "planned",
                    ["decision"] = gate ? "pending" : null,
                    ["onGoStatusId"] = gate ? m.OnGoStatusId?.ToString() : null,
                    ["onNoGoStatusId"] = gate ? m.OnNoGoStatusId?.ToString() : null,
                    ["sortOrder"] = last,
                }, batch, ct);
                await AddDodAsync(write, milestone, DodTexts(m.Dod), batch, ct);
                milestones++;
            }
        }
        return new SetupResult(sections, filled, milestones);
    }

    /// <summary>A template milestone's criteria (<c>dod</c>: a list of texts).</summary>
    public static IReadOnlyList<string> DodTexts(string dod) =>
        JsonNode.Parse(dod) is JsonArray list
            ? [.. list.Where(t => t?.GetValueKind() == JsonValueKind.String).Select(t => t!.GetValue<string>().Trim()).Where(t => t.Length > 0)]
            : [];

    /// <summary>DoD criteria after the milestone's last one, open.</summary>
    public static async Task AddDodAsync(SyncWrite write, Guid milestoneId, IReadOnlyList<string> texts, Guid batch, CancellationToken ct)
    {
        var keys = await write.Db.DodItems.AsNoTracking().Where(d => d.MilestoneId == milestoneId && !d.Deleted).Select(d => d.SortOrder).ToListAsync(ct);
        var last = keys.Count == 0 ? null : keys.Max(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            last = FractionalKey.After(last);
            await ToolWrites.InsertAsync(write, "dod_items", new JsonObject
            {
                ["milestoneId"] = milestoneId.ToString(), ["text"] = text, ["state"] = "open", ["sortOrder"] = last,
            }, batch, ct);
        }
    }

    /// <summary>
    /// The client's <c>fillDefaults</c>: in the sections the user may edit, every field without a value gets the given one or
    /// its default – not scores, archived fields, empty or invalid values.
    /// </summary>
    private static async Task<int> FillAsync(SyncWrite write, ToolConfig config, Guid itemId, IReadOnlyList<SectionEntity> sections,
        IReadOnlyDictionary<Guid, JsonNode?> values, Guid batch, CancellationToken ct)
    {
        var existing = (await write.Db.FieldValues.AsNoTracking().Where(v => v.ItemId == itemId && !v.Deleted).Select(v => v.FieldId).ToListAsync(ct))
            .ToHashSet();
        var count = 0;
        foreach (var section in sections.Where(s => config.EditableSections.Contains(s.Id)))
        {
            foreach (var field in config.Fields.Where(f => f.SectionId == section.Id))
            {
                if (field.Type == FieldType.Score || field.IsArchived || existing.Contains(field.Id))
                {
                    continue;
                }
                var value = values.TryGetValue(field.Id, out var given) ? given : field.DefaultValue is { } d ? JsonNode.Parse(d) : null;
                if (IsEmpty(value) || FieldValueRules.Check(field.Type, JsonNode.Parse(field.Config) as JsonObject, value, out _) is not null)
                {
                    continue;
                }
                await ToolWrites.InsertAsync(write, "field_values", new JsonObject
                {
                    ["itemId"] = itemId.ToString(), ["fieldId"] = field.Id.ToString(), ["value"] = value!.DeepClone(),
                }, batch, ct);
                existing.Add(field.Id);
                count++;
            }
        }
        return count;
    }

    private static bool IsEmpty(JsonNode? value) =>
        value is null || value.GetValueKind() == JsonValueKind.Null
        || value is JsonValue s && s.GetValueKind() == JsonValueKind.String && s.GetValue<string>().Trim().Length == 0
        || value is JsonArray { Count: 0 };

    /// <summary>
    /// The <c>fields</c> argument (a field's key or label → its value) for an item of the type: fields the user sees, of
    /// sections that apply to the type and that they may edit (<c>missing_access</c> otherwise), not scores nor archived.
    /// </summary>
    public static async Task<List<(FieldDefinitionEntity Field, JsonNode? Value)>> FieldsArgAsync(JsonElement args, ToolContext ctx, PgpmDbContext db,
        ItemType type, CancellationToken ct)
    {
        if (!ToolArgs.Has(args, "fields"))
        {
            return [];
        }
        var fields = args.GetProperty("fields");
        if (fields.ValueKind != JsonValueKind.Object)
        {
            throw ToolException.Invalid("fields is an object: a field's key (or label) → its value.");
        }
        if (!ItemKinds.HasSections(type))
        {
            throw ToolException.Invalid($"A {ToolConfig.EnumText(type)} has no configured fields.");
        }
        var config = await ctx.ConfigAsync(db, ct);
        var result = new List<(FieldDefinitionEntity, JsonNode?)>();
        foreach (var property in fields.EnumerateObject())
        {
            var field = config.ResolveField(property.Name, type);
            if (field.IsArchived)
            {
                throw ToolException.Invalid($"{field.Key} is archived: it can't be changed.");
            }
            if (!config.EditableSections.Contains(field.SectionId))
            {
                throw new ToolException(SyncCodes.MissingAccess, $"{field.Key} is in the section {config.Sections[field.SectionId].Name}, which you may not edit.");
            }
            if (result.Any(r => r.Item1.Id == field.Id))
            {
                throw ToolException.Invalid($"{field.Key} is given twice.");
            }
            result.Add((field, await config.ParseAsync(db, ctx, field, property.Value, ct)));
        }
        return result;
    }

    /// <summary>
    /// Field values of an existing item, as its edit page sets them: a new value inserted, a changed one patched, null removes
    /// it; the field's section is added to the item when it doesn't show it yet (its values would be hidden otherwise).
    /// </summary>
    public static async Task<List<string>> ApplyFieldsAsync(SyncWrite write, ToolConfig config, Guid itemId,
        IReadOnlyList<(FieldDefinitionEntity Field, JsonNode? Value)> values, CancellationToken ct)
    {
        var changed = new List<string>();
        if (values.Count == 0)
        {
            return changed;
        }
        var batch = Ids.New();
        var shown = await write.Db.ItemSections.AsNoTracking().Where(s => s.ItemId == itemId && !s.Deleted).ToListAsync(ct);
        var order = shown.Count == 0 ? 0 : shown.Max(s => s.SortOrder);
        foreach (var (field, value) in values)
        {
            var existing = await write.Db.FieldValues.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.FieldId == field.Id && !v.Deleted, ct);
            if (existing is null && value is null)
            {
                continue;
            }
            if (value is not null && shown.All(s => s.SectionId != field.SectionId))
            {
                order += 10;
                await ToolWrites.InsertAsync(write, "item_sections", new JsonObject
                {
                    ["itemId"] = itemId.ToString(), ["sectionId"] = field.SectionId.ToString(), ["sortOrder"] = order,
                }, batch, ct);
                shown.Add(new ItemSectionEntity { ItemId = itemId, SectionId = field.SectionId, SortOrder = order });
            }
            if (existing is null)
            {
                await ToolWrites.InsertAsync(write, "field_values", new JsonObject
                {
                    ["itemId"] = itemId.ToString(), ["fieldId"] = field.Id.ToString(), ["value"] = value!.DeepClone(),
                }, batch, ct);
                changed.Add(field.Key);
            }
            else if (value is null)
            {
                await ToolWrites.DeleteAsync(write, "field_values", existing, ct);
                changed.Add(field.Key);
            }
            else if ((await ToolWrites.PatchAsync(write, "field_values", existing, new Dictionary<string, JsonNode?> { ["value"] = value }, ct, batch)).Count > 0)
            {
                changed.Add(field.Key);
            }
        }
        return changed;
    }

    /// <summary>Given values as the set-up takes them (a new item has none yet), with their sections.</summary>
    public static Carried? Given(IReadOnlyList<(FieldDefinitionEntity Field, JsonNode? Value)> values, Carried? carried)
    {
        if (values.Count == 0)
        {
            return carried;
        }
        var merged = new Dictionary<Guid, JsonNode?>(carried?.Values ?? new Dictionary<Guid, JsonNode?>());
        foreach (var (field, value) in values)
        {
            merged[field.Id] = value;
        }
        return new Carried([.. carried?.SectionIds ?? [], .. values.Where(v => v.Value is not null).Select(v => v.Field.SectionId).Distinct()], merged);
    }
}

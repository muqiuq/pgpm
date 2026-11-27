using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Config;

/// <summary>
/// The configuration's admin endpoints (docs/03 §3.3, <i>Configuration: Manage</i>): statuses, sections with their groups'
/// access, fields (scores validated, docs/03 §16), templates with everything below them, assignment roles. Every write goes
/// through a <see cref="SyncWrite"/> (source <c>admin</c>), so every device receives it live; reads come from the sync. A
/// change of who may view a section resets the affected users' devices (docs/02 §11.5).
/// </summary>
public sealed partial class ConfigService(PgpmDbContext db, SyncWriter writer, SessionCache sessions, IHttpContextAccessor accessor)
{
    public const string AuditSubject = "config";

    private async Task<SyncWrite> BeginAsync(Viewer viewer, CancellationToken ct) =>
        await writer.BeginAsync(viewer, ChangeSource.Admin, accessor.HttpContext?.TraceIdentifier, ct);

    private static void Audit(string kind, Guid id, string name, string summary)
    {
        AuditLog.Target(new AuditTarget(AuditSubject, id, $"{kind} {name}"));
        AuditLog.Summary($"{kind} {name}: {summary}");
    }

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^pi-[a-z0-9-]+$")]
    private static partial Regex IconPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{1,62}$")]
    internal static partial Regex FieldKeyPattern();

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // --- statuses (docs/02 §7.1) ---------------------------------------------------------------------------------------

    public async Task<StatusItem> CreateStatusAsync(Viewer viewer, StatusRequest r, CancellationToken ct)
    {
        var type = r.ItemType;
        new ApiValidator().Check(type is ItemType.Idea or ItemType.Lead or ItemType.Project, "itemType", "idea, lead or project.").ThrowIfInvalid();
        ValidateStatus(r, requireAll: true);
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        await RequireUniqueStatusNameAsync(type!.Value, r.Name!, null, ct);
        var status = new StatusEntity
        {
            Id = Ids.New(), ItemType = type.Value, Name = r.Name!.Trim(), Color = r.Color!.ToLowerInvariant(), Icon = Text(r.Icon), Category = r.Category!.Value,
            MaxDays = r.MaxDays,
            SortOrder = (await db.Statuses.Where(s => s.ItemType == type && !s.Deleted).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0) + 10,
        };
        var hasDefault = await db.Statuses.AnyAsync(s => s.ItemType == type && s.IsDefault && !s.Deleted, ct);
        if (r.IsDefault == true || !hasDefault)
        {
            await ClearDefaultAsync(type.Value, ct);
            status.IsDefault = true;
        }
        db.Statuses.Add(status);
        Audit("Status", status.Id, $"{EnumText.Of(status.ItemType)}: {status.Name}", "created");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(status);
    }

    public async Task<StatusItem> UpdateStatusAsync(Viewer viewer, Guid id, StatusRequest r, CancellationToken ct)
    {
        ValidateStatus(r, requireAll: true);
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var status = await db.Statuses.FirstOrDefaultAsync(s => s.Id == id && !s.Deleted, ct) ?? throw ApiException.NotFound("Status");
        new ApiValidator().Check(r.ItemType is null || r.ItemType == status.ItemType, "itemType", "A status's item type doesn't change.").ThrowIfInvalid();
        if (!status.IsArchived)
        {
            await RequireUniqueStatusNameAsync(status.ItemType, r.Name!, id, ct);
        }
        if (status.IsDefault && r.IsDefault == false)
        {
            throw ApiException.Conflict("default_required", "Make another status the default instead.");
        }
        if (r.IsDefault == true && !status.IsDefault)
        {
            if (status.IsArchived)
            {
                throw ApiException.Conflict("status_archived", "An archived status can't be the default.");
            }
            await ClearDefaultAsync(status.ItemType, ct);
            status.IsDefault = true;
        }
        status.Name = r.Name!.Trim();
        status.Color = r.Color!.ToLowerInvariant();
        status.Icon = Text(r.Icon);
        status.Category = r.Category!.Value;
        status.MaxDays = r.MaxDays;
        Audit("Status", status.Id, $"{EnumText.Of(status.ItemType)}: {status.Name}", "updated");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(status);
    }

    public async Task<StatusItem> ArchiveStatusAsync(Viewer viewer, Guid id, bool archived, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var status = await db.Statuses.FirstOrDefaultAsync(s => s.Id == id && !s.Deleted, ct) ?? throw ApiException.NotFound("Status");
        if (archived && status.IsDefault)
        {
            throw ApiException.Conflict("default_status", "The default status can't be archived: make another one the default first.");
        }
        if (!archived && status.IsArchived)
        {
            await RequireUniqueStatusNameAsync(status.ItemType, status.Name, id, ct);
        }
        status.IsArchived = archived;
        Audit("Status", status.Id, $"{EnumText.Of(status.ItemType)}: {status.Name}", archived ? "archived" : "unarchived");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(status);
    }

    public async Task<IReadOnlyList<StatusItem>> OrderStatusesAsync(Viewer viewer, OrderRequest r, CancellationToken ct)
    {
        new ApiValidator().Check(r.ItemType is ItemType.Idea or ItemType.Lead or ItemType.Project, "itemType", "idea, lead or project.").ThrowIfInvalid();
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var statuses = await db.Statuses.Where(s => s.ItemType == r.ItemType && !s.Deleted).ToListAsync(ct);
        Reorder(statuses, r.Ids, s => s.Id, (s, order) => s.SortOrder = order);
        AuditLog.Summary($"Statuses of {EnumText.Of(r.ItemType!.Value)}s reordered");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return statuses.OrderBy(s => s.SortOrder).Select(ToItem).ToList();
    }

    private static void ValidateStatus(StatusRequest r, bool requireAll) =>
        new ApiValidator()
            .Check(r.Name is { } n && n.Trim().Length is >= 1 and <= 100, "name", "1 to 100 characters.")
            .Check(r.Color is { } c && ColorPattern().IsMatch(c.ToLowerInvariant()), "color", "A colour #rrggbb.")
            .Check(Text(r.Icon) is null || IconPattern().IsMatch(r.Icon!.Trim()), "icon", "A PrimeIcons name (pi-lightbulb), or none.")
            .Check(!requireAll || r.Category is not null, "category", "open, active or closed.")
            .Check(r.MaxDays is null or (>= 1 and <= 36500), "maxDays", "1 to 36,500 days, or none.")
            .ThrowIfInvalid();

    private async Task RequireUniqueStatusNameAsync(ItemType type, string name, Guid? except, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.Statuses.AnyAsync(s => s.ItemType == type && !s.Deleted && !s.IsArchived && s.Id != except && s.Name.ToLower() == normalized, ct))
        {
            throw ApiException.Conflict("duplicate_name", $"Another {EnumText.Of(type)} status has this name.");
        }
    }

    /// <summary>The type's default is no default any more – saved at once: the partial unique index allows one at a time.</summary>
    private async Task ClearDefaultAsync(ItemType type, CancellationToken ct)
    {
        var current = await db.Statuses.Where(s => s.ItemType == type && s.IsDefault && !s.Deleted).ToListAsync(ct);
        if (current.Count > 0)
        {
            current.ForEach(s => s.IsDefault = false);
            await db.SaveChangesAsync(ct);
        }
    }

    public static StatusItem ToItem(StatusEntity s) =>
        new(s.Id, s.ItemType, s.Name, s.Color, s.Icon, s.Category, s.SortOrder, s.IsDefault, s.MaxDays, s.IsArchived, s.Rev);

    /// <summary>All ids of a list in their new order: sort orders 10, 20, 30 …</summary>
    private static void Reorder<T>(List<T> rows, List<Guid>? ids, Func<T, Guid> idOf, Action<T, int> set)
    {
        var wanted = ids ?? [];
        new ApiValidator().Check(wanted.Count == rows.Count && wanted.Distinct().Count() == wanted.Count && wanted.All(i => rows.Any(r => idOf(r) == i)),
            "ids", "Every id of the list once.").ThrowIfInvalid();
        for (var i = 0; i < wanted.Count; i++)
        {
            set(rows.First(r => idOf(r) == wanted[i]), (i + 1) * 10);
        }
    }

    // --- sections and their groups (docs/02 §7.2, §7.3) ---------------------------------------------------------------

    public async Task<SectionItem> CreateSectionAsync(Viewer viewer, SectionRequest r, CancellationToken ct)
    {
        var (appliesTo, defaultFor, access) = await ValidateSectionAsync(r, ct);
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var section = new SectionEntity
        {
            Id = Ids.New(), Name = r.Name!.Trim(), Description = Text(r.Description), AppliesTo = appliesTo, DefaultFor = defaultFor,
            CollapsedByDefault = r.CollapsedByDefault ?? false,
            SortOrder = (await db.Sections.Where(s => !s.Deleted).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0) + 10,
        };
        db.Sections.Add(section);
        await db.SaveChangesAsync(ct);
        await SetSectionAccessAsync(section, access ?? [], ct);
        Audit("Section", section.Id, section.Name, "created");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        ForgetSessions();
        return await SectionItemAsync(section, ct);
    }

    public async Task<SectionItem> UpdateSectionAsync(Viewer viewer, Guid id, SectionRequest r, CancellationToken ct)
    {
        var (appliesTo, defaultFor, access) = await ValidateSectionAsync(r, ct);
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var section = await db.Sections.FirstOrDefaultAsync(s => s.Id == id && !s.Deleted, ct) ?? throw ApiException.NotFound("Section");
        if (!appliesTo.Contains("project") && await db.TemplateSections.AnyAsync(t => t.SectionId == id && !t.Deleted, ct))
        {
            throw ApiException.Conflict("section_in_template", "Templates use the section on projects: remove it from them first.");
        }
        section.Name = r.Name!.Trim();
        section.Description = Text(r.Description);
        section.AppliesTo = appliesTo;
        section.DefaultFor = defaultFor;
        section.CollapsedByDefault = r.CollapsedByDefault ?? section.CollapsedByDefault;
        if (access is not null)
        {
            await SetSectionAccessAsync(section, access, ct);
        }
        Audit("Section", section.Id, section.Name, "updated");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        ForgetSessions();
        return await SectionItemAsync(section, ct);
    }

    public async Task<SectionItem> ArchiveSectionAsync(Viewer viewer, Guid id, bool archived, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var section = await db.Sections.FirstOrDefaultAsync(s => s.Id == id && !s.Deleted, ct) ?? throw ApiException.NotFound("Section");
        section.IsArchived = archived;
        Audit("Section", section.Id, section.Name, archived ? "archived" : "unarchived");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return await SectionItemAsync(section, ct);
    }

    public async Task<IReadOnlyList<SectionItem>> OrderSectionsAsync(Viewer viewer, OrderRequest r, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var sections = await db.Sections.Where(s => !s.Deleted).ToListAsync(ct);
        Reorder(sections, r.Ids, s => s.Id, (s, order) => s.SortOrder = order);
        AuditLog.Summary("Sections reordered");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        var items = new List<SectionItem>();
        foreach (var section in sections.OrderBy(s => s.SortOrder))
        {
            items.Add(await SectionItemAsync(section, ct));
        }
        return items;
    }

    private async Task<(List<string> AppliesTo, List<string> DefaultFor, List<(Guid Group, SectionAccess Level)>? Access)> ValidateSectionAsync(
        SectionRequest r, CancellationToken ct)
    {
        var appliesTo = (r.AppliesTo ?? []).Select(a => a.Trim().ToLowerInvariant()).ToList();
        var defaultFor = (r.DefaultFor ?? []).Select(a => a.Trim().ToLowerInvariant()).ToList();
        var access = r.Access?.Select(a => (a.GroupId ?? Guid.Empty, a.Level ?? SectionAccess.View)).ToList();
        var v = new ApiValidator()
            .Check(r.Name is { } n && n.Trim().Length is >= 1 and <= 100, "name", "1 to 100 characters.")
            .Check(r.Description is null || r.Description.Length <= 4000, "description", "At most 4,000 characters.")
            .Check(appliesTo.Count > 0 && appliesTo.All(a => a is "idea" or "project") && appliesTo.Distinct().Count() == appliesTo.Count, "appliesTo",
                "idea and/or project.")
            .Check(defaultFor.All(appliesTo.Contains) && defaultFor.Distinct().Count() == defaultFor.Count, "defaultFor", "⊆ appliesTo.")
            .Check(r.Access is null || r.Access.All(a => a.GroupId is { } g && g != Guid.Empty && a.Level is not null), "access", "A group and view or edit each.")
            .Check(access is null || access.Select(a => a.Item1).Distinct().Count() == access.Count, "access", "Each group once.");
        v.ThrowIfInvalid();
        if (access is { Count: > 0 })
        {
            var groupIds = access.Select(a => a.Item1).ToList();
            new ApiValidator().Check(await db.Groups.CountAsync(g => groupIds.Contains(g.Id), ct) == groupIds.Count, "access", "Unknown group.").ThrowIfInvalid();
        }
        return (appliesTo, defaultFor, access);
    }

    /// <summary>
    /// The section's groups replaced (docs/02 §7.3). Who may view it changed → the affected users' devices reset (their
    /// <c>access_epoch</c>, docs/02 §11.5); the section row is stamped anew in any case, so every device gets its <c>canEdit</c>.
    /// </summary>
    internal async Task SetSectionAccessAsync(SectionEntity section, IReadOnlyCollection<(Guid Group, SectionAccess Level)> access, CancellationToken ct)
    {
        var current = await db.SectionGroups.Where(g => g.SectionId == section.Id).ToListAsync(ct);
        var before = current.Select(g => (g.GroupId, g.Access)).ToHashSet();
        var after = access.ToHashSet();
        if (before.SetEquals(after))
        {
            return;
        }
        db.SectionGroups.RemoveRange(current);
        await db.SaveChangesAsync(ct);
        db.SectionGroups.AddRange(access.Select(a => new SectionGroupEntity { SectionId = section.Id, GroupId = a.Group, Access = a.Level }));
        db.Entry(section).Property(s => s.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(ct);
        var affected = await AffectedUsersAsync(Seen(before), Seen(after), ct);
        if (affected.Count > 0)
        {
            await AccessService.BumpAccessEpochAsync(db, affected, ct);
            Forget.AddRange(affected);
        }
    }

    /// <summary>The users whose devices must reset after this request commits (their sessions are forgotten then).</summary>
    internal List<Guid> Forget { get; } = [];

    /// <summary>Tells the live connections of <see cref="Forget"/>'s users: after the commit.</summary>
    internal void ForgetSessions()
    {
        foreach (var user in Forget.Distinct())
        {
            sessions.Forget(user);
        }
        Forget.Clear();
    }

    /// <summary>Who sees a section: everybody (null) without view rows, else the groups with any row.</summary>
    private static HashSet<Guid>? Seen(HashSet<(Guid Group, SectionAccess Level)> rows) =>
        rows.All(r => r.Level != SectionAccess.View) ? null : rows.Select(r => r.Group).ToHashSet();

    private async Task<List<Guid>> AffectedUsersAsync(HashSet<Guid>? before, HashSet<Guid>? after, CancellationToken ct)
    {
        if (before is null && after is null || before is not null && after is not null && before.SetEquals(after))
        {
            return [];
        }
        if (before is null || after is null)
        {
            return await db.Users.Where(u => !u.IsAdmin).Select(u => u.Id).ToListAsync(ct);
        }
        var changed = before.Except(after).Concat(after.Except(before)).ToList();
        return await db.UserGroups.Where(m => changed.Contains(m.GroupId)).Select(m => m.UserId).Distinct().ToListAsync(ct);
    }

    private async Task<SectionItem> SectionItemAsync(SectionEntity s, CancellationToken ct)
    {
        var access = await db.SectionGroups.AsNoTracking().Where(g => g.SectionId == s.Id)
            .Join(db.Groups, g => g.GroupId, x => x.Id, (g, x) => new { g.GroupId, x.Name, g.Access }).OrderBy(g => g.Name).ToListAsync(ct);
        return new SectionItem(s.Id, s.Name, s.Description, s.AppliesTo, s.DefaultFor, s.SortOrder, s.CollapsedByDefault, s.IsArchived,
            access.Select(a => new SectionAccessItem(a.GroupId, a.Name, a.Access)).ToList(), s.Rev);
    }

    // --- fields (docs/02 §7.4) ----------------------------------------------------------------------------------------

    public async Task<FieldItem> CreateFieldAsync(Viewer viewer, FieldRequest r, CancellationToken ct)
    {
        new ApiValidator()
            .Check(r.Key is { } k && FieldKeyPattern().IsMatch(k), "key", "^[a-z][a-z0-9_]{1,62}$.")
            .Check(r.Type is not null, "type", "The field's type.")
            .ThrowIfInvalid();
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        if (await db.FieldDefinitions.AnyAsync(f => f.Key == r.Key && !f.Deleted, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"A field with the key {r.Key} exists already.");
        }
        var field = new FieldDefinitionEntity { Id = Ids.New(), Key = r.Key!, Type = r.Type!.Value };
        await ApplyFieldAsync(field, r, ct);
        field.SortOrder = (await db.FieldDefinitions.Where(f => f.SectionId == field.SectionId && !f.Deleted).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0) + 10;
        db.FieldDefinitions.Add(field);
        Audit("Field", field.Id, field.Key, "created");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(field);
    }

    public async Task<FieldItem> UpdateFieldAsync(Viewer viewer, Guid id, FieldRequest r, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var field = await db.FieldDefinitions.FirstOrDefaultAsync(f => f.Id == id && !f.Deleted, ct) ?? throw ApiException.NotFound("Field");
        new ApiValidator()
            .Check(r.Key is null || r.Key == field.Key, "key", "A field's key never changes.")
            .Check(r.Type is null || r.Type == field.Type, "type", "A field's type never changes.")
            .ThrowIfInvalid();
        await ApplyFieldAsync(field, r, ct);
        Audit("Field", field.Id, field.Key, "updated");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(field);
    }

    /// <summary>The request's values into the field, validated: its section, label, configuration (scores with all fields), default value.</summary>
    private async Task ApplyFieldAsync(FieldDefinitionEntity field, FieldRequest r, CancellationToken ct)
    {
        var config = r.Config ?? new JsonObject();
        var v = new ApiValidator()
            .Check(r.SectionId is { } s && await db.Sections.AnyAsync(x => x.Id == s && !x.Deleted, ct), "sectionId", "An existing section.")
            .Check(r.Label is { } l && l.Trim().Length is >= 1 and <= 200, "label", "1 to 200 characters.")
            .Check(r.HelpText is null || r.HelpText.Length <= 1000, "helpText", "At most 1,000 characters.")
            .Check(field.Type != FieldType.Score || r.Required != true, "required", "Scores are computed: never required.");
        foreach (var error in FieldValueRules.CheckConfig(field.Type, config))
        {
            v.Add("config", error);
        }
        var defaultValue = r.DefaultValue is null || r.DefaultValue.GetValueKind() == JsonValueKind.Null ? null : r.DefaultValue;
        if (defaultValue is not null && field.Type is FieldType.Score or FieldType.Person)
        {
            v.Add("defaultValue", $"{EnumText.Of(field.Type)} fields have no default value.");
        }
        else if (FieldValueRules.Check(field.Type, config, defaultValue, out _) is { } error)
        {
            v.Add("defaultValue", error);
        }
        v.ThrowIfInvalid();
        field.SectionId = r.SectionId!.Value;
        field.Label = r.Label!.Trim();
        field.HelpText = Text(r.HelpText);
        field.Config = config.ToJsonString(WireJson.Options);
        field.Required = r.Required ?? false;
        field.DefaultValue = defaultValue?.ToJsonString(WireJson.Options);
        await ValidateScoresAsync(field, ct);
    }

    /// <summary>
    /// The scores among all fields stay valid (docs/03 §16): the field itself if it is one, and every score that uses it as an
    /// input (an input's options or maximum may have changed). 400 with the codes of <see cref="ScoreConfigValidator"/>.
    /// </summary>
    private async Task ValidateScoresAsync(FieldDefinitionEntity changed, CancellationToken ct)
    {
        var all = await db.FieldDefinitions.AsNoTracking().Where(f => !f.Deleted && f.Id != changed.Id).ToListAsync(ct);
        all.Add(changed);
        var specs = all.Select(Spec).ToList();
        var v = new ApiValidator();
        foreach (var field in all.Where(f => f.Type == FieldType.Score && !f.IsArchived))
        {
            var spec = Spec(field);
            if (field.Id != changed.Id && !(spec.Settings.Inputs ?? []).Contains(changed.Key))
            {
                continue;
            }
            foreach (var error in ScoreConfigValidator.Validate(spec, specs))
            {
                v.Add("config", field.Id == changed.Id ? $"{error.Code}{(error.Input is null ? "" : $": {error.Input}")}"
                    : $"the score {field.Key} would become invalid: {error.Code}{(error.Input is null ? "" : $" ({error.Input})")}");
            }
        }
        v.ThrowIfInvalid();
    }

    public static FieldSpec Spec(FieldDefinitionEntity f) =>
        new(f.Key, EnumText.Of(f.Type), FieldConfig.FromJson(JsonDocument.Parse(f.Config).RootElement), f.IsArchived);

    public async Task<FieldItem> ArchiveFieldAsync(Viewer viewer, Guid id, bool archived, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var field = await db.FieldDefinitions.FirstOrDefaultAsync(f => f.Id == id && !f.Deleted, ct) ?? throw ApiException.NotFound("Field");
        if (archived && (await ScoresUsingAsync(field.Key, ct)).Count > 0)
        {
            throw ApiException.Conflict("used_by_score", "Scores use the field: remove it from their inputs first.");
        }
        field.IsArchived = archived;
        if (!archived)
        {
            await ValidateScoresAsync(field, ct);
        }
        Audit("Field", field.Id, field.Key, archived ? "archived" : "unarchived");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(field);
    }

    private async Task<List<string>> ScoresUsingAsync(string key, CancellationToken ct) =>
        (await db.FieldDefinitions.AsNoTracking().Where(f => f.Type == FieldType.Score && !f.Deleted && !f.IsArchived).ToListAsync(ct))
        .Where(f => (Spec(f).Settings.Inputs ?? []).Contains(key)).Select(f => f.Key).ToList();

    public async Task<FieldUsage> FieldUsageAsync(Guid id, CancellationToken ct)
    {
        var field = await db.FieldDefinitions.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && !f.Deleted, ct) ?? throw ApiException.NotFound("Field");
        var values = db.FieldValues.Where(v => v.FieldId == id && !v.Deleted);
        return new FieldUsage(id, field.Key, await values.CountAsync(ct), await values.Select(v => v.ItemId).Distinct().CountAsync(ct),
            await ScoresUsingAsync(field.Key, ct), await db.TemplateFieldDefaults.CountAsync(d => d.FieldId == id && !d.Deleted, ct));
    }

    /// <summary>
    /// Deletes a field with its values and template defaults (one delete batch, every device gets the tombstones):
    /// <paramref name="confirm"/> must be the number of its values (<see cref="FieldUsageAsync"/>), 409 otherwise; never a field
    /// a score uses.
    /// </summary>
    public async Task DeleteFieldAsync(Viewer viewer, Guid id, int? confirm, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var field = await db.FieldDefinitions.FirstOrDefaultAsync(f => f.Id == id && !f.Deleted, ct) ?? throw ApiException.NotFound("Field");
        if ((await ScoresUsingAsync(field.Key, ct)).Count > 0)
        {
            throw ApiException.Conflict("used_by_score", "Scores use the field: remove it from their inputs first.");
        }
        var values = await db.FieldValues.CountAsync(v => v.FieldId == id && !v.Deleted, ct);
        if (confirm != values)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "confirmation_required",
                $"The field has {values} value(s): delete with ?confirm={values}.", extensions: new Dictionary<string, object?> { ["values"] = values });
        }
        var batch = write.NewBatch(DeleteRootKind.Other, "field_definitions", id);
        await SyncTables.Get("field_definitions").MarkDeletedAsync(write, field, batch.Id, ct);
        Audit("Field", field.Id, field.Key, $"deleted with {values} value(s)");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FieldItem>> OrderFieldsAsync(Viewer viewer, OrderRequest r, CancellationToken ct)
    {
        new ApiValidator().Check(r.SectionId is not null, "sectionId", "The section whose fields these are.").ThrowIfInvalid();
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var fields = await db.FieldDefinitions.Where(f => f.SectionId == r.SectionId && !f.Deleted).ToListAsync(ct);
        Reorder(fields, r.Ids, f => f.Id, (f, order) => f.SortOrder = order);
        AuditLog.Summary("Fields reordered");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return fields.OrderBy(f => f.SortOrder).Select(ToItem).ToList();
    }

    public static FieldItem ToItem(FieldDefinitionEntity f) =>
        new(f.Id, f.SectionId, f.Key, f.Label, f.HelpText, f.Type, JsonNode.Parse(f.Config) as JsonObject ?? [], f.Required,
            f.DefaultValue is null ? null : JsonNode.Parse(f.DefaultValue), f.SortOrder, f.IsArchived, f.Rev);

    // --- templates (docs/02 §7.7) -------------------------------------------------------------------------------------

    public async Task<TemplateDetail> CreateTemplateAsync(Viewer viewer, TemplateRequest r, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var template = new ProjectTemplateEntity
        {
            Id = Ids.New(), SortOrder = (await db.ProjectTemplates.Where(t => !t.Deleted).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0) + 10,
        };
        db.ProjectTemplates.Add(template);
        await ApplyTemplateAsync(write, template, r, ct);
        Audit("Template", template.Id, template.Name, "created");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return await TemplateAsync(template.Id, ct);
    }

    public async Task<TemplateDetail> UpdateTemplateAsync(Viewer viewer, Guid id, TemplateRequest r, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var template = await db.ProjectTemplates.FirstOrDefaultAsync(t => t.Id == id && !t.Deleted, ct) ?? throw ApiException.NotFound("Template");
        await ApplyTemplateAsync(write, template, r, ct);
        Audit("Template", template.Id, template.Name, "updated");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return await TemplateAsync(template.Id, ct);
    }

    public async Task<TemplateDetail> ArchiveTemplateAsync(Viewer viewer, Guid id, bool archived, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var template = await db.ProjectTemplates.FirstOrDefaultAsync(t => t.Id == id && !t.Deleted, ct) ?? throw ApiException.NotFound("Template");
        template.IsArchived = archived;
        Audit("Template", template.Id, template.Name, archived ? "archived" : "unarchived");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return await TemplateAsync(id, ct);
    }

    public async Task<IReadOnlyList<TemplateDetail>> OrderTemplatesAsync(Viewer viewer, OrderRequest r, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var templates = await db.ProjectTemplates.Where(t => !t.Deleted).ToListAsync(ct);
        Reorder(templates, r.Ids, t => t.Id, (t, order) => t.SortOrder = order);
        AuditLog.Summary("Templates reordered");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        var items = new List<TemplateDetail>();
        foreach (var template in templates.OrderBy(t => t.SortOrder))
        {
            items.Add(await TemplateAsync(template.Id, ct));
        }
        return items;
    }

    /// <summary>The template and its lists as the request has them, validated (docs/03 §17's rules); removed children are tombstoned.</summary>
    private async Task ApplyTemplateAsync(SyncWrite write, ProjectTemplateEntity template, TemplateRequest r, CancellationToken ct)
    {
        var statusIds = r.StatusIds ?? [];
        var sectionIds = r.SectionIds ?? [];
        var defaults = r.FieldDefaults ?? [];
        var shares = r.DefaultShares ?? [];
        var milestones = r.Milestones ?? [];
        var projectStatuses = await db.Statuses.AsNoTracking().Where(s => s.ItemType == ItemType.Project && !s.Deleted).ToDictionaryAsync(s => s.Id, ct);
        var sections = await db.Sections.AsNoTracking().Where(s => sectionIds.Contains(s.Id) && !s.Deleted).ToDictionaryAsync(s => s.Id, ct);
        var fieldIds = defaults.Select(d => d.FieldId ?? Guid.Empty).ToList();
        var fields = await db.FieldDefinitions.AsNoTracking().Where(f => fieldIds.Contains(f.Id) && !f.Deleted).ToDictionaryAsync(f => f.Id, ct);
        var groupIds = shares.Select(s => s.GroupId ?? Guid.Empty).ToList();
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
        bool Allowed(Guid? id) => id is null || projectStatuses.ContainsKey(id.Value) && (statusIds.Count == 0 || statusIds.Contains(id.Value));
        var v = new ApiValidator()
            .Check(r.Name is { } n && n.Trim().Length is >= 1 and <= 100, "name", "1 to 100 characters.")
            .Check(r.Description is null || r.Description.Length <= 4000, "description", "At most 4,000 characters.")
            .Check(r.Color is null || ColorPattern().IsMatch(r.Color.ToLowerInvariant()), "color", "A colour #rrggbb, or none.")
            .Check(Text(r.Icon) is null || IconPattern().IsMatch(r.Icon!.Trim()), "icon", "A PrimeIcons name, or none.")
            .Check(statusIds.Distinct().Count() == statusIds.Count && statusIds.All(projectStatuses.ContainsKey), "statusIds", "Project statuses, each once.")
            .Check(Allowed(r.DefaultStatusId), "defaultStatusId", "A project status the template allows.")
            .Check(sectionIds.Distinct().Count() == sectionIds.Count && sectionIds.All(id => sections.TryGetValue(id, out var s) && s.AppliesTo.Contains("project")),
                "sectionIds", "Sections that apply to projects, each once.")
            .Check(groupIds.Distinct().Count() == groupIds.Count && groupIds.All(groups.Contains) && shares.All(s => s.Access is ItemAccess.Read
                or ItemAccess.Edit or ItemAccess.Manage), "defaultShares", "Existing groups, each once, with read, edit or manage.")
            .Check(fieldIds.Distinct().Count() == fieldIds.Count, "fieldDefaults", "Each field once.");
        foreach (var d in defaults)
        {
            if (d.FieldId is not { } fid || !fields.TryGetValue(fid, out var field))
            {
                v.Add("fieldDefaults", "Unknown field.");
                continue;
            }
            if (!sectionIds.Contains(field.SectionId))
            {
                v.Add("fieldDefaults", $"{field.Key}: its section is not one of the template's.");
            }
            if (field.Type is FieldType.Score or FieldType.Person)
            {
                v.Add("fieldDefaults", $"{field.Key}: {EnumText.Of(field.Type)} fields have no default.");
            }
            else if (FieldValueRules.Check(field.Type, JsonNode.Parse(field.Config) as JsonObject, d.Value, out _) is { } error)
            {
                v.Add("fieldDefaults", $"{field.Key}: {error}");
            }
        }
        foreach (var m in milestones)
        {
            var gate = m.Kind == MilestoneKind.Gate;
            v.Check(m.Kind is not null, "milestones", "Each with its kind (milestone or gate).")
                .Check(m.Title is { } t && t.Trim().Length is >= 1 and <= 200, "milestones", "Titles of 1 to 200 characters.")
                .Check(m.Description is null || m.Description.Length <= 4000, "milestones", "Descriptions of at most 4,000 characters.")
                .Check(m.OffsetDays is null or (>= 0 and <= 3650), "milestones", "Offsets of 0 to 3,650 days.")
                .Check((m.Dod ?? []).All(x => x.Trim().Length is >= 1 and <= 500) && (m.Dod ?? []).Count <= 100, "milestones",
                    "DoD items of 1 to 500 characters, at most 100.")
                .Check(gate || m.OnGoStatusId is null && m.OnNoGoStatusId is null, "milestones", "Only gates suggest statuses.")
                .Check(Allowed(m.OnGoStatusId) && Allowed(m.OnNoGoStatusId), "milestones", "Suggested statuses are project statuses the template allows.");
        }
        v.ThrowIfInvalid();

        template.Name = r.Name!.Trim();
        template.Description = Text(r.Description);
        template.Color = r.Color?.ToLowerInvariant();
        template.Icon = Text(r.Icon);
        template.DefaultStatusId = r.DefaultStatusId;
        template.DefaultShares = new JsonArray([.. shares.Select(s => (JsonNode)new JsonObject
        {
            ["groupId"] = s.GroupId!.Value.ToString(), ["access"] = EnumText.Of(s.Access!.Value),
        })]).ToJsonString(WireJson.Options);
        await db.SaveChangesAsync(ct);

        DeleteBatchEntity? batch = null;
        Guid Batch() => (batch ??= write.NewBatch(DeleteRootKind.Other, "project_templates", template.Id)).Id;

        var existingStatuses = await db.TemplateStatuses.Where(s => s.TemplateId == template.Id && !s.Deleted).ToListAsync(ct);
        foreach (var gone in existingStatuses.Where(s => !statusIds.Contains(s.StatusId)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        db.TemplateStatuses.AddRange(statusIds.Where(id => existingStatuses.All(s => s.StatusId != id))
            .Select(id => new TemplateStatusEntity { Id = Ids.New(), TemplateId = template.Id, StatusId = id }));

        var existingSections = await db.TemplateSections.Where(s => s.TemplateId == template.Id && !s.Deleted).ToListAsync(ct);
        foreach (var gone in existingSections.Where(s => !sectionIds.Contains(s.SectionId)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        for (var i = 0; i < sectionIds.Count; i++)
        {
            var row = existingSections.FirstOrDefault(s => s.SectionId == sectionIds[i]);
            if (row is null)
            {
                db.TemplateSections.Add(new TemplateSectionEntity { Id = Ids.New(), TemplateId = template.Id, SectionId = sectionIds[i], SortOrder = (i + 1) * 10 });
            }
            else if (row.SortOrder != (i + 1) * 10)
            {
                row.SortOrder = (i + 1) * 10;
            }
        }

        var existingDefaults = await db.TemplateFieldDefaults.Where(d => d.TemplateId == template.Id && !d.Deleted).ToListAsync(ct);
        foreach (var gone in existingDefaults.Where(d => !fieldIds.Contains(d.FieldId)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        foreach (var d in defaults)
        {
            var value = d.Value?.ToJsonString(WireJson.Options) ?? "null";
            var row = existingDefaults.FirstOrDefault(x => x.FieldId == d.FieldId);
            if (row is null)
            {
                db.TemplateFieldDefaults.Add(new TemplateFieldDefaultEntity { Id = Ids.New(), TemplateId = template.Id, FieldId = d.FieldId!.Value, Value = value });
            }
            else if (!WireJson.Same(JsonNode.Parse(row.Value), d.Value))
            {
                row.Value = value;
            }
        }

        var existingMilestones = await db.TemplateMilestones.Where(m => m.TemplateId == template.Id && !m.Deleted).ToListAsync(ct);
        var keep = milestones.Where(m => m.Id is not null).Select(m => m.Id!.Value).ToHashSet();
        foreach (var gone in existingMilestones.Where(m => !keep.Contains(m.Id)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        var keys = FractionalKey.Sequence(milestones.Count);
        for (var i = 0; i < milestones.Count; i++)
        {
            var m = milestones[i];
            var row = m.Id is { } mid ? existingMilestones.FirstOrDefault(x => x.Id == mid) : null;
            if (row is null)
            {
                var reuse = m.Id is { } given && Ids.IsVersion7(given) && !await db.TemplateMilestones.AnyAsync(x => x.Id == given, ct);
                row = new TemplateMilestoneEntity { Id = reuse ? m.Id!.Value : Ids.New(), TemplateId = template.Id };
                db.TemplateMilestones.Add(row);
            }
            row.Kind = m.Kind!.Value;
            row.Title = m.Title!.Trim();
            row.Description = Text(m.Description);
            row.OffsetDays = m.OffsetDays;
            var dod = new JsonArray([.. (m.Dod ?? []).Select(x => (JsonNode?)JsonValue.Create(x.Trim()))]);
            if (!WireJson.Same(JsonNode.Parse(row.Dod), dod))
            {
                row.Dod = dod.ToJsonString(WireJson.Options);
            }
            row.OnGoStatusId = m.OnGoStatusId;
            row.OnNoGoStatusId = m.OnNoGoStatusId;
            row.SortOrder = keys[i];
        }
    }

    public async Task<TemplateDetail> TemplateAsync(Guid id, CancellationToken ct)
    {
        var t = await db.ProjectTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct) ?? throw ApiException.NotFound("Template");
        var statuses = await db.TemplateStatuses.AsNoTracking().Where(s => s.TemplateId == id && !s.Deleted).OrderBy(s => s.Seq).Select(s => s.StatusId)
            .ToListAsync(ct);
        var sections = await db.TemplateSections.AsNoTracking().Where(s => s.TemplateId == id && !s.Deleted).OrderBy(s => s.SortOrder)
            .Select(s => s.SectionId).ToListAsync(ct);
        var defaults = await db.TemplateFieldDefaults.AsNoTracking().Where(d => d.TemplateId == id && !d.Deleted).OrderBy(d => d.Seq).ToListAsync(ct);
        var milestones = await db.TemplateMilestones.AsNoTracking().Where(m => m.TemplateId == id && !m.Deleted).ToListAsync(ct);
        var shares = (JsonNode.Parse(t.DefaultShares) as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => new TemplateShareItem(Guid.Parse(s["groupId"]!.GetValue<string>()), EnumText.Parse<ItemAccess>(s["access"]!.GetValue<string>())))
            .ToList();
        return new TemplateDetail(t.Id, t.Name, t.Description, t.Color, t.Icon, t.DefaultStatusId, statuses, sections,
            defaults.Select(d => new TemplateFieldDefaultItem(d.FieldId, JsonNode.Parse(d.Value))).ToList(), shares,
            milestones.OrderBy(m => m.SortOrder, StringComparer.Ordinal).ThenBy(m => m.Id)
                .Select(m => new TemplateMilestoneItem(m.Id, m.Kind, m.Title, m.Description, m.OffsetDays,
                    (JsonNode.Parse(m.Dod) as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToList(), m.OnGoStatusId, m.OnNoGoStatusId,
                    m.SortOrder)).ToList(),
            t.SortOrder, t.IsArchived, t.Rev);
    }

    // --- assignment roles (docs/03 §17) -------------------------------------------------------------------------------

    public async Task<RoleItem> CreateRoleAsync(Viewer viewer, RoleRequest r, CancellationToken ct)
    {
        ValidateRole(r);
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        await RequireUniqueRoleAsync(r.Name!, null, ct);
        var role = new AssignmentRoleEntity
        {
            Id = Ids.New(), Name = r.Name!.Trim(), Description = Text(r.Description),
            SortOrder = r.SortOrder ?? (await db.AssignmentRoles.Where(x => !x.Deleted).MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 10,
        };
        db.AssignmentRoles.Add(role);
        Audit("Role", role.Id, role.Name, "created");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(role);
    }

    public async Task<RoleItem> UpdateRoleAsync(Viewer viewer, Guid id, RoleRequest r, CancellationToken ct)
    {
        ValidateRole(r);
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var role = await db.AssignmentRoles.FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct) ?? throw ApiException.NotFound("Role");
        await RequireUniqueRoleAsync(r.Name!, id, ct);
        role.Name = r.Name!.Trim();
        role.Description = Text(r.Description);
        role.SortOrder = r.SortOrder ?? role.SortOrder;
        Audit("Role", role.Id, role.Name, "updated");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return ToItem(role);
    }

    /// <summary>A role is a suggestion only: deleting it changes no assignment.</summary>
    public async Task DeleteRoleAsync(Viewer viewer, Guid id, CancellationToken ct)
    {
        await using var write = await BeginAsync(viewer, ct);
        using var _ = ChangeContext.Use(write.Context());
        var role = await db.AssignmentRoles.FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct) ?? throw ApiException.NotFound("Role");
        var batch = write.NewBatch(DeleteRootKind.Other, "assignment_roles", id);
        await SyncTables.Get("assignment_roles").MarkDeletedAsync(write, role, batch.Id, ct);
        Audit("Role", role.Id, role.Name, "deleted");
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
    }

    private static void ValidateRole(RoleRequest r) =>
        new ApiValidator()
            .Check(r.Name is { } n && n.Trim().Length is >= 1 and <= 100, "name", "1 to 100 characters.")
            .Check(r.Description is null || r.Description.Length <= 500, "description", "At most 500 characters.")
            .Check(r.SortOrder is null or (>= 0 and <= 1_000_000), "sortOrder", "0 to 1,000,000.")
            .ThrowIfInvalid();

    private async Task RequireUniqueRoleAsync(string name, Guid? except, CancellationToken ct)
    {
        var normalized = name.Trim().ToLower();
        if (await db.AssignmentRoles.AnyAsync(x => !x.Deleted && x.Id != except && x.Name.ToLower() == normalized, ct))
        {
            throw ApiException.Conflict("duplicate_name", "A role with this name exists already.");
        }
    }

    public static RoleItem ToItem(AssignmentRoleEntity r) => new(r.Id, r.Name, r.Description, r.SortOrder, r.IsArchived, r.Rev);
}

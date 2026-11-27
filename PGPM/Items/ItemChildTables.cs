using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Config;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Items;

/// <summary>What the tables below an item share: the item decides visibility (read) and changes (edit).</summary>
internal static partial class ItemRules
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    public static partial Regex Color();

    public static ApiValidator CheckKey(this ApiValidator v, string? key, string field) =>
        v.Check(FractionalKey.IsValid(key), field, "A fractional index key (docs/02 §10).");

    /// <summary>The project of a milestone, if the milestone is live.</summary>
    public static async Task<Guid?> ItemOfMilestoneAsync(PgpmDbContext db, Guid milestoneId, CancellationToken ct) =>
        await db.Milestones.AsNoTracking().Where(m => m.Id == milestoneId && !m.Deleted).Select(m => (Guid?)m.ItemId).FirstOrDefaultAsync(ct);

    /// <summary>The project of a milestone for the History, from the context's tracked rows or the database.</summary>
    public static Guid? ItemOfMilestone(PgpmDbContext db, Guid milestoneId) =>
        db.Milestones.Local.FirstOrDefault(m => m.Id == milestoneId)?.ItemId
        ?? db.Milestones.IgnoreQueryFilters().AsNoTracking().Where(m => m.Id == milestoneId).Select(m => (Guid?)m.ItemId).FirstOrDefault();
}

/// <summary>
/// <c>assignments</c> (docs/02 §6): a person on an item or a milestone, once each (a duplicate insert is merged into the
/// existing row). Visible and changed with the item (edit).
/// </summary>
public sealed class AssignmentsTable() : SyncTable<AssignmentEntity>("assignments")
{
    public override IReadOnlyList<ParentRef> Parents { get; } =
    [
        new("items", "itemId", DeletedWithParent: true), new("milestones", "milestoneId", DeletedWithParent: true),
        new("persons", "personId", DeletedWithParent: true),
    ];

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal) { "role", "sortOrder" };

    protected override AssignmentEntity Create() => new();

    public override IQueryable<AssignmentEntity> Visible(IQueryable<AssignmentEntity> rows, Viewer viewer, AccessService access)
    {
        var items = access.ReadableItems(viewer);
        var milestones = access.Db.Milestones.Where(m => items.Any(i => i.Id == m.ItemId));
        return rows.Where(a => (a.ItemId != null && items.Any(i => i.Id == a.ItemId)) || (a.MilestoneId != null && milestones.Any(m => m.Id == a.MilestoneId)));
    }

    protected override Guid? ItemOf(PgpmDbContext db, AssignmentEntity row) =>
        row.ItemId ?? (row.MilestoneId is { } m ? ItemRules.ItemOfMilestone(db, m) : null);

    private static async Task<Guid> ItemIdAsync(SyncWrite w, AssignmentEntity row, CancellationToken ct) =>
        row.ItemId ?? (row.MilestoneId is { } m ? await ItemRules.ItemOfMilestoneAsync(w.Db, m, ct) : null)
        ?? throw new SyncRejection(SyncCodes.ParentMissing, "The milestone doesn't exist.");

    protected override async Task AuthorizeInsertAsync(SyncWrite w, AssignmentEntity row, SyncMutationIn m, CancellationToken ct)
    {
        if ((row.ItemId is null) == (row.MilestoneId is null))
        {
            throw Invalid("itemId", "Either an item or a milestone.");
        }
        await RequireParentItemAsync(w, await ItemIdAsync(w, row, ct), ItemAccess.Edit, ct);
        if (!await w.Db.Persons.AnyAsync(p => p.Id == row.PersonId && !p.Deleted, ct))
        {
            throw new SyncRejection(SyncCodes.ParentMissing, "The person doesn't exist.");
        }
    }

    protected override async Task AuthorizePatchAsync(SyncWrite w, AssignmentEntity row, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        await RequireItemAccessAsync(w, row, await ItemIdAsync(w, row, ct), ItemAccess.Edit, ct);

    protected override async Task AuthorizeDeleteAsync(SyncWrite w, AssignmentEntity row, CancellationToken ct) =>
        await RequireItemAccessAsync(w, row, await ItemIdAsync(w, row, ct), ItemAccess.Edit, ct);

    protected override void Validate(AssignmentEntity row, ApiValidator v) =>
        v.Check(row.Role is null || row.Role.Trim().Length is >= 1 and <= 100, "role", "1 to 100 characters, or none.").CheckKey(row.SortOrder, "sortOrder");

    protected override Expression<Func<AssignmentEntity, bool>> SamePair(AssignmentEntity row)
    {
        if (row.ItemId is { } item)
        {
            return a => a.ItemId == item && a.PersonId == row.PersonId;
        }
        var milestone = row.MilestoneId;
        return a => a.MilestoneId == milestone && a.PersonId == row.PersonId;
    }
}

/// <summary>
/// <c>item_links</c> (docs/02 §6, docs/01 §4.7): belongs to its source item (edit to add or remove, read to see); the target
/// must be readable when the link is made. Deleting either item tombstones the link in that delete's batch.
/// </summary>
public sealed class ItemLinksTable() : SyncTable<ItemLinkEntity>("item_links")
{
    public override IReadOnlyList<ParentRef> Parents { get; } =
        [new("items", "fromItemId", DeletedWithParent: true), new("items", "toItemId", DeletedWithParent: true)];

    protected override ItemLinkEntity Create() => new();

    public override IQueryable<ItemLinkEntity> Visible(IQueryable<ItemLinkEntity> rows, Viewer viewer, AccessService access)
    {
        var items = access.ReadableItems(viewer);
        return rows.Where(l => items.Any(i => i.Id == l.FromItemId));
    }

    protected override Guid? ItemOf(ItemLinkEntity row) => row.FromItemId;

    protected override async Task AuthorizeInsertAsync(SyncWrite w, ItemLinkEntity row, SyncMutationIn m, CancellationToken ct)
    {
        await RequireParentItemAsync(w, row.FromItemId, ItemAccess.Edit, ct);
        if (row.ToItemId != row.FromItemId)
        {
            await RequireParentItemAsync(w, row.ToItemId, ItemAccess.Read, ct);
        }
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, ItemLinkEntity row, CancellationToken ct) =>
        RequireItemAccessAsync(w, row, row.FromItemId, ItemAccess.Edit, ct);

    protected override void Validate(ItemLinkEntity row, ApiValidator v) =>
        v.Check(row.FromItemId != row.ToItemId, "toItemId", "An item doesn't link to itself.");

    protected override Expression<Func<ItemLinkEntity, bool>> SamePair(ItemLinkEntity row) =>
        l => l.FromItemId == row.FromItemId && l.ToItemId == row.ToItemId && l.Relation == row.Relation;
}

/// <summary>
/// <c>item_sections</c> (docs/02 §7.6): the sections an idea or a project shows; the section applies to the item's type.
/// Removing one keeps its values. Visible with the item and the section.
/// </summary>
public sealed class ItemSectionsTable() : SyncTable<ItemSectionEntity>("item_sections")
{
    public override IReadOnlyList<ParentRef> Parents { get; } =
        [new("items", "itemId", DeletedWithParent: true), new("sections", "sectionId", DeletedWithParent: true)];

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal) { "sortOrder" };

    protected override ItemSectionEntity Create() => new();

    public override IQueryable<ItemSectionEntity> Visible(IQueryable<ItemSectionEntity> rows, Viewer viewer, AccessService access)
    {
        var items = access.ReadableItems(viewer);
        var sections = access.VisibleSections(access.Db.Sections, viewer);
        return rows.Where(s => items.Any(i => i.Id == s.ItemId) && sections.Any(x => x.Id == s.SectionId));
    }

    protected override Guid? ItemOf(ItemSectionEntity row) => row.ItemId;

    protected override async Task AuthorizeInsertAsync(SyncWrite w, ItemSectionEntity row, SyncMutationIn m, CancellationToken ct)
    {
        var item = await RequireParentItemAsync(w, row.ItemId, ItemAccess.Edit, ct);
        var section = await w.Access.VisibleSections(w.Db.Sections.AsNoTracking(), w.Viewer).FirstOrDefaultAsync(s => s.Id == row.SectionId && !s.Deleted, ct)
            ?? throw new SyncRejection(SyncCodes.ParentMissing, "The section doesn't exist.");
        if (section.IsArchived || !section.AppliesTo.Contains(item.Type.ToString().ToLowerInvariant()))
        {
            throw Invalid("sectionId", $"The section {(section.IsArchived ? "is archived" : $"doesn't apply to {item.Type.ToString().ToLowerInvariant()}s")}.");
        }
    }

    protected override Task AuthorizePatchAsync(SyncWrite w, ItemSectionEntity row, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        RequireItemAccessAsync(w, row, row.ItemId, ItemAccess.Edit, ct);

    protected override Task AuthorizeDeleteAsync(SyncWrite w, ItemSectionEntity row, CancellationToken ct) =>
        RequireItemAccessAsync(w, row, row.ItemId, ItemAccess.Edit, ct);

    protected override Expression<Func<ItemSectionEntity, bool>> SamePair(ItemSectionEntity row) =>
        s => s.ItemId == row.ItemId && s.SectionId == row.SectionId;
}

/// <summary>
/// <c>field_values</c> (docs/02 §7.5): a value of a field of an idea or project, in the shape of the field's type; the field's
/// section must be visible (to see it) and editable (to change it), as well as the item. A second insert of the same pair
/// becomes a patch of the existing row's <c>value</c> (old = null: may conflict).
/// </summary>
public sealed class FieldValuesTable() : SyncTable<FieldValueEntity>("field_values")
{
    public override IReadOnlyList<ParentRef> Parents { get; } =
        [new("items", "itemId", DeletedWithParent: true), new("field_definitions", "fieldId", DeletedWithParent: true)];

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal) { "value" };

    protected override DuplicateMerge Merge => DuplicateMerge.PatchFromNull;

    protected override IReadOnlyList<string> MergeKeys { get; } = ["value"];

    protected override FieldValueEntity Create() => new();

    public override IQueryable<FieldValueEntity> Visible(IQueryable<FieldValueEntity> rows, Viewer viewer, AccessService access)
    {
        var items = access.ReadableItems(viewer);
        var fields = access.VisibleFields(access.Db.FieldDefinitions, viewer);
        return rows.Where(v => items.Any(i => i.Id == v.ItemId) && fields.Any(f => f.Id == v.FieldId));
    }

    protected override Guid? ItemOf(FieldValueEntity row) => row.ItemId;

    private static async Task<FieldDefinitionEntity> FieldAsync(SyncWrite w, Guid fieldId, CancellationToken ct) =>
        await w.Access.VisibleFields(w.Db.FieldDefinitions.AsNoTracking(), w.Viewer).FirstOrDefaultAsync(f => f.Id == fieldId && !f.Deleted, ct)
        ?? throw new SyncRejection(SyncCodes.ParentMissing, "The field doesn't exist (or you may not see it).");

    private static async Task RequireSectionEditAsync(SyncWrite w, FieldDefinitionEntity field, JsonObject? current, CancellationToken ct)
    {
        if (!(await w.Access.EditableSectionsAsync(w.Viewer, [field.SectionId], ct)).Contains(field.SectionId))
        {
            throw new SyncRejection(SyncCodes.MissingAccess, "The field's section is not editable for you.", current);
        }
    }

    protected override async Task AuthorizeInsertAsync(SyncWrite w, FieldValueEntity row, SyncMutationIn m, CancellationToken ct)
    {
        var item = await RequireParentItemAsync(w, row.ItemId, ItemAccess.Edit, ct);
        var field = await FieldAsync(w, row.FieldId, ct);
        var section = await w.Db.Sections.AsNoTracking().FirstAsync(s => s.Id == field.SectionId, ct);
        if (field.Type == FieldType.Score)
        {
            throw Invalid("fieldId", "Score values are computed, never stored.");
        }
        if (!section.AppliesTo.Contains(item.Type.ToString().ToLowerInvariant()))
        {
            throw Invalid("fieldId", $"The field's section doesn't apply to {item.Type.ToString().ToLowerInvariant()}s.");
        }
        await RequireSectionEditAsync(w, field, null, ct);
    }

    protected override async Task AuthorizePatchAsync(SyncWrite w, FieldValueEntity row, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        await RequireItemAccessAsync(w, row, row.ItemId, ItemAccess.Edit, ct);
        await RequireSectionEditAsync(w, await FieldAsync(w, row.FieldId, ct), await CurrentAsync(w, row, ct), ct);
    }

    protected override async Task AuthorizeDeleteAsync(SyncWrite w, FieldValueEntity row, CancellationToken ct)
    {
        await RequireItemAccessAsync(w, row, row.ItemId, ItemAccess.Edit, ct);
        await RequireSectionEditAsync(w, await FieldAsync(w, row.FieldId, ct), await CurrentAsync(w, row, ct), ct);
    }

    protected override async Task ValidateAsync(SyncWrite w, FieldValueEntity row, IReadOnlyCollection<string>? changed, ApiValidator v, CancellationToken ct)
    {
        var field = await FieldAsync(w, row.FieldId, ct);
        var value = JsonNode.Parse(row.Value);
        var config = JsonNode.Parse(field.Config) as JsonObject;
        if (FieldValueRules.Check(field.Type, config, value, out var persons) is { } error)
        {
            v.Add("value", error);
            return;
        }
        if (field.Required && (value is null || value.GetValueKind() == JsonValueKind.Null || value is JsonValue s && s.GetValueKind() == JsonValueKind.String
                && s.GetValue<string>().Trim().Length == 0 || value is JsonArray { Count: 0 }))
        {
            v.Add("value", "The field is required.");
        }
        if (persons.Count > 0 && await w.Db.Persons.CountAsync(p => persons.Contains(p.Id) && !p.Deleted, ct) != persons.Count)
        {
            v.Add("value", "Unknown person.");
        }
    }

    protected override Expression<Func<FieldValueEntity, bool>> SamePair(FieldValueEntity row) =>
        f => f.ItemId == row.ItemId && f.FieldId == row.FieldId;
}

/// <summary>
/// <c>milestones</c> (docs/02 §8): milestones and gates of a project, changed with edit on it. The server sets
/// <c>completedAt</c> (status done), <c>decidedAt</c>/<c>decidedBy</c> (a decision other than pending); a decision and status
/// suggestions only for gates.
/// </summary>
public sealed class MilestonesTable() : SyncTable<MilestoneEntity>("milestones")
{
    public override IReadOnlyList<ParentRef> Parents { get; } = [new("items", "itemId", DeletedWithParent: true)];

    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal) { "completedAt", "decidedAt", "decidedBy" };

    /// <summary>docs/03 §11.5: everything but the item and the kind (and the server's stamps).</summary>
    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "description", "dueDate", "status", "decision", "decisionNote", "onGoStatusId", "onNoGoStatusId", "color", "sortOrder",
    };

    protected override MilestoneEntity Create() => new();

    public override IQueryable<MilestoneEntity> Visible(IQueryable<MilestoneEntity> rows, Viewer viewer, AccessService access)
    {
        var items = access.ReadableItems(viewer);
        return rows.Where(m => items.Any(i => i.Id == m.ItemId));
    }

    protected override Guid? ItemOf(MilestoneEntity row) => row.ItemId;

    protected override async Task AuthorizeInsertAsync(SyncWrite w, MilestoneEntity row, SyncMutationIn m, CancellationToken ct)
    {
        var item = await RequireParentItemAsync(w, row.ItemId, ItemAccess.Edit, ct);
        if (item.Type != ItemType.Project)
        {
            throw Invalid("itemId", "Milestones belong to projects.");
        }
    }

    protected override Task PrepareInsertAsync(SyncWrite w, MilestoneEntity row, CancellationToken ct)
    {
        row.CompletedAt = row.Status == MilestoneStatus.Done ? Clock.UtcNow : null;
        Decided(w, row);
        return Task.CompletedTask;
    }

    private static void Decided(SyncWrite w, MilestoneEntity row)
    {
        var decided = row.Decision is { } d && d != GateDecision.Pending;
        row.DecidedAt = decided ? Clock.UtcNow : null;
        row.DecidedBy = decided ? w.Viewer.UserId : null;
    }

    protected override Task AuthorizePatchAsync(SyncWrite w, MilestoneEntity row, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        RequireItemAccessAsync(w, row, row.ItemId, ItemAccess.Edit, ct);

    protected override Task AfterPatchAsync(SyncWrite w, MilestoneEntity row, IReadOnlyCollection<string> applied, CancellationToken ct)
    {
        if (applied.Contains("status"))
        {
            row.CompletedAt = row.Status == MilestoneStatus.Done ? row.CompletedAt ?? Clock.UtcNow : null;
        }
        if (applied.Contains("decision"))
        {
            Decided(w, row);
        }
        return Task.CompletedTask;
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, MilestoneEntity row, CancellationToken ct) =>
        RequireItemAccessAsync(w, row, row.ItemId, ItemAccess.Edit, ct);

    protected override void Validate(MilestoneEntity row, ApiValidator v)
    {
        var gate = row.Kind == MilestoneKind.Gate;
        v.Check(!string.IsNullOrWhiteSpace(row.Title) && row.Title.Length <= 300, "title", "1 to 300 characters.")
            .Check(row.Description is null || row.Description.Length <= FieldValueRules.MaxText, "description", "At most 1,000,000 characters.")
            .Check(gate || row.Decision is null, "decision", "Only gates have a decision.")
            .Check(gate || (row.OnGoStatusId is null && row.OnNoGoStatusId is null), "onGoStatusId", "Only gates suggest statuses.")
            .Check(row.DecisionNote is null || row.DecisionNote.Length <= 4000, "decisionNote", "At most 4,000 characters.")
            .Check(row.Color is null || ItemRules.Color().IsMatch(row.Color), "color", "A colour #rrggbb.")
            .CheckKey(row.SortOrder, "sortOrder");
    }

    protected override async Task ValidateAsync(SyncWrite w, MilestoneEntity row, IReadOnlyCollection<string>? changed, ApiValidator v, CancellationToken ct)
    {
        foreach (var (key, id) in new[] { ("onGoStatusId", row.OnGoStatusId), ("onNoGoStatusId", row.OnNoGoStatusId) })
        {
            if (id is { } statusId && (changed is null || changed.Contains(key))
                && !await w.Db.Statuses.AnyAsync(s => s.Id == statusId && s.ItemType == ItemType.Project && !s.Deleted, ct))
            {
                v.Add(key, "A project status.");
            }
        }
    }
}

/// <summary>
/// <c>dod_items</c> (docs/02 §8): a milestone's Definition-of-Done criteria, changed with edit on its project; <c>na</c> needs
/// a note; the server stamps who changed the state when.
/// </summary>
public sealed class DodItemsTable() : SyncTable<DodItemEntity>("dod_items")
{
    public override IReadOnlyList<ParentRef> Parents { get; } = [new("milestones", "milestoneId", DeletedWithParent: true)];

    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal) { "stateChangedBy", "stateChangedAt" };

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal) { "text", "state", "note", "sortOrder" };

    protected override DodItemEntity Create() => new();

    public override IQueryable<DodItemEntity> Visible(IQueryable<DodItemEntity> rows, Viewer viewer, AccessService access)
    {
        var items = access.ReadableItems(viewer);
        var milestones = access.Db.Milestones.Where(m => items.Any(i => i.Id == m.ItemId));
        return rows.Where(d => milestones.Any(m => m.Id == d.MilestoneId));
    }

    protected override Guid? ItemOf(PgpmDbContext db, DodItemEntity row) => ItemRules.ItemOfMilestone(db, row.MilestoneId);

    private static async Task<Guid> ItemIdAsync(SyncWrite w, DodItemEntity row, CancellationToken ct) =>
        await ItemRules.ItemOfMilestoneAsync(w.Db, row.MilestoneId, ct) ?? throw new SyncRejection(SyncCodes.ParentMissing, "The milestone doesn't exist.");

    protected override async Task AuthorizeInsertAsync(SyncWrite w, DodItemEntity row, SyncMutationIn m, CancellationToken ct) =>
        await RequireParentItemAsync(w, await ItemIdAsync(w, row, ct), ItemAccess.Edit, ct);

    protected override Task PrepareInsertAsync(SyncWrite w, DodItemEntity row, CancellationToken ct)
    {
        if (row.State != DodState.Open)
        {
            row.StateChangedAt = Clock.UtcNow;
            row.StateChangedBy = w.Viewer.UserId;
        }
        else
        {
            row.StateChangedAt = null;
            row.StateChangedBy = null;
        }
        return Task.CompletedTask;
    }

    protected override async Task AuthorizePatchAsync(SyncWrite w, DodItemEntity row, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        await RequireItemAccessAsync(w, row, await ItemIdAsync(w, row, ct), ItemAccess.Edit, ct);

    protected override Task AfterPatchAsync(SyncWrite w, DodItemEntity row, IReadOnlyCollection<string> applied, CancellationToken ct)
    {
        if (applied.Contains("state"))
        {
            row.StateChangedAt = Clock.UtcNow;
            row.StateChangedBy = w.Viewer.UserId;
        }
        return Task.CompletedTask;
    }

    protected override async Task AuthorizeDeleteAsync(SyncWrite w, DodItemEntity row, CancellationToken ct) =>
        await RequireItemAccessAsync(w, row, await ItemIdAsync(w, row, ct), ItemAccess.Edit, ct);

    protected override void Validate(DodItemEntity row, ApiValidator v) =>
        v.Check(!string.IsNullOrWhiteSpace(row.Text) && row.Text.Length <= 500, "text", "1 to 500 characters.")
            .Check(row.Note is null || row.Note.Length <= 2000, "note", "At most 2,000 characters.")
            .Check(row.State != DodState.Na || !string.IsNullOrWhiteSpace(row.Note), "note", "n/a needs a note (the evidence).")
            .CheckKey(row.SortOrder, "sortOrder");
}

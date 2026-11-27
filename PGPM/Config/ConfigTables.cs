using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Auth.Rights;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Config;

/// <summary>
/// The configuration tables (docs/02 §7) [S, ro]: written by <c>/api/v1/config/*</c> and the package importer through a
/// <see cref="SyncWrite"/>, delivered to every user (sections and their fields only where visible). Their changes are in the
/// audit log, not in an item's History.
/// </summary>
public abstract class ConfigTable<T>(string name) : SyncTable<T>(name) where T : class, ISyncedEntity
{
    public override bool ReadOnly => true;

    public override bool History => false;

    public override IQueryable<T> Visible(IQueryable<T> rows, Viewer viewer, AccessService access) => rows;
}

/// <summary><c>statuses</c> (docs/02 §7.1).</summary>
public sealed class StatusesTable() : ConfigTable<StatusEntity>("statuses")
{
    protected override StatusEntity Create() => new();
}

/// <summary><c>sections</c> (docs/02 §7.2): only the sections the user may view, with the computed <c>canEdit</c>.</summary>
public sealed class SectionsTable() : ConfigTable<SectionEntity>("sections")
{
    protected override SectionEntity Create() => new();

    public override IQueryable<SectionEntity> Visible(IQueryable<SectionEntity> rows, Viewer viewer, AccessService access) =>
        access.VisibleSections(rows, viewer);

    protected override async Task AddComputedAsync(PullScope scope, IReadOnlyList<(SectionEntity Row, JsonObject Wire)> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var editable = await scope.Access.EditableSectionsAsync(scope.Viewer, rows.Select(r => r.Row.Id).ToList(), ct);
        foreach (var (row, wire) in rows)
        {
            wire["canEdit"] = editable.Contains(row.Id);
        }
    }
}

/// <summary><c>field_definitions</c> (docs/02 §7.4): those of visible sections.</summary>
public sealed class FieldDefinitionsTable() : ConfigTable<FieldDefinitionEntity>("field_definitions")
{
    public override IReadOnlyList<ParentRef> Parents { get; } = [new("sections", "sectionId", DeletedWithParent: true)];

    protected override FieldDefinitionEntity Create() => new();

    public override IQueryable<FieldDefinitionEntity> Visible(IQueryable<FieldDefinitionEntity> rows, Viewer viewer, AccessService access) =>
        access.VisibleFields(rows, viewer);
}

/// <summary><c>project_templates</c> (docs/02 §7.7).</summary>
public sealed class ProjectTemplatesTable() : ConfigTable<ProjectTemplateEntity>("project_templates")
{
    protected override ProjectTemplateEntity Create() => new();
}

public sealed class TemplateStatusesTable() : ConfigTable<TemplateStatusEntity>("template_statuses")
{
    public override IReadOnlyList<ParentRef> Parents { get; } = [new("project_templates", "templateId", DeletedWithParent: true)];

    protected override TemplateStatusEntity Create() => new();
}

public sealed class TemplateSectionsTable() : ConfigTable<TemplateSectionEntity>("template_sections")
{
    public override IReadOnlyList<ParentRef> Parents { get; } = [new("project_templates", "templateId", DeletedWithParent: true)];

    protected override TemplateSectionEntity Create() => new();
}

/// <summary><c>template_field_defaults</c>: those of fields the user may see.</summary>
public sealed class TemplateFieldDefaultsTable() : ConfigTable<TemplateFieldDefaultEntity>("template_field_defaults")
{
    public override IReadOnlyList<ParentRef> Parents { get; } =
        [new("project_templates", "templateId", DeletedWithParent: true), new("field_definitions", "fieldId", DeletedWithParent: true)];

    protected override TemplateFieldDefaultEntity Create() => new();

    public override IQueryable<TemplateFieldDefaultEntity> Visible(IQueryable<TemplateFieldDefaultEntity> rows, Viewer viewer, AccessService access)
    {
        var fields = access.VisibleFields(access.Db.FieldDefinitions, viewer);
        return rows.Where(d => fields.Any(f => f.Id == d.FieldId));
    }
}

public sealed class TemplateMilestonesTable() : ConfigTable<TemplateMilestoneEntity>("template_milestones")
{
    public override IReadOnlyList<ParentRef> Parents { get; } = [new("project_templates", "templateId", DeletedWithParent: true)];

    protected override TemplateMilestoneEntity Create() => new();
}

/// <summary><c>assignment_roles</c> (docs/03 §17): suggestions for <c>assignments.role</c>.</summary>
public sealed class AssignmentRolesTable() : ConfigTable<AssignmentRoleEntity>("assignment_roles")
{
    protected override AssignmentRoleEntity Create() => new();
}

/// <summary>
/// <c>saved_views</c> (docs/02 §7.8) [S]: a user's own views and the shared ones. Anybody saves personal views; shared ones
/// (setting <c>isShared</c>, editing or deleting a shared view) need <i>Configuration: Manage</i>.
/// </summary>
public sealed class SavedViewsTable() : SyncTable<SavedViewEntity>("saved_views")
{
    public override bool History => false;

    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal) { "ownerUserId" };

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "description", "definition", "definition.*", "isShared", "sortOrder",
    };

    protected override SavedViewEntity Create() => new();

    public override IQueryable<SavedViewEntity> Visible(IQueryable<SavedViewEntity> rows, Viewer viewer, AccessService access)
    {
        var me = viewer.UserId;
        return rows.Where(v => v.OwnerUserId == me || v.IsShared);
    }

    private static void RequireShareRight(SyncWrite w)
    {
        if (!w.Viewer.Rights.Has(Area.Configuration, Level.Manage))
        {
            throw new SyncRejection(SyncCodes.MissingRight, $"Shared views need {RightsCatalog.Of(Area.Configuration).Label}: Manage.");
        }
    }

    protected override Task AuthorizeInsertAsync(SyncWrite w, SavedViewEntity row, SyncMutationIn m, CancellationToken ct)
    {
        if (row.IsShared)
        {
            RequireShareRight(w);
        }
        return Task.CompletedTask;
    }

    protected override Task PrepareInsertAsync(SyncWrite w, SavedViewEntity row, CancellationToken ct)
    {
        row.OwnerUserId = w.Viewer.UserId;
        return Task.CompletedTask;
    }

    protected override Task AuthorizePatchAsync(SyncWrite w, SavedViewEntity row, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (row.IsShared || keys.Contains("isShared"))
        {
            RequireShareRight(w);
        }
        else if (row.OwnerUserId != w.Viewer.UserId)
        {
            throw new SyncRejection(SyncCodes.MissingAccess, "Only its owner changes a personal view.");
        }
        return Task.CompletedTask;
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, SavedViewEntity row, CancellationToken ct)
    {
        if (row.IsShared)
        {
            RequireShareRight(w);
        }
        else if (row.OwnerUserId != w.Viewer.UserId)
        {
            throw new SyncRejection(SyncCodes.MissingAccess, "Only its owner deletes a personal view.");
        }
        return Task.CompletedTask;
    }

    protected override void Validate(SavedViewEntity row, ApiValidator v)
    {
        v.Check(!string.IsNullOrWhiteSpace(row.Name) && row.Name.Length <= 100, "name", "1 to 100 characters.")
            .Check(row.Description is null || row.Description.Length <= 500, "description", "At most 500 characters.")
            .Check(IsObject(row.Definition, 100_000), "definition", "A JSON object {search, filters, sort, columns} of at most 100,000 characters.");
    }

    /// <summary>A jsonb column's text is a JSON object (not too long).</summary>
    internal static bool IsObject(string? json, int maxLength)
    {
        if (json is null || json.Length > maxLength)
        {
            return false;
        }
        try
        {
            return JsonNode.Parse(json) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Lookups the item tables share.</summary>
internal static class ConfigLookups
{
    /// <summary>The project statuses a template allows: null when it restricts none (all project statuses).</summary>
    public static async Task<HashSet<Guid>?> AllowedStatusesAsync(Data.PgpmDbContext db, Guid templateId, CancellationToken ct)
    {
        var ids = await db.TemplateStatuses.AsNoTracking().Where(s => s.TemplateId == templateId && !s.Deleted).Select(s => s.StatusId).ToListAsync(ct);
        return ids.Count == 0 ? null : [.. ids];
    }
}

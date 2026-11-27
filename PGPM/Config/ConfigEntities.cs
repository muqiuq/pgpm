using System.Text.Json.Serialization;
using PGPM.Audit;
using PGPM.Items;
using PGPM.Sync;

namespace PGPM.Config;

/// <summary>A status's category (docs/02 §7.1): drives the default filters (<i>hide closed</i>) and the map styling.</summary>
public enum StatusCategory
{
    [JsonStringEnumMemberName("open")] Open,
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("closed")] Closed,
}

/// <summary>The field types of docs/02 §7.4.</summary>
public enum FieldType
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("textarea")] Textarea,
    [JsonStringEnumMemberName("markdown")] Markdown,
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("date")] Date,
    [JsonStringEnumMemberName("datetime")] Datetime,
    [JsonStringEnumMemberName("bool")] Bool,
    [JsonStringEnumMemberName("select")] Select,
    [JsonStringEnumMemberName("multiselect")] Multiselect,
    [JsonStringEnumMemberName("person")] Person,
    [JsonStringEnumMemberName("url")] Url,
    [JsonStringEnumMemberName("email")] Email,
    [JsonStringEnumMemberName("rating")] Rating,
    [JsonStringEnumMemberName("score")] Score,
}

/// <summary>A group's access to a section (docs/02 §7.3): edit implies view.</summary>
public enum SectionAccess
{
    [JsonStringEnumMemberName("view")] View,
    [JsonStringEnumMemberName("edit")] Edit,
}

/// <summary>A milestone or a gate (docs/02 §8).</summary>
public enum MilestoneKind
{
    [JsonStringEnumMemberName("milestone")] Milestone,
    [JsonStringEnumMemberName("gate")] Gate,
}

/// <summary>The lists a saved view belongs to (docs/02 §7.8): the four item types and the gate-decision list.</summary>
public enum ViewItemType
{
    [JsonStringEnumMemberName("note")] Note,
    [JsonStringEnumMemberName("idea")] Idea,
    [JsonStringEnumMemberName("lead")] Lead,
    [JsonStringEnumMemberName("project")] Project,
    [JsonStringEnumMemberName("gate")] Gate,
}

/// <summary>
/// A status of ideas, leads or projects (docs/02 §7.1) [S, ro]: written by <c>/api/v1/config/statuses</c>, archived instead of
/// deleted. One default per item type; names unique per type among the non-archived.
/// </summary>
[Audited]
public sealed class StatusEntity : SyncedEntity
{
    public ItemType ItemType { get; set; }

    public string Name { get; set; } = "";

    public string Color { get; set; } = "#64748b";

    /// <summary>PrimeIcons name, <c>pi-lightbulb</c>.</summary>
    public string? Icon { get; set; }

    public StatusCategory Category { get; set; }

    public int SortOrder { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>Aging: <i>overdue</i> after this many days in the status.</summary>
    public int? MaxDays { get; set; }

    public bool IsArchived { get; set; }
}

/// <summary>
/// A section of the field catalogue (docs/02 §7.2) [S, ro]: delivered only to users who may view it, with the computed
/// <c>canEdit</c>. Who may view and edit it is <see cref="SectionGroupEntity"/> (server-only).
/// </summary>
[Audited]
public sealed class SectionEntity : SyncedEntity
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>⊆ {idea, project}, not empty.</summary>
    public List<string> AppliesTo { get; set; } = [];

    /// <summary>⊆ <see cref="AppliesTo"/>: added to new items of the type (projects: without template).</summary>
    public List<string> DefaultFor { get; set; } = [];

    public int SortOrder { get; set; }

    public bool CollapsedByDefault { get; set; }

    public bool IsArchived { get; set; }
}

/// <summary>
/// A group's access to a section (docs/02 §7.3), server-only. No <c>view</c> rows: visible to everyone with access to the
/// item; no <c>edit</c> rows: editable by everyone with edit on the item; a group's <c>edit</c> row lets it view too.
/// </summary>
[Audited]
public sealed class SectionGroupEntity
{
    public Guid SectionId { get; set; }

    public Guid GroupId { get; set; }

    public SectionAccess Access { get; set; }
}

/// <summary>A field of a section (docs/02 §7.4) [S, ro]: the key is unique and immutable; score values are never stored.</summary>
[Audited]
public sealed class FieldDefinitionEntity : SyncedEntity
{
    public Guid SectionId { get; set; }

    /// <summary><c>^[a-z][a-z0-9_]{1,62}$</c>, immutable.</summary>
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string? HelpText { get; set; }

    public FieldType Type { get; set; }

    /// <summary>jsonb, by type (docs/02 §7.4).</summary>
    [AuditText]
    public string Config { get; set; } = "{}";

    public bool Required { get; set; }

    /// <summary>jsonb: a value in the shape of <c>field_values.value</c>, or null.</summary>
    public string? DefaultValue { get; set; }

    public int SortOrder { get; set; }

    public bool IsArchived { get; set; }
}

/// <summary>A project template (docs/02 §7.7) [S, ro]: applied once, on the client, when a project is created.</summary>
[Audited]
public sealed class ProjectTemplateEntity : SyncedEntity
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public string? Color { get; set; }

    public string? Icon { get; set; }

    /// <summary>An allowed project status.</summary>
    public Guid? DefaultStatusId { get; set; }

    /// <summary>jsonb <c>[{"groupId", "access"}]</c>: preselected in the create dialog.</summary>
    public string DefaultShares { get; set; } = "[]";

    public int SortOrder { get; set; }

    public bool IsArchived { get; set; }
}

/// <summary>A project status a template allows (docs/02 §7.7); none: all project statuses.</summary>
public sealed class TemplateStatusEntity : SyncedEntity
{
    public Guid TemplateId { get; set; }

    public Guid StatusId { get; set; }
}

/// <summary>A section of a template, in order (docs/02 §7.7); it applies to projects.</summary>
public sealed class TemplateSectionEntity : SyncedEntity
{
    public Guid TemplateId { get; set; }

    public Guid SectionId { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>A template's default value of a field (docs/02 §7.7), in the shape of <c>field_values.value</c>.</summary>
public sealed class TemplateFieldDefaultEntity : SyncedEntity
{
    public Guid TemplateId { get; set; }

    public Guid FieldId { get; set; }

    /// <summary>jsonb.</summary>
    public string Value { get; set; } = "null";
}

/// <summary>A milestone or gate a template creates (docs/02 §7.7): due = project start + offset.</summary>
public sealed class TemplateMilestoneEntity : SyncedEntity
{
    public Guid TemplateId { get; set; }

    public MilestoneKind Kind { get; set; }

    public string Title { get; set; } = "";

    public string? Description { get; set; }

    public int? OffsetDays { get; set; }

    /// <summary>jsonb <c>["criterion 1", …]</c>.</summary>
    public string Dod { get; set; } = "[]";

    public Guid? OnGoStatusId { get; set; }

    public Guid? OnNoGoStatusId { get; set; }

    /// <summary>Fractional index (docs/02 §10), as the milestones it becomes.</summary>
    public string SortOrder { get; set; } = "a0";
}

/// <summary>A suggestion for <c>assignments.role</c> (docs/03 §17 "Assignment roles") [S, ro].</summary>
[Audited]
public sealed class AssignmentRoleEntity : SyncedEntity
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsArchived { get; set; }
}

/// <summary>
/// A saved list view (docs/02 §7.8) [S]: personal, or shared with everybody (only <i>Configuration: Manage</i> sets and edits
/// shared ones). Delivered: own and shared views.
/// </summary>
public sealed class SavedViewEntity : SyncedEntity
{
    public Guid OwnerUserId { get; set; }

    public ViewItemType ItemType { get; set; }

    public string Name { get; set; } = "";

    /// <summary>What the view shows, for the view picker (configuration packages have it).</summary>
    public string? Description { get; set; }

    /// <summary>jsonb <c>{search, filters, sort, columns}</c>, the list state's shape.</summary>
    public string Definition { get; set; } = "{}";

    public bool IsShared { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// The stable key of a configuration entity (docs/02 §7.9, docs/03 §17), server-only: re-importing a package updates the
/// entities it knows. <see cref="Hash"/> is the entity's state as last imported, so a dry run tells a local edit apart.
/// </summary>
public sealed class ConfigKeyEntity
{
    /// <summary>The entity's table (wire name): <c>statuses</c>, <c>field_definitions</c>, <c>template_milestones</c>, <c>maps</c>, …</summary>
    public string Entity { get; set; } = "";

    /// <summary><c>field:feasibility_index</c>, <c>template:ai_use_case/g1</c>.</summary>
    public string Key { get; set; } = "";

    public Guid Id { get; set; }

    public string? Package { get; set; }

    /// <summary>SHA-256 of the entity's package form when it was imported last (or exported unchanged).</summary>
    public string? Hash { get; set; }
}

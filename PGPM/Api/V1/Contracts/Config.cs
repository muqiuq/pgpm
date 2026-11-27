using System.Text.Json.Nodes;
using PGPM.Config;
using PGPM.Items;

namespace PGPM.Api.V1.Contracts;

/// <summary><c>POST</c> / <c>PUT /api/v1/config/statuses[/{id}]</c>: the item type only on create (immutable).</summary>
public sealed class StatusRequest
{
    /// <summary>idea, lead or project.</summary>
    public ItemType? ItemType { get; set; }

    public string? Name { get; set; }

    /// <summary>#rrggbb.</summary>
    public string? Color { get; set; }

    /// <summary>PrimeIcons name (<c>pi-lightbulb</c>), or none.</summary>
    public string? Icon { get; set; }

    public StatusCategory? Category { get; set; }

    /// <summary>Aging: overdue after this many days, or none.</summary>
    public int? MaxDays { get; set; }

    /// <summary>true: the type's default from now on (the previous one isn't any more).</summary>
    public bool? IsDefault { get; set; }
}

public sealed record StatusItem(Guid Id, ItemType ItemType, string Name, string Color, string? Icon, StatusCategory Category, int SortOrder,
    bool IsDefault, int? MaxDays, bool IsArchived, int Rev);

/// <summary>
/// <c>PUT /api/v1/config/{statuses|sections|fields|templates}/order</c>: all ids of the list in their new order – statuses of
/// one <see cref="ItemType"/>, fields of one <see cref="SectionId"/>.
/// </summary>
public sealed class OrderRequest
{
    public ItemType? ItemType { get; set; }

    public Guid? SectionId { get; set; }

    public List<Guid>? Ids { get; set; }
}

/// <summary>A group's access to a section: <c>view</c> or <c>edit</c> (edit implies view).</summary>
public sealed class SectionAccessRequest
{
    public Guid? GroupId { get; set; }

    public SectionAccess? Level { get; set; }
}

/// <summary><c>POST</c> / <c>PUT /api/v1/config/sections[/{id}]</c>. <see cref="Access"/> replaces the section's groups (null: unchanged).</summary>
public sealed class SectionRequest
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>⊆ {idea, project}, not empty.</summary>
    public List<string>? AppliesTo { get; set; }

    /// <summary>⊆ appliesTo.</summary>
    public List<string>? DefaultFor { get; set; }

    public bool? CollapsedByDefault { get; set; }

    public List<SectionAccessRequest>? Access { get; set; }
}

public sealed record SectionAccessItem(Guid GroupId, string GroupName, SectionAccess Level);

public sealed record SectionItem(Guid Id, string Name, string? Description, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> DefaultFor,
    int SortOrder, bool CollapsedByDefault, bool IsArchived, IReadOnlyList<SectionAccessItem> Access, int Rev);

/// <summary>
/// <c>POST</c> / <c>PUT /api/v1/config/fields[/{id}]</c>: key and type on create only (immutable; a PUT that names other ones
/// is refused). <see cref="Config"/> by type (docs/02 §7.4); a score's is validated against all fields (docs/03 §16).
/// </summary>
public sealed class FieldRequest
{
    public Guid? SectionId { get; set; }

    /// <summary><c>^[a-z][a-z0-9_]{1,62}$</c>, unique.</summary>
    public string? Key { get; set; }

    public string? Label { get; set; }

    public string? HelpText { get; set; }

    public FieldType? Type { get; set; }

    public JsonObject? Config { get; set; }

    public bool? Required { get; set; }

    /// <summary>A value in the shape of the type's field values, or null.</summary>
    public JsonNode? DefaultValue { get; set; }
}

public sealed record FieldItem(Guid Id, Guid SectionId, string Key, string Label, string? HelpText, FieldType Type, JsonObject Config,
    bool Required, JsonNode? DefaultValue, int SortOrder, bool IsArchived, int Rev);

/// <summary>
/// What a field's delete would remove (<c>GET /api/v1/config/fields/{id}/usage</c>): its values (and on how many items), the
/// scores that use it (a field in use by a score can't be deleted or archived) and the templates with a default for it.
/// </summary>
public sealed record FieldUsage(Guid Id, string Key, int Values, int Items, IReadOnlyList<string> Scores, int Templates);

public sealed class TemplateFieldDefaultRequest
{
    public Guid? FieldId { get; set; }

    public JsonNode? Value { get; set; }
}

public sealed class TemplateShareRequest
{
    public Guid? GroupId { get; set; }

    public ItemAccess? Access { get; set; }
}

/// <summary>A template's milestone or gate: with the <see cref="Id"/> of an existing one it is updated, else created.</summary>
public sealed class TemplateMilestoneRequest
{
    public Guid? Id { get; set; }

    public MilestoneKind? Kind { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    /// <summary>Due = project start + offset (days), 0–3650.</summary>
    public int? OffsetDays { get; set; }

    /// <summary>The Definition of Done, in order.</summary>
    public List<string>? Dod { get; set; }

    /// <summary>Gates: project statuses the template allows.</summary>
    public Guid? OnGoStatusId { get; set; }

    public Guid? OnNoGoStatusId { get; set; }
}

/// <summary>
/// <c>POST</c> / <c>PUT /api/v1/config/templates[/{id}]</c>: the template with everything below it, each list complete and in
/// order (statuses: none = all project statuses).
/// </summary>
public sealed class TemplateRequest
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Color { get; set; }

    public string? Icon { get; set; }

    public Guid? DefaultStatusId { get; set; }

    public List<Guid>? StatusIds { get; set; }

    public List<Guid>? SectionIds { get; set; }

    public List<TemplateFieldDefaultRequest>? FieldDefaults { get; set; }

    public List<TemplateShareRequest>? DefaultShares { get; set; }

    public List<TemplateMilestoneRequest>? Milestones { get; set; }
}

public sealed record TemplateFieldDefaultItem(Guid FieldId, JsonNode? Value);

public sealed record TemplateShareItem(Guid GroupId, ItemAccess Access);

public sealed record TemplateMilestoneItem(Guid Id, MilestoneKind Kind, string Title, string? Description, int? OffsetDays, IReadOnlyList<string> Dod,
    Guid? OnGoStatusId, Guid? OnNoGoStatusId, string SortOrder);

public sealed record TemplateDetail(Guid Id, string Name, string? Description, string? Color, string? Icon, Guid? DefaultStatusId,
    IReadOnlyList<Guid> StatusIds, IReadOnlyList<Guid> SectionIds, IReadOnlyList<TemplateFieldDefaultItem> FieldDefaults,
    IReadOnlyList<TemplateShareItem> DefaultShares, IReadOnlyList<TemplateMilestoneItem> Milestones, int SortOrder, bool IsArchived, int Rev);

/// <summary><c>POST</c> / <c>PUT /api/v1/config/roles[/{id}]</c>: an assignment role (a suggestion).</summary>
public sealed class RoleRequest
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>null: after the others.</summary>
    public int? SortOrder { get; set; }
}

public sealed record RoleItem(Guid Id, string Name, string? Description, int SortOrder, bool IsArchived, int Rev);

/// <summary>
/// <c>POST /api/v1/config/packages/dry-run</c> and <c>…/apply</c>: a package's JSON (<see cref="Package"/>) or the name of a
/// built-in one (<see cref="BuiltIn"/>: <c>core</c>, <c>innovation-ai</c>); <see cref="Overwrite"/> the keys whose local edits
/// the package replaces.
/// </summary>
public sealed class PackageRequest
{
    public JsonObject? Package { get; set; }

    public string? BuiltIn { get; set; }

    public List<string>? Overwrite { get; set; }
}

/// <summary>When and by whom a package was imported last, in which version.</summary>
public sealed record PackageImportState(string Version, DateTime ImportedAt, Guid? ImportedBy, string? ImportedByName, string? Title = null);

/// <summary>A package of <c>GET /api/v1/config/packages</c>: the built-in ones, and others imported.</summary>
public sealed record PackageInfo(string Name, string Title, string? Description, string Version, bool BuiltIn, PackageImportState? Imported);

/// <summary>
/// One entity of a dry run: its key, kind (<c>status</c>, <c>field</c>, …) and name, and what an apply does –
/// <c>create</c>, <c>update</c>, <c>unchanged</c> or <c>conflict</c> (edited here since the last import: kept unless
/// overwritten). <see cref="Adopted"/>: an existing entity with the same natural key, taken over. <see cref="Changed"/>: the
/// members that differ.
/// </summary>
public sealed record PackageDiffEntry(string Key, string Entity, string Name, string Action, bool Adopted, IReadOnlyList<string> Changed);

public sealed record PackageDiffCounts(int Create, int Update, int Unchanged, int Conflict);

/// <summary>
/// A dry run's result. <see cref="NeedsAdmin"/>: the package creates or changes groups (and their rights), which only an
/// admin imports (<see cref="IsAdmin"/>: the user is one).
/// </summary>
public sealed record PackageDiff(string Name, string Version, IReadOnlyList<PackageDiffEntry> Entries, PackageDiffCounts Counts, bool NeedsAdmin,
    bool IsAdmin);

/// <summary>What an apply did: the plan, and how many entities it created, updated, and kept (conflicts not overwritten).</summary>
public sealed record PackageApplyResult(PackageDiff Diff, int Created, int Updated, int Skipped);

using System.Text.Json.Serialization;
using PGPM.Config;
using PGPM.Sync;

namespace PGPM.Items;

/// <summary>The relations between items (docs/02 §6 <c>item_links</c>, docs/01 §4.7).</summary>
public enum LinkRelation
{
    [JsonStringEnumMemberName("contributes_to")] ContributesTo,
    [JsonStringEnumMemberName("learning_of")] LearningOf,
    [JsonStringEnumMemberName("depends_on")] DependsOn,
    [JsonStringEnumMemberName("related")] Related,
}

/// <summary>A milestone's progress (docs/02 §8); <i>overdue</i> is derived.</summary>
public enum MilestoneStatus
{
    [JsonStringEnumMemberName("planned")] Planned,
    [JsonStringEnumMemberName("in_progress")] InProgress,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>A gate's decision (docs/02 §8).</summary>
public enum GateDecision
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("go")] Go,
    [JsonStringEnumMemberName("no_go")] NoGo,
    [JsonStringEnumMemberName("iterate")] Iterate,
    [JsonStringEnumMemberName("pivot")] Pivot,
    [JsonStringEnumMemberName("park")] Park,
}

/// <summary>A Definition-of-Done criterion's state (docs/02 §8): <c>na</c> needs a note.</summary>
public enum DodState
{
    [JsonStringEnumMemberName("open")] Open,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("na")] Na,
}

/// <summary>
/// A person assigned to an item or a milestone (docs/02 §6 <c>assignments</c>), with an optional role; once per item (or
/// milestone): a duplicate insert is merged.
/// </summary>
public sealed class AssignmentEntity : SyncedEntity
{
    public Guid? ItemId { get; set; }

    public Guid? MilestoneId { get; set; }

    public Guid PersonId { get; set; }

    /// <summary>Free text with suggestions (<c>assignment_roles</c>).</summary>
    public string? Role { get; set; }

    /// <summary>Fractional index.</summary>
    public string SortOrder { get; set; } = "a0";
}

/// <summary>A typed link from one item to another (docs/02 §6 <c>item_links</c>): it belongs to its source item.</summary>
public sealed class ItemLinkEntity : SyncedEntity
{
    public Guid FromItemId { get; set; }

    public Guid ToItemId { get; set; }

    public LinkRelation Relation { get; set; }
}

/// <summary>A section an idea or project shows (docs/02 §7.6); removing it keeps the values.</summary>
public sealed class ItemSectionEntity : SyncedEntity
{
    public Guid ItemId { get; set; }

    public Guid SectionId { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>A field's value on an idea or project (docs/02 §7.5): <see cref="Value"/> is the JSON of the field's type.</summary>
public sealed class FieldValueEntity : SyncedEntity
{
    public Guid ItemId { get; set; }

    public Guid FieldId { get; set; }

    /// <summary>jsonb: <c>"text"</c>, <c>12.5</c>, <c>"2026-11-26"</c>, <c>true</c>, <c>"lt3"</c>, <c>["o1"]</c>, <c>["&lt;person&gt;"]</c>, <c>4</c> or <c>"na"</c>.</summary>
    public string Value { get; set; } = "null";
}

/// <summary>A milestone or gate of a project (docs/02 §8).</summary>
public sealed class MilestoneEntity : SyncedEntity
{
    public Guid ItemId { get; set; }

    public MilestoneKind Kind { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Markdown.</summary>
    public string? Description { get; set; }

    public DateOnly? DueDate { get; set; }

    public MilestoneStatus Status { get; set; } = MilestoneStatus.Planned;

    /// <summary>Set by the server when the status becomes done.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gates only.</summary>
    public GateDecision? Decision { get; set; }

    public string? DecisionNote { get; set; }

    /// <summary>Set by the server when a decision other than pending is recorded.</summary>
    public DateTime? DecidedAt { get; set; }

    public Guid? DecidedBy { get; set; }

    public Guid? OnGoStatusId { get; set; }

    public Guid? OnNoGoStatusId { get; set; }

    /// <summary>null: the project's colour.</summary>
    public string? Color { get; set; }

    /// <summary>Fractional index.</summary>
    public string SortOrder { get; set; } = "a0";
}

/// <summary>A Definition-of-Done criterion of a milestone (docs/02 §8).</summary>
public sealed class DodItemEntity : SyncedEntity
{
    public Guid MilestoneId { get; set; }

    public string Text { get; set; } = "";

    public DodState State { get; set; }

    /// <summary>Evidence (a link or text); required for <c>na</c>.</summary>
    public string? Note { get; set; }

    /// <summary>Set by the server on every state change.</summary>
    public Guid? StateChangedBy { get; set; }

    public DateTime? StateChangedAt { get; set; }

    /// <summary>Fractional index.</summary>
    public string SortOrder { get; set; } = "a0";
}

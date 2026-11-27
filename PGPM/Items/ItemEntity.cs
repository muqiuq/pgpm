using System.Text.Json.Serialization;
using PGPM.Sync;

namespace PGPM.Items;

/// <summary>The four kinds of item (docs/02 §6). Phase 2 creates notes only.</summary>
public enum ItemType { Note, Idea, Lead, Project }

/// <summary>Access to an item or map (docs/02 §4.1), ordered: read &lt; edit &lt; manage. Lower case in every JSON.</summary>
public enum ItemAccess
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("read")] Read,
    [JsonStringEnumMemberName("edit")] Edit,
    [JsonStringEnumMemberName("manage")] Manage,
}

/// <summary>
/// An item (docs/02 §6 <c>items</c>): notes, ideas, leads and projects in one table with the full column set, the
/// discriminator <see cref="Type"/> immutable. One class rather than EF's TPH hierarchy (docs/03 §15): the sync reads and
/// writes rows generically by column. Columns of other types stay null (validated per type).
/// </summary>
public sealed class ItemEntity : SyncedEntity
{
    public ItemType Type { get; set; }

    /// <summary>Per-type running number, assigned by the server at the first insert (key <c>N-12</c>).</summary>
    public int? Number { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Markdown.</summary>
    public string? Description { get; set; }

    /// <summary><c>#rrggbb</c>; null: the type's default.</summary>
    public string? Color { get; set; }

    /// <summary>The cover image (files, phase 3).</summary>
    public Guid? ImageFileId { get; set; }

    /// <summary>The creator; transferable (online, manage). No foreign key: an item outlives its owner's account.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Required for ideas, leads and projects (statuses, phase 3).</summary>
    public Guid? StatusId { get; set; }

    /// <summary>Set by the server on every status change (aging).</summary>
    public DateTime? StatusChangedAt { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>A project: the idea or lead it was converted from.</summary>
    public Guid? OriginItemId { get; set; }

    /// <summary>An idea or lead: the project it became.</summary>
    public Guid? ConvertedToItemId { get; set; }

    /// <summary>Notes: pinned notes sort first.</summary>
    public bool Pinned { get; set; }

    public string? LeadOrganisation { get; set; }

    public decimal? LeadValue { get; set; }

    /// <summary>ISO 4217.</summary>
    public string? LeadCurrency { get; set; }

    /// <summary>0–100.</summary>
    public short? LeadProbability { get; set; }

    public DateOnly? LeadDecisionDate { get; set; }

    public string? LeadNextAction { get; set; }

    public DateOnly? LeadNextActionDue { get; set; }

    public string? LeadSource { get; set; }

    public Guid? ProjectTemplateId { get; set; }

    public DateOnly? ProjectStartDate { get; set; }

    public DateOnly? ProjectEndDate { get; set; }

    /// <summary>The display key's prefix: N, I, L, P.</summary>
    public static string Prefix(ItemType type) => type switch
    {
        ItemType.Note => "N",
        ItemType.Idea => "I",
        ItemType.Lead => "L",
        _ => "P",
    };
}

/// <summary>
/// An item shared with a group (docs/02 §4 <c>item_shares</c>): read-only for clients once created – a client inserts
/// shares only with a new item in the same batch; later changes go through <c>PUT /api/v1/items/{id}/shares</c>.
/// Deleting an item keeps its shares: they say who sees the tombstone and who may restore it.
/// </summary>
public sealed class ItemShareEntity : SyncedEntity
{
    public Guid ItemId { get; set; }

    public Guid GroupId { get; set; }

    public ItemAccess Access { get; set; } = ItemAccess.Read;
}

/// <summary>The next display number per item type (docs/02 §6), incremented in the push transaction.</summary>
public sealed class NumberCounterEntity
{
    public string Type { get; set; } = "";

    public int NextValue { get; set; }
}

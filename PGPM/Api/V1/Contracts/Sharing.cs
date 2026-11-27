using System.Text.Json.Nodes;
using PGPM.Items;

namespace PGPM.Api.V1.Contracts;

/// <summary>One share of <c>PUT /api/v1/items/{id}/shares</c> (the full list replaces the item's shares).</summary>
public sealed class ItemShareRequest
{
    public Guid? GroupId { get; set; }

    /// <summary>read, edit or manage.</summary>
    public ItemAccess? Access { get; set; }
}

public sealed record ItemShareItem(Guid Id, Guid GroupId, string GroupName, ItemAccess Access);

/// <summary><c>POST /api/v1/items/{id}/owner</c>.</summary>
public sealed class ItemOwnerRequest
{
    public Guid? UserId { get; set; }
}

/// <summary>A Trash entry: a deleted item (maps in phase 5) the user may manage, with who deleted it when.</summary>
public sealed record TrashItem(Guid BatchId, string RootKind, Guid RootId, string RootType, string Title, DateTime DeletedAt, Guid? DeletedBy,
    string? DeletedByName);

/// <summary>What a restore did: rows un-deleted, rows left in the trash (their parent is gone, or a duplicate exists).</summary>
public sealed record TrashRestoreResult(Guid BatchId, int Restored, int Skipped);

/// <summary><c>GET /api/v1/history?itemId=</c>.</summary>
public sealed class HistoryQuery : PageQuery
{
    public Guid? ItemId { get; set; }
}

/// <summary>
/// A History entry (docs/02 §12): <see cref="Changes"/> is <c>[{"field","old","new"}]</c> (field = wire key), for a
/// <c>share</c> <c>[{"field": "share", "groupId", "old", "new"}]</c>, a <c>conflict</c> <c>{"field","yours","theirs","by"}</c>,
/// a <c>conflict_resolved</c> <c>{"field","resolution"}</c>.
/// </summary>
public sealed record HistoryEntryItem(long Id, DateTime At, Guid? UserId, string? UserName, string Source, string Entity, Guid EntityId,
    string Action, JsonNode? Changes, DateTime? ClientChangedAt);

/// <summary><c>POST</c> / <c>DELETE /api/v1/editing</c>: <c>target</c> is <c>description</c>, <c>title</c>, <c>field:&lt;key&gt;</c> or <c>milestone:&lt;id&gt;</c>.</summary>
public sealed class EditingRequest
{
    public Guid? ItemId { get; set; }

    public string? Target { get; set; }
}

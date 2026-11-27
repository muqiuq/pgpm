using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Items;
using PGPM.Sharing;
using PGPM.Sync;
using PGPM.Sync.Live;

namespace PGPM.Api.V1;

/// <summary>
/// The online parts of items (docs/03 §3.3, §6.3): sharing and the owner. Everything else about items goes through the sync.
/// Access is the item's (manage), not a right.
/// </summary>
[Route("api/v1/items")]
public sealed class ItemsController(SharingService sharing) : V1ControllerBase
{
    /// <summary>The item's shares, all of them: groups added, changed and removed (manage access). Returns the new list.</summary>
    [HttpPut("{id:guid}/shares")]
    [Audit(AuditEvents.ItemSharesChanged)]
    public async Task<IReadOnlyList<ItemShareItem>> SetShares(Guid id, [FromBody] List<ItemShareRequest> shares, CancellationToken ct) =>
        await sharing.SetSharesAsync(await Viewer.OfAsync(HttpContext, ct), id, shares, ct);

    /// <summary>Another active user becomes the owner (manage access).</summary>
    [HttpPost("{id:guid}/owner")]
    [Audit(AuditEvents.ItemOwnerChanged)]
    public async Task<IActionResult> TransferOwner(Guid id, [FromBody] ItemOwnerRequest request, CancellationToken ct)
    {
        await sharing.TransferOwnerAsync(await Viewer.OfAsync(HttpContext, ct), id, request.UserId, ct);
        return NoContent();
    }
}

/// <summary>The Trash (docs/01 §4.9): deleted items the user may manage; restore; permanent delete for admins.</summary>
[Route("api/v1/trash")]
public sealed class TrashController(TrashService trash) : V1ControllerBase
{
    /// <summary>Newest first.</summary>
    [HttpGet]
    public async Task<Paged<TrashItem>> List([FromQuery] PageQuery query, CancellationToken ct) =>
        await trash.ListAsync(await Viewer.OfAsync(HttpContext, ct), query, ct);

    /// <summary>Brings the batch back (manage access to its item): new seqs, so every device gets the rows again.</summary>
    [HttpPost("{batchId:guid}/restore")]
    [Audit(AuditEvents.TrashRestored)]
    public async Task<TrashRestoreResult> Restore(Guid batchId, CancellationToken ct) =>
        await trash.RestoreAsync(await Viewer.OfAsync(HttpContext, ct), batchId, ct);

    /// <summary>Deletes the batch permanently (admins). Devices that may have missed its tombstones reset.</summary>
    [HttpDelete("{batchId:guid}")]
    [RequireAdmin]
    [Audit(AuditEvents.TrashPurged)]
    public async Task<IActionResult> Purge(Guid batchId, CancellationToken ct)
    {
        await trash.PurgeAsync(await Viewer.OfAsync(HttpContext, ct), batchId, ct);
        return NoContent();
    }
}

/// <summary>The History of an item (docs/01 §4.8): every change with who, the source and when; online only.</summary>
[Route("api/v1/history")]
public sealed class HistoryController(PgpmDbContext db) : V1ControllerBase
{
    /// <summary>Newest first; needs read access to the item.</summary>
    [HttpGet]
    public async Task<Paged<HistoryEntryItem>> List([FromQuery] HistoryQuery query, CancellationToken ct)
    {
        var itemId = query.ItemId ?? throw ApiException.Invalid("itemId", "The item.");
        await new AccessService(db).RequireItemAsync(await Viewer.OfAsync(HttpContext, ct), itemId, ItemAccess.Read, ct);
        var entries = db.ChangeLog.AsNoTracking().Where(c => c.ItemId == itemId).OrderByDescending(c => c.At).ThenByDescending(c => c.Id)
            .Select(c => new
            {
                c.Id, c.At, c.UserId, UserName = db.Users.Where(u => u.Id == c.UserId).Select(u => u.DisplayName).FirstOrDefault(), c.Source, c.Entity,
                c.EntityId, c.Action, c.Changes, c.ClientChangedAt,
            });
        var page = await Paging.PageAsync(entries, query, ct);
        return new Paged<HistoryEntryItem>(page.Items.Select(e => new HistoryEntryItem(e.Id, e.At, e.UserId, e.UserName,
            e.Source.ToString().ToLowerInvariant(), e.Entity, e.EntityId, e.Action, JsonNode.Parse(e.Changes), e.ClientChangedAt)).ToList(),
            page.Page, page.PageSize, page.TotalCount);
    }
}

/// <summary>Who else is editing (docs/03 §18.6): a heartbeat every 30 s while an editor is open (edit access), gone after 90 s.</summary>
[Route("api/v1/editing")]
public sealed partial class EditingController(PgpmDbContext db, EditingRegistry editing) : V1ControllerBase
{
    [GeneratedRegex(@"^(description|title|field:[A-Za-z0-9_.-]{1,64}|milestone:[0-9a-fA-F-]{36})$")]
    private static partial Regex TargetPattern();

    [HttpPost]
    [NotAudited("Presence, kept in memory.")]
    public async Task<IActionResult> Start([FromBody] EditingRequest request, CancellationToken ct)
    {
        var (itemId, target) = Validate(request);
        var viewer = await Viewer.OfAsync(HttpContext, ct);
        await new AccessService(db).RequireItemAsync(viewer, itemId, ItemAccess.Edit, ct);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == viewer.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "?";
        editing.Touch(itemId, target, viewer.UserId, name);
        return NoContent();
    }

    [HttpDelete]
    [NotAudited("Presence, kept in memory.")]
    public async Task<IActionResult> Stop([FromBody] EditingRequest request, CancellationToken ct)
    {
        var (itemId, target) = Validate(request);
        editing.End(itemId, target, (await Viewer.OfAsync(HttpContext, ct)).UserId);
        return NoContent();
    }

    private static (Guid ItemId, string Target) Validate(EditingRequest request)
    {
        new ApiValidator()
            .Check(request.ItemId is { } id && id != Guid.Empty, "itemId", "The item.")
            .Check(request.Target is { } t && TargetPattern().IsMatch(t), "target", "description, title, field:<key> or milestone:<id>.")
            .ThrowIfInvalid();
        return (request.ItemId!.Value, request.Target!);
    }
}

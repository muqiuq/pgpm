using System.Linq.Expressions;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Items;

/// <summary>
/// <c>items</c> (docs/02 §6): visible with read access, patched with edit, deleted (to the Trash) with manage. Phase 2
/// creates notes only; the other types' inserts are <c>not_supported</c> until phase 3. The server sets the number, the
/// owner and <c>status_changed_at</c>; <c>access</c> is computed per user.
/// </summary>
public sealed partial class ItemsTable() : SyncTable<ItemEntity>("items")
{
    public override DeleteRootKind DeleteRoot => DeleteRootKind.Item;

    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "number", "ownerUserId", "statusChangedAt", "originItemId", "convertedToItemId",
    };

    /// <summary>docs/03 §11.5: not type, number, owner, origin, conversion, template.</summary>
    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "description", "color", "imageFileId", "statusId", "pinned", "tags",
        "leadOrganisation", "leadValue", "leadCurrency", "leadProbability", "leadDecisionDate", "leadNextAction", "leadNextActionDue", "leadSource",
        "projectStartDate", "projectEndDate",
    };

    /// <summary>The types a client may create now (phase 3 adds ideas, leads and projects).</summary>
    public static readonly IReadOnlySet<ItemType> Creatable = new HashSet<ItemType> { ItemType.Note };

    protected override ItemEntity Create() => new();

    public override IQueryable<ItemEntity> Visible(IQueryable<ItemEntity> rows, Viewer viewer, AccessService access) => access.Readable(rows, viewer);

    protected override Guid? ItemOf(ItemEntity row) => row.Id;

    protected override async Task AddComputedAsync(PullScope scope, IReadOnlyList<(ItemEntity Row, JsonObject Wire)> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var access = await scope.Access.ItemAccessAsync(scope.Viewer, rows.Select(r => r.Row.Id).ToList(), ct);
        foreach (var (row, wire) in rows)
        {
            wire["access"] = access.GetValueOrDefault(row.Id, ItemAccess.None).ToString().ToLowerInvariant();
        }
    }

    protected override Task AuthorizeInsertAsync(SyncWrite w, ItemEntity row, SyncMutationIn m, CancellationToken ct)
    {
        if (!Creatable.Contains(row.Type))
        {
            throw new SyncRejection(SyncCodes.NotSupported, $"Items of type {row.Type.ToString().ToLowerInvariant()} can't be created yet.");
        }
        var area = AreaOf(row.Type);
        if (!w.Viewer.Rights.Has(area, Level.Create))
        {
            throw new SyncRejection(SyncCodes.MissingRight, $"You need {RightsCatalog.Of(area).Label}: Create.");
        }
        return Task.CompletedTask;
    }

    public static Area AreaOf(ItemType type) => type switch
    {
        ItemType.Note => Area.Notes,
        ItemType.Idea => Area.Ideas,
        ItemType.Lead => Area.Leads,
        _ => Area.Projects,
    };

    protected override async Task PrepareInsertAsync(SyncWrite w, ItemEntity row, CancellationToken ct)
    {
        row.OwnerUserId = w.Viewer.UserId;
        row.Number = await NextNumberAsync(w.Db, row.Type, ct);
        row.StatusChangedAt = row.StatusId is null ? null : Clock.UtcNow;
        row.OriginItemId = null;
        row.ConvertedToItemId = null;
    }

    /// <summary>The type's next display number (docs/02 §6 <c>number_counters</c>), in the push's transaction.</summary>
    public static async Task<int> NextNumberAsync(PgpmDbContext db, ItemType type, CancellationToken ct)
    {
        var key = type.ToString().ToLowerInvariant();
        return (await db.Database.SqlQuery<int>($"""
            INSERT INTO number_counters (type, next_value) VALUES ({key}, 2)
            ON CONFLICT (type) DO UPDATE SET next_value = number_counters.next_value + 1
            RETURNING next_value - 1 AS "Value"
            """).ToListAsync(ct)).Single();
    }

    protected override Task AuthorizePatchAsync(SyncWrite w, ItemEntity row, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        RequireItemAccessAsync(w, row, row.Id, ItemAccess.Edit, ct);

    protected override Task AfterPatchAsync(SyncWrite w, ItemEntity row, IReadOnlyCollection<string> applied, CancellationToken ct)
    {
        if (applied.Contains("statusId"))
        {
            row.StatusChangedAt = Clock.UtcNow;
        }
        return Task.CompletedTask;
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, ItemEntity row, CancellationToken ct) =>
        RequireItemAccessAsync(w, row, row.Id, ItemAccess.Manage, ct);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    protected override void Validate(ItemEntity row, ApiValidator v)
    {
        v.Check(!string.IsNullOrWhiteSpace(row.Title) && row.Title.Length <= 300, "title", "1 to 300 characters.")
            .Check(row.Description is null || row.Description.Length <= 1_000_000, "description", "At most 1,000,000 characters.")
            .Check(row.Color is null || ColorPattern().IsMatch(row.Color), "color", "A colour #rrggbb.")
            .Check(row.Tags.Count <= 50 && row.Tags.All(t => t.Trim().Length is >= 1 and <= 50) && row.Tags.Distinct().Count() == row.Tags.Count,
                "tags", "At most 50 different tags of 1 to 50 characters.");
        var isLead = row.Type == ItemType.Lead;
        var isProject = row.Type == ItemType.Project;
        v.Check(row.Type == ItemType.Note ? row.StatusId is null : row.StatusId is not null, "statusId",
                row.Type == ItemType.Note ? "Notes have no status." : "A status is required.")
            .Check(row.Type == ItemType.Note || !row.Pinned, "pinned", "Only notes are pinned.")
            .Check(isLead || (row.LeadOrganisation is null && row.LeadValue is null && row.LeadCurrency is null && row.LeadProbability is null
                && row.LeadDecisionDate is null && row.LeadNextAction is null && row.LeadNextActionDue is null && row.LeadSource is null),
                "type", "Lead fields are for leads.")
            .Check(isProject || (row.ProjectStartDate is null && row.ProjectEndDate is null && row.ProjectTemplateId is null), "type",
                "Project fields are for projects.")
            .Check(row.LeadProbability is null or (>= 0 and <= 100), "leadProbability", "0 to 100.")
            .Check(row.LeadCurrency is null || CurrencyPattern().IsMatch(row.LeadCurrency), "leadCurrency", "An ISO 4217 code (CHF).")
            .Check(row.LeadValue is null || (Math.Abs(row.LeadValue.Value) < 1_000_000_000_000m && decimal.Round(row.LeadValue.Value, 2) == row.LeadValue.Value),
                "leadValue", "Below 1,000,000,000,000, at most two decimals.")
            .Check(row.ProjectStartDate is null || row.ProjectEndDate is null || row.ProjectEndDate >= row.ProjectStartDate, "projectEndDate",
                "Not before the start.");
        foreach (var (key, text) in new[] { ("leadOrganisation", row.LeadOrganisation), ("leadNextAction", row.LeadNextAction), ("leadSource", row.LeadSource) })
        {
            v.Check(text is null || text.Length <= 500, key, "At most 500 characters.");
        }
    }
}

/// <summary>
/// <c>item_shares</c> (docs/02 §4): visible with the item, inserted by a client only together with a new item of the same
/// batch (the create dialog's <i>Share with</i>); changed later through <c>PUT /api/v1/items/{id}/shares</c>. Not deleted
/// with its item: the shares say who sees the tombstone and who may restore it.
/// </summary>
public sealed class ItemSharesTable() : SyncTable<ItemShareEntity>("item_shares")
{
    public override bool History => false;

    /// <summary>Not deleted with the item; deleted with the group (and never restored without it).</summary>
    public override IReadOnlyList<ParentRef> Parents { get; } =
        [new("items", "itemId", DeletedWithParent: false), new("groups", "groupId", DeletedWithParent: true)];

    protected override ItemShareEntity Create() => new();

    public override IQueryable<ItemShareEntity> Visible(IQueryable<ItemShareEntity> rows, Viewer viewer, AccessService access)
    {
        var readable = access.ReadableItems(viewer);
        return rows.Where(s => readable.Any(i => i.Id == s.ItemId));
    }

    protected override Guid? ItemOf(ItemShareEntity row) => row.ItemId;

    protected override async Task AuthorizeInsertAsync(SyncWrite w, ItemShareEntity row, SyncMutationIn m, CancellationToken ct)
    {
        if (!await w.Db.Items.AnyAsync(i => i.Id == row.ItemId && !i.Deleted, ct))
        {
            throw new SyncRejection(SyncCodes.ParentMissing, "The item doesn't exist.");
        }
        if (!await w.InsertedInBatchAsync(w.CurrentBatch, "items", row.ItemId, ct))
        {
            throw new SyncRejection(SyncCodes.MissingAccess,
                "Shares are created with a new item (same batch); later changes go online through PUT /api/v1/items/{id}/shares.");
        }
        if (!await w.Db.Groups.AnyAsync(g => g.Id == row.GroupId, ct))
        {
            throw new SyncRejection(SyncCodes.ParentMissing, "The group doesn't exist.");
        }
    }

    protected override Task AfterInsertAsync(SyncWrite w, ItemShareEntity row, CancellationToken ct)
    {
        w.Log(SyncTables.Get("items"), row.ItemId, row.ItemId, ChangeActions.Share,
            new JsonArray(SharingService.ShareChange(row.GroupId, null, row.Access)));
        return Task.CompletedTask;
    }

    protected override void Validate(ItemShareEntity row, ApiValidator v) =>
        v.Check(row.Access != ItemAccess.None, "access", "read, edit or manage.");

    protected override Expression<Func<ItemShareEntity, bool>> SamePair(ItemShareEntity row) =>
        s => s.ItemId == row.ItemId && s.GroupId == row.GroupId;
}

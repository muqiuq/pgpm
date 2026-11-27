using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Files;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Maps;

/// <summary>
/// <c>maps</c> (docs/02 §9): created with <i>Maps: Create</i> (the creator owns it), visible with read access (templates to
/// everybody), changed with edit, deleted (to the Trash) with manage; templates and the default view need more
/// (<i>Configuration: Manage</i>, manage). <c>access</c> is computed per user.
/// </summary>
public sealed class MapsTable() : SyncTable<MapEntity>("maps")
{
    public override DeleteRootKind DeleteRoot => DeleteRootKind.Map;

    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal) { "ownerUserId" };

    /// <summary>Not the kind, the owner (online, <c>POST /api/v1/maps/{id}/owner</c>) or the template it came from.</summary>
    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "description", "settings", "settings.*", "matrix", "matrix.*", "defaultView", "defaultView.*", "isTemplate", "preview",
    };

    public const int MaxPreviewBytes = 256 * 1024;

    protected override MapEntity Create() => new();

    public override IQueryable<MapEntity> Visible(IQueryable<MapEntity> rows, Viewer viewer, AccessService access) => access.ReadableMaps(rows, viewer);

    protected override Guid? MapOf(MapEntity row) => row.Id;

    protected override async Task AddComputedAsync(PullScope scope, IReadOnlyList<(MapEntity Row, JsonObject Wire)> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var access = await scope.Access.MapAccessAsync(scope.Viewer, rows.Select(r => r.Row.Id).ToList(), ct);
        foreach (var (row, wire) in rows)
        {
            wire["access"] = access.GetValueOrDefault(row.Id, ItemAccess.None).ToString().ToLowerInvariant();
        }
    }

    private static void RequireTemplateRight(SyncWrite w)
    {
        if (!w.Viewer.Rights.Has(Area.Configuration, Level.Manage))
        {
            throw new SyncRejection(SyncCodes.MissingRight, $"Map templates need {RightsCatalog.Of(Area.Configuration).Label}: Manage.");
        }
    }

    protected override async Task AuthorizeInsertAsync(SyncWrite w, MapEntity row, SyncMutationIn m, CancellationToken ct)
    {
        if (!w.Viewer.Rights.Has(Area.Maps, Level.Create))
        {
            throw new SyncRejection(SyncCodes.MissingRight, $"You need {RightsCatalog.Of(Area.Maps).Label}: Create.");
        }
        if (row.IsTemplate)
        {
            RequireTemplateRight(w);
        }
        if (row.TemplateSourceId is { } source && !await w.Access.ReadableMaps(w.Viewer).AnyAsync(x => x.Id == source, ct))
        {
            row.TemplateSourceId = null;
        }
    }

    protected override Task PrepareInsertAsync(SyncWrite w, MapEntity row, CancellationToken ct)
    {
        row.OwnerUserId = w.Viewer.UserId;
        return Task.CompletedTask;
    }

    protected override async Task AuthorizePatchAsync(SyncWrite w, MapEntity row, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        await RequireMapAccessAsync(w, row, row.Id, ItemAccess.Edit, ct);
        if (keys.Contains("isTemplate"))
        {
            RequireTemplateRight(w);
        }
        if (keys.Any(k => k == "defaultView" || k.StartsWith("defaultView.", StringComparison.Ordinal)))
        {
            await RequireMapAccessAsync(w, row, row.Id, ItemAccess.Manage, ct);
        }
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, MapEntity row, CancellationToken ct) =>
        RequireMapAccessAsync(w, row, row.Id, ItemAccess.Manage, ct);

    protected override void Validate(MapEntity row, ApiValidator v) =>
        v.Check(!string.IsNullOrWhiteSpace(row.Name) && row.Name.Length <= 200, "name", "1 to 200 characters.")
            .Check(row.Description is null || row.Description.Length <= 100_000, "description", "At most 100,000 characters.")
            .Check(SavedViewsTable.IsObject(row.Settings, 100_000), "settings", "A JSON object.")
            .Check(row.Kind == MapKind.Matrix ? SavedViewsTable.IsObject(row.Matrix, 100_000) : row.Matrix is null, "matrix",
                "A matrix map has its matrix (a JSON object); a canvas none.")
            .Check(row.DefaultView is null || SavedViewsTable.IsObject(row.DefaultView, 100_000), "defaultView", "A JSON object, or none.")
            .Check(row.Preview is null || row.Preview.Length <= MaxPreviewBytes, "preview", "At most 256 KB.")
            .Check(!row.IsTemplate || row.TemplateSourceId is null || row.TemplateSourceId != row.Id, "templateSourceId", "Not itself.");
}

/// <summary>
/// <c>map_shares</c> (docs/02 §4): as <c>item_shares</c> – inserted by a client only with a new map of the same batch, later
/// through <c>PUT /api/v1/maps/{id}/shares</c>; kept when the map is deleted.
/// </summary>
public sealed class MapSharesTable() : SyncTable<MapShareEntity>("map_shares")
{
    public override bool History => false;

    public override IReadOnlyList<ParentRef> Parents { get; } =
        [new("maps", "mapId", DeletedWithParent: false), new("groups", "groupId", DeletedWithParent: true)];

    protected override MapShareEntity Create() => new();

    public override IQueryable<MapShareEntity> Visible(IQueryable<MapShareEntity> rows, Viewer viewer, AccessService access)
    {
        var maps = access.ReadableMaps(viewer);
        return rows.Where(s => maps.Any(m => m.Id == s.MapId));
    }

    protected override Guid? MapOf(MapShareEntity row) => row.MapId;

    protected override async Task AuthorizeInsertAsync(SyncWrite w, MapShareEntity row, SyncMutationIn m, CancellationToken ct)
    {
        if (!await w.Db.Maps.AnyAsync(x => x.Id == row.MapId && !x.Deleted, ct))
        {
            throw new SyncRejection(SyncCodes.ParentMissing, "The map doesn't exist.");
        }
        if (!await w.InsertedInBatchAsync(w.CurrentBatch, "maps", row.MapId, ct))
        {
            throw new SyncRejection(SyncCodes.MissingAccess,
                "Shares are created with a new map (same batch); later changes go online through PUT /api/v1/maps/{id}/shares.");
        }
        if (!await w.Db.Groups.AnyAsync(g => g.Id == row.GroupId, ct))
        {
            throw new SyncRejection(SyncCodes.ParentMissing, "The group doesn't exist.");
        }
    }

    protected override Task AfterInsertAsync(SyncWrite w, MapShareEntity row, CancellationToken ct)
    {
        w.Log(SyncTables.Get("maps"), row.MapId, null, ChangeActions.Share, new JsonArray(SharingService.ShareChange(row.GroupId, null, row.Access)), row.MapId);
        return Task.CompletedTask;
    }

    protected override void Validate(MapShareEntity row, ApiValidator v) =>
        v.Check(row.Access != ItemAccess.None, "access", "read, edit or manage.");

    protected override Expression<Func<MapShareEntity, bool>> SamePair(MapShareEntity row) =>
        s => s.MapId == row.MapId && s.GroupId == row.GroupId;
}

/// <summary><c>map_layers</c> (docs/02 §9): changed with edit on the map; a map keeps at least one layer.</summary>
public sealed class MapLayersTable() : SyncTable<MapLayerEntity>("map_layers")
{
    public override IReadOnlyList<ParentRef> Parents { get; } = [new("maps", "mapId", DeletedWithParent: true)];

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal) { "name", "sortOrder", "isVisible", "isLocked" };

    protected override MapLayerEntity Create() => new();

    public override IQueryable<MapLayerEntity> Visible(IQueryable<MapLayerEntity> rows, Viewer viewer, AccessService access)
    {
        var maps = access.ReadableMaps(viewer);
        return rows.Where(l => maps.Any(m => m.Id == l.MapId));
    }

    protected override Guid? MapOf(MapLayerEntity row) => row.MapId;

    protected override Task AuthorizeInsertAsync(SyncWrite w, MapLayerEntity row, SyncMutationIn m, CancellationToken ct) =>
        RequireParentMapAsync(w, row.MapId, ItemAccess.Edit, ct);

    protected override Task AuthorizePatchAsync(SyncWrite w, MapLayerEntity row, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        RequireMapAccessAsync(w, row, row.MapId, ItemAccess.Edit, ct);

    protected override async Task AuthorizeDeleteAsync(SyncWrite w, MapLayerEntity row, CancellationToken ct)
    {
        await RequireMapAccessAsync(w, row, row.MapId, ItemAccess.Edit, ct);
        if (!await w.Db.MapLayers.AnyAsync(l => l.MapId == row.MapId && l.Id != row.Id && !l.Deleted, ct))
        {
            throw Invalid("id", "A map keeps at least one layer.", await CurrentAsync(w, row, ct));
        }
    }

    protected override void Validate(MapLayerEntity row, ApiValidator v) =>
        v.Check(!string.IsNullOrWhiteSpace(row.Name) && row.Name.Length <= 100, "name", "1 to 100 characters.")
            .Check(FractionalKey.IsValid(row.SortOrder), "sortOrder", "A fractional index key (docs/02 §10).");
}

/// <summary>
/// <c>map_elements</c> (docs/02 §9): changed with edit on the map. Coordinates finite within ±1,000,000 (x, y rounded to two
/// decimals), sizes above 0; the references fit the kind – an item card names an item the user can read (not on matrix
/// maps, whose cards are computed), a milestone or timeline one of a readable project, an image a file of this map,
/// connector ends elements of this map.
/// </summary>
public sealed class MapElementsTable() : SyncTable<MapElementEntity>("map_elements")
{
    public override IReadOnlyList<ParentRef> Parents { get; } =
    [
        new("maps", "mapId", DeletedWithParent: true), new("map_layers", "layerId", DeletedWithParent: true),
        new("items", "itemId", DeletedWithParent: true), new("milestones", "milestoneId", DeletedWithParent: true),
        new("items", "projectId", DeletedWithParent: true), new("files", "fileId", DeletedWithParent: true),
    ];

    /// <summary>docs/03 §11.5: not the map, the kind or what a card shows.</summary>
    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "layerId", "z", "x", "y", "width", "height", "text", "points", "startElementId", "endElementId", "style", "style.*", "config", "config.*",
    };

    public const double Limit = 1_000_000;

    protected override MapElementEntity Create() => new();

    public override IQueryable<MapElementEntity> Visible(IQueryable<MapElementEntity> rows, Viewer viewer, AccessService access)
    {
        var maps = access.ReadableMaps(viewer);
        return rows.Where(e => maps.Any(m => m.Id == e.MapId));
    }

    protected override Guid? MapOf(MapElementEntity row) => row.MapId;

    protected override async Task AuthorizeInsertAsync(SyncWrite w, MapElementEntity row, SyncMutationIn m, CancellationToken ct)
    {
        var map = await RequireParentMapAsync(w, row.MapId, ItemAccess.Edit, ct);
        if (map.Kind == MapKind.Matrix && row.Kind == ElementKind.Item)
        {
            throw Invalid("kind", "Item cards of a matrix map are computed from its matrix.");
        }
        if (row.ItemId is { } item)
        {
            await RequireReadableItemAsync(w, item, "itemId", null, ct);
        }
    }

    private static async Task RequireReadableItemAsync(SyncWrite w, Guid itemId, string key, ItemType? type, CancellationToken ct)
    {
        var item = await w.Db.Items.AsNoTracking().Where(i => i.Id == itemId && !i.Deleted).Select(i => (ItemType?)i.Type).FirstOrDefaultAsync(ct);
        if (item is null || await w.Access.ItemAccessAsync(w.Viewer, itemId, ct) < ItemAccess.Read)
        {
            throw new SyncRejection(SyncCodes.ParentMissing, "The item doesn't exist (or you may not see it).");
        }
        if (type is { } t && item != t)
        {
            throw Invalid(key, $"A {t.ToString().ToLowerInvariant()}.");
        }
    }

    protected override Task PrepareInsertAsync(SyncWrite w, MapElementEntity row, CancellationToken ct)
    {
        Round(row);
        return Task.CompletedTask;
    }

    private static void Round(MapElementEntity row)
    {
        row.X = double.IsFinite(row.X) ? Math.Round(row.X, 2, MidpointRounding.AwayFromZero) : row.X;
        row.Y = double.IsFinite(row.Y) ? Math.Round(row.Y, 2, MidpointRounding.AwayFromZero) : row.Y;
    }

    protected override Task AuthorizePatchAsync(SyncWrite w, MapElementEntity row, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        RequireMapAccessAsync(w, row, row.MapId, ItemAccess.Edit, ct);

    protected override Task AfterPatchAsync(SyncWrite w, MapElementEntity row, IReadOnlyCollection<string> applied, CancellationToken ct)
    {
        Round(row);
        return Task.CompletedTask;
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, MapElementEntity row, CancellationToken ct) =>
        RequireMapAccessAsync(w, row, row.MapId, ItemAccess.Edit, ct);

    protected override void Validate(MapElementEntity row, ApiValidator v)
    {
        v.Check(double.IsFinite(row.X) && Math.Abs(row.X) <= Limit && double.IsFinite(row.Y) && Math.Abs(row.Y) <= Limit, "x",
                "Finite, within ±1,000,000.")
            .Check(double.IsFinite(row.Width) && row.Width is > 0 and <= Limit && double.IsFinite(row.Height) && row.Height is > 0 and <= Limit, "width",
                "Above 0, at most 1,000,000.")
            .Check(FractionalKey.IsValid(row.Z), "z", "A fractional index key (docs/02 §10).")
            .Check((row.Kind == ElementKind.Item) == (row.ItemId is not null), "itemId", "Item cards (only) name an item.")
            .Check((row.Kind == ElementKind.Milestone) == (row.MilestoneId is not null), "milestoneId", "Milestone elements (only) name a milestone.")
            .Check((row.Kind == ElementKind.Timeline) == (row.ProjectId is not null), "projectId", "Timelines (only) name a project.")
            .Check((row.Kind == ElementKind.Image) == (row.FileId is not null), "fileId", "Images (only) name a file.")
            .Check(row.Text is null || row.Text.Length <= 10_000, "text", "At most 10,000 characters.")
            .Check(row.Kind == ElementKind.Line ? Points(row.Points) : row.Points is null, "points", "Lines (only): [[x, y], [x, y], …], at least two, finite.")
            .Check(SavedViewsTable.IsObject(row.Style, 10_000), "style", "A JSON object.")
            .Check(row.Config is null || SavedViewsTable.IsObject(row.Config, 10_000), "config", "A JSON object, or none.")
            .Check((row.StartElementId is null && row.EndElementId is null) || row.Kind == ElementKind.Line, "startElementId", "Only lines connect.")
            .Check(row.StartElementId != row.Id && row.EndElementId != row.Id, "startElementId", "Not to itself.");
    }

    private static bool Points(string? json)
    {
        try
        {
            return json is { Length: <= 100_000 } && JsonNode.Parse(json) is JsonArray { Count: >= 2 and <= 1000 } points
                && points.All(p => p is JsonArray { Count: 2 } xy && xy.All(c => c?.GetValueKind() == JsonValueKind.Number
                    && double.IsFinite(c.GetValue<double>()) && Math.Abs(c.GetValue<double>()) <= 2 * Limit));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    protected override async Task ValidateAsync(SyncWrite w, MapElementEntity row, IReadOnlyCollection<string>? changed, ApiValidator v, CancellationToken ct)
    {
        bool Changed(string key) => changed is null || changed.Contains(key);
        if (Changed("layerId") && !await w.Db.MapLayers.AnyAsync(l => l.Id == row.LayerId && l.MapId == row.MapId && !l.Deleted, ct))
        {
            v.Add("layerId", "A layer of this map.");
        }
        if (changed is null && row.MilestoneId is { } milestone)
        {
            var project = await w.Db.Milestones.AsNoTracking().Where(m => m.Id == milestone && !m.Deleted).Select(m => (Guid?)m.ItemId).FirstOrDefaultAsync(ct);
            if (project is null || await w.Access.ItemAccessAsync(w.Viewer, project.Value, ct) < ItemAccess.Read)
            {
                v.Add("milestoneId", "A milestone of a project you can read.");
            }
        }
        if (changed is null && row.ProjectId is { } projectId)
        {
            await RequireReadableItemAsync(w, projectId, "projectId", ItemType.Project, ct);
        }
        if (changed is null && row.FileId is { } fileId
            && !await w.Db.Files.AnyAsync(f => f.Id == fileId && f.MapId == row.MapId && !f.Deleted && f.Purpose == FilePurpose.MapImage, ct))
        {
            v.Add("fileId", "A map image of this map.");
        }
        foreach (var (key, end) in new[] { ("startElementId", row.StartElementId), ("endElementId", row.EndElementId) })
        {
            if (end is { } id && Changed(key) && !await w.Db.MapElements.AnyAsync(e => e.Id == id && e.MapId == row.MapId && !e.Deleted, ct))
            {
                v.Add(key, "An element of this map.");
            }
        }
    }
}

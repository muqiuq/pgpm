using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Maps;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// The map tools (docs/03 §7.3): <c>list_maps</c>, <c>get_map</c> (layers, the elements in short; a matrix map's cards
/// computed as the devices compute them – values and scores of the fields the user sees, the quadrants, the tray of items
/// not yet assessed) and <c>add_item_to_map</c> (a card on a canvas, as <i>Add to map…</i> places it). Map access is the
/// token's, capped at read or edit.
/// </summary>
public static class MapTools
{
    public static IReadOnlyList<ToolSpec> All =>
    [
        new("list_maps", "List maps",
            "Maps you may see: canvas boards and matrix maps (items placed by two scores), by kind or text in the name; templates only with " +
            "templates: true. Each with its id, kind, your access, its number of elements and a link.",
            ToolArgs.Schema([
                Choice("kind", "canvas or matrix", ["canvas", "matrix"]),
                Text("text", "Words in the name or description"),
                Bool("templates", "Map templates instead of maps"),
                .. PageArgs]),
            null, Level.None, ToolKind.Read, ListAsync),
        new("get_map", "Get a map",
            "One map: its layers and its elements in short (counts by kind, the item cards with their items, texts). A matrix map also has its " +
            "axes and quadrants and its cards computed: each item's values, position (0–1 on each axis), quadrant; items without both values " +
            "are in the tray (not yet assessed).",
            Schema(Id("id", "The map's id (list_maps)"), Int("limit", "At most this many elements listed, 1 to 500; 200 when left out")),
            null, Level.None, ToolKind.Read, GetAsync),
        new("add_item_to_map", "Add an item to a map",
            "Puts a card of an item you may read on a canvas map you may edit (an item may be on a map any number of times): at x/y, to the right " +
            "of an element (near), or in the middle of the map; on the given layer or the top visible, unlocked one. Matrix maps place their " +
            "items themselves.",
            Schema(
                Id("map", "The map's id (list_maps)"),
                Text("item", ItemTools.ItemArg, required: true),
                Number("x", "The card's left edge (with y)"),
                Number("y", "The card's top edge (with x)"),
                Id("near", "An element of the map (get_map): the card goes to its right", required: false),
                Text("layer", "A layer's id or name (get_map)")),
            null, Level.None, ToolKind.Write, AddItemAsync)
        {
            AuditEvents = [AuditEvents.McpMapChanged],
        },
    ];

    private static JsonObject Schema(params Field[] fields) => ToolArgs.Schema(fields);

    /// <summary>A card's size on a canvas (PGPM.WebUI/src/local/maps.ts <c>CARD_SIZE</c>).</summary>
    public const double CardWidth = 220;
    public const double CardHeight = 92;

    private static async Task<object> ListAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var paging = ToolPaging.From(args);
        var templates = OptionalBool(args, "templates") == true;
        var maps = ctx.Access(db).ReadableMaps(db.Maps.AsNoTracking(), ctx.Viewer).Where(m => !m.Deleted && m.IsTemplate == templates);
        if (OptionalChoice(args, "kind", ["canvas", "matrix"]) is { } kind)
        {
            var wanted = kind == "matrix" ? MapKind.Matrix : MapKind.Canvas;
            maps = maps.Where(m => m.Kind == wanted);
        }
        if (SearchText.Pattern(OptionalText(args, "text", 200)) is { } pattern)
        {
            maps = maps.Where(m => EF.Functions.ILike(m.Name, pattern) || (m.Description != null && EF.Functions.ILike(m.Description, pattern)));
        }
        var page = await paging.ReadAsync(maps.OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.Id), ct);
        var ids = page.Items.Select(m => m.Id).ToList();
        var access = await ctx.Access(db).MapAccessAsync(ctx.Viewer, ids, ct);
        var counts = await db.MapElements.AsNoTracking().Where(e => ids.Contains(e.MapId) && !e.Deleted).GroupBy(e => e.MapId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var owners = await ItemViews.NamesAsync(db, page.Items.Select(m => (Guid?)m.OwnerUserId), ct);
        ctx.Summary = $"{page.Total} maps";
        return new
        {
            items = page.Items.Select(m => new
            {
                id = m.Id, name = m.Name, kind = ToolConfig.EnumText(m.Kind), description = m.Description, isTemplate = m.IsTemplate,
                access = access.GetValueOrDefault(m.Id).ToString().ToLowerInvariant(), elements = counts.GetValueOrDefault(m.Id),
                owner = new { userId = m.OwnerUserId, name = owners.GetValueOrDefault(m.OwnerUserId) }, updatedAt = m.UpdatedAt, rev = m.Rev,
                url = ctx.Link($"maps/{m.Id}"),
            }),
            nextCursor = page.NextCursor,
            total = page.Total,
        };
    }

    /// <summary>A live map the token reads: not found otherwise.</summary>
    public static async Task<MapEntity> FindAsync(ToolContext ctx, PgpmDbContext db, Guid id, CancellationToken ct) =>
        await ctx.Access(db).ReadableMaps(db.Maps.AsNoTracking(), ctx.Viewer).FirstOrDefaultAsync(m => m.Id == id && !m.Deleted, ct)
        ?? throw ToolException.NotFound("map");

    private static async Task<object> GetAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var map = await FindAsync(ctx, db, IdOf(args), ct);
        var limit = Math.Clamp(OptionalInt(args, "limit") ?? 200, 1, 500);
        var access = await ctx.Access(db).MapAccessAsync(ctx.Viewer, map.Id, ct);
        var layers = (await db.MapLayers.AsNoTracking().Where(l => l.MapId == map.Id && !l.Deleted).ToListAsync(ct))
            .OrderBy(l => l.SortOrder, StringComparer.Ordinal).ThenBy(l => l.Id).ToList();
        var elements = (await db.MapElements.AsNoTracking().Where(e => e.MapId == map.Id && !e.Deleted).ToListAsync(ct))
            .OrderBy(e => layers.FindIndex(l => l.Id == e.LayerId)).ThenBy(e => e.Z, StringComparer.Ordinal).ThenBy(e => e.Id).ToList();
        var refs = await ItemDetails.RefsAsync(elements.Select(e => e.ItemId ?? e.ProjectId).OfType<Guid>(), ctx, db, ct);
        var milestoneIds = elements.Select(e => e.MilestoneId).OfType<Guid>().ToList();
        var readable = ctx.Access(db).Readable(db.Items.AsNoTracking(), ctx.Viewer);
        var milestones = await db.Milestones.AsNoTracking().Where(m => milestoneIds.Contains(m.Id) && !m.Deleted && readable.Any(i => i.Id == m.ItemId))
            .ToDictionaryAsync(m => m.Id, ct);
        var json = new JsonObject
        {
            ["id"] = map.Id.ToString(),
            ["name"] = map.Name,
            ["kind"] = ToolConfig.EnumText(map.Kind),
            ["description"] = map.Description,
            ["isTemplate"] = map.IsTemplate,
            ["access"] = access.ToString().ToLowerInvariant(),
            ["rev"] = map.Rev,
            ["url"] = ctx.Link($"maps/{map.Id}"),
            ["layers"] = new JsonArray([.. layers.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id.ToString(), ["name"] = l.Name, ["visible"] = l.IsVisible, ["locked"] = l.IsLocked,
                ["elements"] = elements.Count(e => e.LayerId == l.Id),
            })]),
            ["elementCounts"] = new JsonObject(elements.GroupBy(e => ToolConfig.EnumText(e.Kind)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["elements"] = new JsonArray([.. elements.Take(limit).Select(e =>
            {
                var element = new JsonObject
                {
                    ["id"] = e.Id.ToString(), ["kind"] = ToolConfig.EnumText(e.Kind), ["layerId"] = e.LayerId.ToString(),
                    ["x"] = e.X, ["y"] = e.Y, ["width"] = e.Width, ["height"] = e.Height,
                };
                if ((e.ItemId ?? e.ProjectId) is { } item)
                {
                    element["item"] = refs[item].DeepClone();
                }
                if (e.MilestoneId is { } m)
                {
                    element["milestone"] = milestones.TryGetValue(m, out var milestone)
                        ? new JsonObject { ["id"] = m.ToString(), ["title"] = milestone.Title, ["kind"] = ToolConfig.EnumText(milestone.Kind) }
                        : new JsonObject { ["id"] = m.ToString(), ["access"] = "none" };
                }
                if (e.Text is { Length: > 0 } text)
                {
                    element["text"] = text.Length > 500 ? text[..500] + "…" : text;
                }
                return (JsonNode)element;
            })]),
            ["elementsListed"] = Math.Min(limit, elements.Count),
            ["elementsTotal"] = elements.Count,
        };
        if (map.Kind == MapKind.Matrix)
        {
            var matrix = await MatrixAsync(ctx, db, map.Id, ct, map);
            json["matrix"] = await matrix.DescribeAsync(db, ct);
        }
        ctx.Summary = $"{map.Name}: {elements.Count} elements";
        ctx.Target("map", map.Id, map.Name);
        return json;
    }

    /// <summary>A matrix map the token reads, with its computed placement.</summary>
    public static async Task<MatrixView> MatrixAsync(ToolContext ctx, PgpmDbContext db, Guid mapId, CancellationToken ct, MapEntity? map = null)
    {
        map ??= await FindAsync(ctx, db, mapId, ct);
        if (map.Kind != MapKind.Matrix || map.Matrix is null || JsonNode.Parse(map.Matrix) is not JsonObject matrix)
        {
            throw ToolException.Invalid($"{map.Name} is no matrix map.");
        }
        return await MatrixView.LoadAsync(ctx, db, map, matrix, ct);
    }

    // --- add_item_to_map ----------------------------------------------------------------------------------------------

    private static async Task<object> AddItemAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var map = await FindAsync(ctx, db, IdOf(args, "map"), ct);
        if (map.Kind == MapKind.Matrix)
        {
            throw ToolException.Invalid("A matrix map places its items itself (by their values): set the values with update_item.");
        }
        var item = await ItemViews.FindAsync(TextOf(args, "item", 1, 64), ctx, db, ct);
        var x = OptionalNumber(args, "x");
        var y = OptionalNumber(args, "y");
        if ((x is null) != (y is null))
        {
            throw ToolException.Invalid("x and y go together.");
        }
        var near = OptionalId(args, "near");
        var layerArg = OptionalText(args, "layer", 200);
        var elementId = await ctx.WriteAsync(db, async write =>
        {
            var layers = (await write.Db.MapLayers.AsNoTracking().Where(l => l.MapId == map.Id && !l.Deleted).ToListAsync(ct))
                .OrderBy(l => l.SortOrder, StringComparer.Ordinal).ThenBy(l => l.Id).ToList();
            var layer = layerArg is { Length: > 0 }
                ? layers.FirstOrDefault(l => Guid.TryParse(layerArg, out var lid) && l.Id == lid)
                  ?? layers.FirstOrDefault(l => string.Equals(l.Name.Trim(), layerArg.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw ToolException.NotFound($"layer {layerArg} of {map.Name}")
                : layers.LastOrDefault(l => l.IsVisible && !l.IsLocked) ?? throw ToolException.Invalid("Every layer of the map is hidden or locked: name one.");
            var elements = await write.Db.MapElements.AsNoTracking().Where(e => e.MapId == map.Id && !e.Deleted).ToListAsync(ct);
            double left, top;
            if (near is { } anchorId)
            {
                var anchor = elements.FirstOrDefault(e => e.Id == anchorId) ?? throw ToolException.NotFound($"element {anchorId} of {map.Name}");
                (left, top) = (anchor.X + anchor.Width + 40, anchor.Y);
            }
            else if (x is { } gx && y is { } gy)
            {
                (left, top) = ((double)gx, (double)gy);
            }
            else
            {
                var (cx, cy) = elements.Count == 0 ? (0d, 0d)
                    : ((elements.Min(e => e.X) + elements.Max(e => e.X + e.Width)) / 2, (elements.Min(e => e.Y) + elements.Max(e => e.Y + e.Height)) / 2);
                (left, top) = (cx - CardWidth / 2, cy - CardHeight / 2);
            }
            return await ToolWrites.InsertAsync(write, "map_elements", new JsonObject
            {
                ["mapId"] = map.Id.ToString(),
                ["layerId"] = layer.Id.ToString(),
                ["kind"] = "item",
                ["itemId"] = item.Id.ToString(),
                ["z"] = ItemDetails.NextKey(elements.Where(e => e.LayerId == layer.Id).Select(e => e.Z)),
                ["x"] = Math.Round(left, 2, MidpointRounding.AwayFromZero),
                ["y"] = Math.Round(top, 2, MidpointRounding.AwayFromZero),
                ["width"] = CardWidth,
                ["height"] = CardHeight,
                ["style"] = new JsonObject(),
                ["config"] = new JsonObject { ["size"] = "compact" },
            }, Ids.New(), ct);
        }, ct);
        var element = await db.MapElements.AsNoTracking().SingleAsync(e => e.Id == elementId, ct);
        var key = ItemViews.KeyOf(item);
        ctx.Summary = $"{key} {item.Title} on {map.Name}";
        ctx.Target("map", map.Id, map.Name);
        ctx.Target("item", item.Id, item.Title);
        AuditLog.Target(AuditTarget.Map(map.Id, map.Name));
        AuditLog.Summary($"{key} {item.Title} added to {map.Name}");
        return new
        {
            elementId = element.Id, mapId = map.Id, layerId = element.LayerId, x = element.X, y = element.Y, width = element.Width, height = element.Height,
            item = await ItemDetails.RefAsync(item.Id, ctx, db, ct), url = ctx.Link($"maps/{map.Id}"),
        };
    }
}

/// <summary>Where a matrix map puts an item (PGPM.WebUI/src/maps/matrixLayout.ts, before the nudging apart).</summary>
public sealed record MatrixPlace(decimal X, decimal Y, decimal? Size, double PositionX, double PositionY, double PointX, double PointY, string Quadrant,
    string Label);

/// <summary>
/// A matrix map's layout as a device computes it (docs/03 §13.5, PGPM.WebUI/src/maps/matrixItems.ts and matrixLayout.ts):
/// the items of its filter the user reads, their values on the two axes (number, rating, a select's points, a score – from
/// the values of fields the user sees), the quadrant (right and top are the high sides, the split line counts as high), the
/// position on each axis (0 at min, 1 at max, clamped) and the world point its card aims at in the 2000 × 1400 box; the
/// spiral nudging that keeps cards apart is the device's.
/// </summary>
public sealed class MatrixView
{
    public static readonly IReadOnlyDictionary<string, string> DefaultQuadrants = new Dictionary<string, string>
    {
        ["tr"] = "Star initiatives", ["br"] = "Quick wins", ["tl"] = "Difficult but impactful", ["bl"] = "Limited interest",
    };

    private const double WorldWidth = 2000, WorldHeight = 1400, Padding = 100;

    public sealed record Axis(string? Field, string? Label, double Min, double Max, double? Split)
    {
        public double SplitValue => Split is { } s && double.IsFinite(s) ? s : (Min + Max) / 2;

        public double Position(double value) =>
            !double.IsFinite(Min) || !double.IsFinite(Max) || Max == Min ? 0.5 : Math.Min(1, Math.Max(0, (value - Min) / (Max - Min)));

        public bool High(double value) => (value - SplitValue) * (Max < Min ? -1 : 1) >= 0;

        public static Axis Of(JsonNode? node) => new(Text(node?["field"]), Text(node?["label"]), Number(node?["min"]) ?? double.NaN,
            Number(node?["max"]) ?? double.NaN, Number(node?["split"]));
    }

    private readonly ToolConfig config;
    private readonly HashSet<Guid> candidates;
    private readonly List<ItemEntity> items;
    private Dictionary<Guid, Dictionary<string, JsonElement>>? values;

    private MatrixView(ToolContext ctx, MapEntity map, JsonObject matrix, ToolConfig config, List<ItemEntity> items)
    {
        Ctx = ctx;
        Map = map;
        this.config = config;
        this.items = items;
        candidates = [.. items.Select(i => i.Id)];
        X = Axis.Of(matrix["x"]);
        Y = Axis.Of(matrix["y"]);
        SizeField = Text(matrix["size"]?["field"]);
        Quadrants = DefaultQuadrants.ToDictionary(q => q.Key, q => Text(matrix["quadrants"]?[q.Key]) is { Length: > 0 } label ? label : q.Value);
        Filter = matrix["filter"]?.DeepClone();
    }

    public ToolContext Ctx { get; }

    public MapEntity Map { get; }

    public Axis X { get; }

    public Axis Y { get; }

    public string? SizeField { get; }

    public IReadOnlyDictionary<string, string> Quadrants { get; }

    public JsonNode? Filter { get; }

    public static async Task<MatrixView> LoadAsync(ToolContext ctx, PgpmDbContext db, MapEntity map, JsonObject matrix, CancellationToken ct)
    {
        var filter = matrix["filter"] as JsonObject;
        var types = Texts(filter?["types"]).Select(t => Data.EnumText.TryParse<ItemType>(t, out var type) ? (ItemType?)type : null).OfType<ItemType>().ToList();
        var statuses = GuidList(filter?["statusIds"]);
        var templates = GuidList(filter?["templateIds"]);
        var groups = GuidList(filter?["groupIds"]);
        var query = ctx.Access(db).Readable(db.Items.AsNoTracking(), ctx.Viewer).Where(i => !i.Deleted);
        if (types.Count > 0)
        {
            query = query.Where(i => types.Contains(i.Type));
        }
        if (statuses.Count > 0)
        {
            query = query.Where(i => i.StatusId != null && statuses.Contains(i.StatusId.Value));
        }
        if (templates.Count > 0)
        {
            query = query.Where(i => i.ProjectTemplateId != null && templates.Contains(i.ProjectTemplateId.Value));
        }
        if (groups.Count > 0)
        {
            query = query.Where(i => db.ItemShares.Any(s => s.ItemId == i.Id && !s.Deleted && groups.Contains(s.GroupId)));
        }
        var items = await query.OrderBy(i => i.Id).Take(ItemTools.MaxComputed + 1).ToListAsync(ct);
        if (items.Count > ItemTools.MaxComputed)
        {
            throw ToolException.Invalid($"The matrix shows more than {ItemTools.MaxComputed:N0} items: too many to compute here.");
        }
        return new MatrixView(ctx, map, matrix, await ctx.ConfigAsync(db, ct), items);
    }

    /// <summary>The ids of the items the matrix shows.</summary>
    public IReadOnlySet<Guid> Candidates => candidates;

    /// <summary>Where the item goes; null when it is in the tray (a value missing, or an axis' field the user doesn't see).</summary>
    public MatrixPlace? Place(ItemEntity item, IReadOnlyDictionary<string, JsonElement> itemValues)
    {
        decimal? Value(string? key) => key is not null && config.FieldByKey.GetValueOrDefault(key) is { } field ? config.Numeric(field, itemValues) : null;
        if (Value(X.Field) is not { } x || Value(Y.Field) is not { } y)
        {
            return null;
        }
        var (dx, dy) = ((double)x, (double)y);
        var quadrant = $"{(Y.High(dy) ? 't' : 'b')}{(X.High(dx) ? 'r' : 'l')}";
        var (px, py) = (X.Position(dx), Y.Position(dy));
        return new MatrixPlace(x, y, Value(SizeField), Math.Round(px, 4), Math.Round(py, 4),
            Math.Round(Padding + px * (WorldWidth - 2 * Padding), 2), Math.Round(WorldHeight - Padding - py * (WorldHeight - 2 * Padding), 2),
            quadrant, Quadrants[quadrant]);
    }

    /// <summary>The matrix for <c>get_map</c>: axes, quadrants, cards and tray.</summary>
    public async Task<JsonObject> DescribeAsync(PgpmDbContext db, CancellationToken ct)
    {
        values ??= await config.ValuesAsync(db, candidates, ct);
        var refs = await ItemDetails.RefsAsync(candidates, Ctx, db, ct);
        var cards = new JsonArray();
        var tray = new JsonArray();
        foreach (var item in items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id))
        {
            var reference = refs[item.Id];
            if (Place(item, values[item.Id]) is { } place)
            {
                var card = reference.DeepClone().AsObject();
                card["x"] = place.X;
                card["y"] = place.Y;
                if (place.Size is { } size)
                {
                    card["size"] = size;
                }
                card["position"] = new JsonObject { ["x"] = place.PositionX, ["y"] = place.PositionY };
                card["point"] = new JsonObject { ["x"] = place.PointX, ["y"] = place.PointY };
                card["quadrant"] = place.Quadrant;
                card["quadrantLabel"] = place.Label;
                cards.Add(card);
            }
            else
            {
                tray.Add(reference.DeepClone());
            }
        }
        JsonObject AxisJson(Axis axis) => new()
        {
            ["field"] = axis.Field,
            ["label"] = axis.Label ?? (axis.Field is not null ? config.FieldByKey.GetValueOrDefault(axis.Field)?.Label : null),
            ["visible"] = axis.Field is not null && config.FieldByKey.ContainsKey(axis.Field),
            ["min"] = double.IsFinite(axis.Min) ? axis.Min : null,
            ["max"] = double.IsFinite(axis.Max) ? axis.Max : null,
            ["split"] = double.IsFinite(axis.SplitValue) ? axis.SplitValue : null,
        };
        return new JsonObject
        {
            ["x"] = AxisJson(X),
            ["y"] = AxisJson(Y),
            ["size"] = SizeField,
            ["quadrants"] = new JsonObject(Quadrants.Select(q => KeyValuePair.Create(q.Key, (JsonNode?)q.Value))),
            ["filter"] = Filter?.DeepClone(),
            ["cards"] = cards,
            ["tray"] = tray,
        };
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static double? Number(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static List<string> Texts(JsonNode? node) => node is JsonArray list ? [.. list.Select(Text).OfType<string>()] : [];

    private static List<Guid> GuidList(JsonNode? node) => [.. Texts(node).Select(t => Guid.TryParse(t, out var id) ? (Guid?)id : null).OfType<Guid>()];
}

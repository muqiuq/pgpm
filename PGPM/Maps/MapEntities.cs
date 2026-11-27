using System.Text.Json.Serialization;
using PGPM.Audit;
using PGPM.Items;
using PGPM.Sync;

namespace PGPM.Maps;

/// <summary>A free canvas or a matrix that places items by two field values (docs/02 §9).</summary>
public enum MapKind
{
    [JsonStringEnumMemberName("canvas")] Canvas,
    [JsonStringEnumMemberName("matrix")] Matrix,
}

/// <summary>The kinds of map element (docs/02 §9 <c>map_elements</c>).</summary>
public enum ElementKind
{
    [JsonStringEnumMemberName("item")] Item,
    [JsonStringEnumMemberName("milestone")] Milestone,
    [JsonStringEnumMemberName("timeline")] Timeline,
    [JsonStringEnumMemberName("rect")] Rect,
    [JsonStringEnumMemberName("ellipse")] Ellipse,
    [JsonStringEnumMemberName("line")] Line,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("image")] Image,
}

/// <summary>
/// A map (docs/02 §9) [S]: shared like an item (<see cref="MapShareEntity"/>), the computed <c>access</c> per user; a template
/// (<see cref="IsTemplate"/>, <i>Configuration: Manage</i>) is delivered to everybody.
/// </summary>
public sealed class MapEntity : SyncedEntity
{
    public MapKind Kind { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>No foreign key: a map outlives its owner's account.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>jsonb <c>{background, grid, snap, gridSize, cardBadges}</c>.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>jsonb, matrix maps only (docs/02 §9).</summary>
    public string? Matrix { get; set; }

    /// <summary>jsonb: the default visibility filter.</summary>
    public string? DefaultView { get; set; }

    public bool IsTemplate { get; set; }

    /// <summary>The template the map was made from (informational).</summary>
    public Guid? TemplateSourceId { get; set; }

    /// <summary>A small WebP thumbnail, rendered by the client.</summary>
    public byte[]? Preview { get; set; }
}

/// <summary>A map shared with a group (docs/02 §4 <c>map_shares</c>): read-only for clients once created, as item shares.</summary>
[Audited]
public sealed class MapShareEntity : SyncedEntity
{
    public Guid MapId { get; set; }

    public Guid GroupId { get; set; }

    public ItemAccess Access { get; set; } = ItemAccess.Read;
}

/// <summary>A named layer of a map (docs/02 §9 <c>map_layers</c>); higher <see cref="SortOrder"/> on top.</summary>
public sealed class MapLayerEntity : SyncedEntity
{
    public Guid MapId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Fractional index.</summary>
    public string SortOrder { get; set; } = "a0";

    public bool IsVisible { get; set; } = true;

    public bool IsLocked { get; set; }
}

/// <summary>An element on a map (docs/02 §9 <c>map_elements</c>): an item card (any number per item), a milestone, a timeline, a drawing.</summary>
public sealed class MapElementEntity : SyncedEntity
{
    public Guid MapId { get; set; }

    public Guid LayerId { get; set; }

    public ElementKind Kind { get; set; }

    /// <summary>Fractional index within the layer.</summary>
    public string Z { get; set; } = "a0";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 1;

    public double Height { get; set; } = 1;

    /// <summary><c>kind = item</c>.</summary>
    public Guid? ItemId { get; set; }

    /// <summary><c>kind = milestone</c>.</summary>
    public Guid? MilestoneId { get; set; }

    /// <summary><c>kind = timeline</c>: the project.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary><c>kind = image</c>: a file of the map.</summary>
    public Guid? FileId { get; set; }

    public string? Text { get; set; }

    /// <summary>jsonb, lines: <c>[[x1, y1], [x2, y2], …]</c> relative to x, y.</summary>
    public string? Points { get; set; }

    public Guid? StartElementId { get; set; }

    public Guid? EndElementId { get; set; }

    /// <summary>jsonb, all keys optional (docs/02 §9).</summary>
    public string Style { get; set; } = "{}";

    /// <summary>jsonb, kind-specific.</summary>
    public string? Config { get; set; }
}

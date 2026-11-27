using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PGPM.Auth.Rights;
using PGPM.Items;

namespace PGPM.Mcp.Tools;

/// <summary>
/// A field the item tools read, set at creation and change (<c>create_*</c>, <c>update_item</c>): its argument, the
/// <c>items</c> wire key it writes through the sync's patch, its JSON Schema, and how an argument becomes the wire value.
/// </summary>
public sealed record ItemField(string Arg, string WireKey, ToolArgs.Field Schema, Func<JsonElement, string, JsonNode?> Parse);

/// <summary>
/// An item type as the MCP tools know it (docs/03 §7.3): its name in the tools (<c>note</c>), the page its links go to, the
/// fields only it has, and whether it has statuses. The tools are generic over these: adding a type is a kind here – and
/// <c>create_{name}</c> appears by itself once the sync accepts the type (<see cref="ItemsTable.Creatable"/>).
/// </summary>
public sealed record ItemKind(ItemType Type, string Name, string Page, string Description, IReadOnlyList<ItemField> Fields, bool HasStatus)
{
    public Area Area => ItemsTable.AreaOf(Type);

    public string Key(int? number) => number is { } n ? $"{ItemEntity.Prefix(Type)}-{n}" : ItemEntity.Prefix(Type);

    public bool Creatable => ItemsTable.Creatable.Contains(Type);
}

/// <summary>
/// The item types the tools offer, and the fields they share. Phase 2 has notes only. Phase 3 adds ideas, leads and
/// projects (with their own fields – a lead's organisation, value and next action – and <c>HasStatus</c>, which brings the
/// <c>status</c> filter and field); phase 4 the dynamic fields and ratings.
/// </summary>
public static partial class ItemKinds
{
    public static readonly ItemField Title = new("title", "title", ToolArgs.Text("title", "The title, 1 to 300 characters"),
        (args, name) => JsonValue.Create(ToolArgs.TextOf(args, name, 1, 300)));

    public static readonly ItemField Description = new("description", "description",
        ToolArgs.Text("description", "The text, Markdown; an empty text removes it"),
        (args, name) => ToolArgs.OptionalText(args, name, 1_000_000, trim: false) is { Length: > 0 } text ? JsonValue.Create(text) : null);

    public static readonly ItemField Color = new("color", "color", ToolArgs.Text("color", "A colour #rrggbb; \"none\" for the type's default"),
        (args, name) => ToolArgs.OptionalText(args, name, 16) switch
        {
            null or "" or "none" => null,
            { } c when ColorPattern().IsMatch(c) => JsonValue.Create(c.ToLowerInvariant()),
            _ => throw ToolException.Invalid($"{name} is a colour #rrggbb, or \"none\"."),
        });

    public static readonly ItemField Pinned = new("pinned", "pinned", ToolArgs.Bool("pinned", "Notes: pinned notes come first"),
        (args, name) => JsonValue.Create(ToolArgs.OptionalBool(args, name) ?? false));

    /// <summary>What every item has.</summary>
    public static readonly IReadOnlyList<ItemField> Common = [Title, Description, Color];

    public static readonly ItemKind Note = new(ItemType.Note, "note", "notes",
        "A note: a quick capture or a learning, with a Markdown text; pinned notes come first.", [Pinned], HasStatus: false);

    /// <summary>Every kind the tools know. Phase 3: Idea, Lead and Project here.</summary>
    public static readonly IReadOnlyList<ItemKind> All = [Note];

    public static IReadOnlyList<string> Names => [.. All.Select(k => k.Name)];

    public static ItemKind? Of(ItemType type) => All.FirstOrDefault(k => k.Type == type);

    public static ItemKind? Find(string? name) => All.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every field an <c>update_item</c> may change: the common ones and each kind's (a field once, for every kind that has it).</summary>
    public static IReadOnlyList<ItemField> Updatable => [.. Common, .. All.SelectMany(k => k.Fields).DistinctBy(f => f.Arg)];

    public static bool AnyStatus => All.Any(k => k.HasStatus);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    /// <summary>"N-12" → (Note, 12).</summary>
    [GeneratedRegex("^([A-Za-z])-([0-9]{1,9})$")]
    public static partial Regex KeyPattern();
}

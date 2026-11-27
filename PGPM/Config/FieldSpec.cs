using System.Text.Json;

namespace PGPM.Config;

/// <summary>
/// A field definition as score evaluation and validation need it (data model §7.4): plain records without database types,
/// so the MCP layer, the configuration endpoints and the tests use the same evaluator.
/// </summary>
public sealed record FieldSpec(string Key, string Type, FieldConfig? Config = null, bool IsArchived = false)
{
    public FieldConfig Settings => Config ?? FieldConfig.Empty;

    /// <summary>A definition as JSON: <c>{ "key", "type", "config", "isArchived" }</c>.</summary>
    public static FieldSpec FromJson(JsonElement json) =>
        json.Deserialize<FieldSpec>(FieldConfig.JsonOptions) ?? throw new JsonException("A field definition was null.");
}

/// <summary>
/// <c>field_definitions.config</c> (data model §7.4): the members of every field type, all optional; each type reads its
/// own, unknown members (labels, unit, placeholder, …) are ignored.
/// </summary>
public sealed record FieldConfig
{
    public static readonly FieldConfig Empty = new();

    /// <summary>camelCase, numbers as numbers only (as the TypeScript evaluator reads them).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // number, rating
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }

    // select, multiselect
    public IReadOnlyList<SelectOptionSpec>? Options { get; init; }
    /// <summary>Multiselect in scores: <c>count</c> (default) or <c>sum</c> of the options' points.</summary>
    public string? MultiPoints { get; init; }

    // score
    public string? Op { get; init; }
    public IReadOnlyList<string>? Inputs { get; init; }
    public string? Na { get; init; }
    public IReadOnlyDictionary<string, decimal>? Cap { get; init; }
    public decimal? Scale { get; init; }
    public bool? Normalize { get; init; }
    public decimal? Decimals { get; init; }

    /// <summary>A <c>config</c> column's JSON; anything but an object is an empty configuration.</summary>
    public static FieldConfig FromJson(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object ? json.Deserialize<FieldConfig>(JsonOptions) ?? Empty : Empty;
}

/// <summary>A select option (ids are stable, labels may change); <c>points</c> make it usable in scores.</summary>
public sealed record SelectOptionSpec(string Id, string? Label = null, decimal? Points = null, bool Archived = false);

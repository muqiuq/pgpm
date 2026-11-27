using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Config;
using PGPM.Data;
using PGPM.Items;

namespace PGPM.Mcp.Tools;

/// <summary>
/// The configuration a call works with, as the token's user sees it on a device (docs/03 §7.3: "section visibility
/// applies"): the statuses, the sections the user may view (and which of them they may edit), their field definitions –
/// never those of hidden sections –, so values, scores and matrix positions are computed from what the user's own browser
/// has, and arguments name fields by key or label.
/// </summary>
public sealed class ToolConfig
{
    private ToolConfig(List<StatusEntity> statuses, List<SectionEntity> sections, HashSet<Guid> editable, List<FieldDefinitionEntity> fields)
    {
        Statuses = statuses;
        SectionList = sections;
        Sections = sections.ToDictionary(s => s.Id);
        EditableSections = editable;
        Fields = fields;
        FieldById = fields.ToDictionary(f => f.Id);
        FieldByKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        Specs = fields.ToDictionary(f => f.Key, ConfigService.Spec, StringComparer.Ordinal);
    }

    /// <summary>Every status (archived ones too), in their order per type.</summary>
    public IReadOnlyList<StatusEntity> Statuses { get; }

    /// <summary>The sections the user may view, in their order (archived ones too).</summary>
    public IReadOnlyList<SectionEntity> SectionList { get; }

    public IReadOnlyDictionary<Guid, SectionEntity> Sections { get; }

    /// <summary>Of those, the ones the user may edit (given edit on the item).</summary>
    public IReadOnlySet<Guid> EditableSections { get; }

    /// <summary>The fields of the visible sections (archived ones too), in their sections' order.</summary>
    public IReadOnlyList<FieldDefinitionEntity> Fields { get; }

    public IReadOnlyDictionary<Guid, FieldDefinitionEntity> FieldById { get; }

    public IReadOnlyDictionary<string, FieldDefinitionEntity> FieldByKey { get; }

    /// <summary>The visible fields as the score evaluator takes them, by key.</summary>
    public IReadOnlyDictionary<string, FieldSpec> Specs { get; }

    public static async Task<ToolConfig> LoadAsync(ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var access = ctx.Access(db);
        var statuses = await db.Statuses.AsNoTracking().Where(s => !s.Deleted).OrderBy(s => s.ItemType).ThenBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(ct);
        var sections = await access.VisibleSections(db.Sections.AsNoTracking(), ctx.Viewer).Where(s => !s.Deleted).OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Name).ToListAsync(ct);
        var editable = await access.EditableSectionsAsync(ctx.Viewer, sections.Select(s => s.Id).ToList(), ct);
        var order = sections.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var fields = (await access.VisibleFields(db.FieldDefinitions.AsNoTracking(), ctx.Viewer).Where(f => !f.Deleted).ToListAsync(ct))
            .Where(f => order.ContainsKey(f.SectionId)).OrderBy(f => order[f.SectionId]).ThenBy(f => f.SortOrder).ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();
        return new ToolConfig(statuses, sections, editable, fields);
    }

    // --- statuses -------------------------------------------------------------------------------------------------------

    public StatusEntity? Status(Guid? id) => id is { } s ? Statuses.FirstOrDefault(x => x.Id == s) : null;

    /// <summary>The statuses an item of the type may be given: not archived, in order.</summary>
    public IEnumerable<StatusEntity> StatusesOf(ItemType type) => Statuses.Where(s => s.ItemType == type && !s.IsArchived);

    /// <summary>A status of the type by its id or name (not case-sensitive), not archived.</summary>
    public StatusEntity ResolveStatus(ItemType type, string value)
    {
        var name = value.Trim();
        return StatusesOf(type).FirstOrDefault(s => Guid.TryParse(name, out var id) && s.Id == id)
            ?? StatusesOf(type).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.Invalid(
                $"No {EnumText(type)} status {value}: one of {string.Join(", ", StatusesOf(type).Select(s => s.Name))} (list_config has them).");
    }

    /// <summary>The project statuses a template allows (none listed: all), in order, not archived.</summary>
    public async Task<List<StatusEntity>> AllowedAsync(PgpmDbContext db, Guid? templateId, CancellationToken ct)
    {
        var allowed = templateId is { } t ? await ConfigLookups.AllowedStatusesAsync(db, t, ct) : null;
        return [.. StatusesOf(ItemType.Project).Where(s => allowed is null || allowed.Contains(s.Id))];
    }

    // --- fields ---------------------------------------------------------------------------------------------------------

    /// <summary>The section applies to items of the type.</summary>
    public static bool AppliesTo(SectionEntity section, ItemType type) => section.AppliesTo.Contains(EnumText(type));

    /// <summary>
    /// A field the user sees, for an item of <paramref name="type"/> (null: any type), by key, else by label (not
    /// case-sensitive, unique among the visible ones).
    /// </summary>
    public FieldDefinitionEntity ResolveField(string value, ItemType? type)
    {
        var candidates = Fields.Where(f => type is not { } t || AppliesTo(Sections[f.SectionId], t)).ToList();
        if (candidates.FirstOrDefault(f => f.Key == value) is { } byKey)
        {
            return byKey;
        }
        var byLabel = candidates.Where(f => !f.IsArchived && string.Equals(f.Label.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return byLabel.Count switch
        {
            1 => byLabel[0],
            0 => throw new ToolException("not_found",
                $"No field {value}{(type is { } it ? $" for {EnumText(it)}s" : "")}, or you may not see it: give a key from list_config."),
            _ => throw ToolException.Invalid($"Several fields are labelled {value}: give the key ({string.Join(", ", byLabel.Select(f => f.Key))})."),
        };
    }

    /// <summary>The values of the visible fields of these items, by item and field key.</summary>
    public async Task<Dictionary<Guid, Dictionary<string, JsonElement>>> ValuesAsync(PgpmDbContext db, IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        var fieldIds = Fields.Select(f => f.Id).ToList();
        var rows = ids.Count == 0 || fieldIds.Count == 0 ? []
            : await db.FieldValues.AsNoTracking().Where(v => ids.Contains(v.ItemId) && fieldIds.Contains(v.FieldId) && !v.Deleted)
                .Select(v => new { v.ItemId, v.FieldId, v.Value }).ToListAsync(ct);
        var result = ids.ToDictionary(id => id, _ => new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        foreach (var row in rows)
        {
            using var doc = JsonDocument.Parse(row.Value);
            result[row.ItemId][FieldById[row.FieldId].Key] = doc.RootElement.Clone();
        }
        return result;
    }

    /// <summary>
    /// A field's number, as a matrix axis or a range reads it (the client's <c>numericValue</c>): a number or rating as it
    /// is (n/a: none), a select option's points, a score computed (docs/03 §16) from the visible values; nothing else.
    /// </summary>
    public decimal? Numeric(FieldDefinitionEntity field, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (field.Type == FieldType.Score)
        {
            return ScoreEvaluator.Evaluate(Specs[field.Key], Specs, values);
        }
        if (!values.TryGetValue(field.Key, out var value))
        {
            return null;
        }
        return field.Type switch
        {
            FieldType.Number or FieldType.Rating => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) ? n : null,
            FieldType.Select => value.ValueKind == JsonValueKind.String
                ? Specs[field.Key].Settings.Options?.FirstOrDefault(o => o.Id == value.GetString())?.Points : null,
            _ => null,
        };
    }

    /// <summary>
    /// A field with its value for an answer: <c>{key, label, type, value}</c>, a select's option label (<c>text</c>), a
    /// rating's or score's maximum, a score computed.
    /// </summary>
    public JsonObject Describe(FieldDefinitionEntity field, IReadOnlyDictionary<string, JsonElement> values, IReadOnlyDictionary<Guid, string> persons)
    {
        var spec = Specs[field.Key];
        var json = new JsonObject { ["key"] = field.Key, ["label"] = field.Label, ["type"] = EnumText(field.Type) };
        if (field.Type == FieldType.Score)
        {
            json["value"] = Numeric(field, values) is { } score ? JsonValue.Create(score) : null;
            json["max"] = ScoreEvaluator.Max(spec) is { } max ? JsonValue.Create(max) : null;
            return json;
        }
        var value = values.TryGetValue(field.Key, out var v) ? JsonNode.Parse(v.GetRawText()) : null;
        json["value"] = value;
        switch (field.Type)
        {
            case FieldType.Rating:
                json["max"] = spec.Settings.Max ?? ScoreEvaluator.DefaultRatingMax;
                break;
            case FieldType.Select when value is JsonValue s && s.GetValueKind() == JsonValueKind.String:
                json["text"] = spec.Settings.Options?.FirstOrDefault(o => o.Id == s.GetValue<string>())?.Label;
                break;
            case FieldType.Multiselect when value is JsonArray list:
                json["text"] = string.Join(", ", list.Select(i => i?.GetValueKind() == JsonValueKind.String
                    ? spec.Settings.Options?.FirstOrDefault(o => o.Id == i.GetValue<string>())?.Label ?? i.GetValue<string>() : ""));
                break;
            case FieldType.Person when value is JsonArray people:
                json["text"] = string.Join(", ", people.Select(i => i?.GetValueKind() == JsonValueKind.String && Guid.TryParse(i.GetValue<string>(), out var p)
                    ? persons.GetValueOrDefault(p, p.ToString()) : ""));
                break;
        }
        return json;
    }

    /// <summary>
    /// An argument's value as the field stores it (docs/02 §7.5): a rating 0…max or "na", a select or multiselect option by
    /// id or label, a person by id or e-mail (or "me"), a date yyyy-MM-dd, a timestamp as UTC; null removes the value. The
    /// sync checks it once more (<see cref="FieldValueRules"/>).
    /// </summary>
    public async Task<JsonNode?> ParseAsync(PgpmDbContext db, ToolContext ctx, FieldDefinitionEntity field, JsonElement arg, CancellationToken ct)
    {
        if (arg.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var spec = Specs[field.Key];
        string Name() => $"{field.Key} ({field.Label})";
        JsonNode? node;
        switch (field.Type)
        {
            case FieldType.Score:
                throw ToolException.Invalid($"{Name()} is a score: it is computed from its inputs, never set.");
            case FieldType.Rating when arg.ValueKind == JsonValueKind.String:
            {
                var text = arg.GetString()!.Trim().ToLowerInvariant();
                node = text is "na" or "n/a" ? JsonValue.Create("na")
                    : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating) ? JsonValue.Create(rating)
                    : throw ToolException.Invalid($"{Name()} is a rating: a whole number or \"na\".");
                break;
            }
            case FieldType.Select when arg.ValueKind == JsonValueKind.String:
                node = JsonValue.Create(OptionId(spec, arg.GetString()!, Name()));
                break;
            case FieldType.Multiselect when arg.ValueKind is JsonValueKind.String or JsonValueKind.Array:
            {
                var picked = arg.ValueKind == JsonValueKind.String ? [arg.GetString()!]
                    : arg.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()! : throw ToolException.Invalid($"{Name()} takes texts."))
                        .ToList();
                node = new JsonArray([.. picked.Select(p => (JsonNode)OptionId(spec, p, Name())).DistinctBy(n => n.GetValue<string>())]);
                break;
            }
            case FieldType.Person when arg.ValueKind is JsonValueKind.String or JsonValueKind.Array:
            {
                var given = arg.ValueKind == JsonValueKind.String ? [arg.GetString()!]
                    : arg.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()! : throw ToolException.Invalid($"{Name()} takes persons."))
                        .ToList();
                var ids = new List<Guid>();
                foreach (var person in given)
                {
                    ids.Add(await PersonIdAsync(db, ctx, person, ct));
                }
                node = new JsonArray([.. ids.Distinct().Select(i => (JsonNode)i.ToString())]);
                break;
            }
            case FieldType.Datetime when arg.ValueKind == JsonValueKind.String:
                node = DateTime.TryParse(arg.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
                    ? JsonValue.Create(at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                    : throw ToolException.Invalid($"{Name()} is a timestamp (2026-11-27T09:00:00Z).");
                break;
            default:
                node = JsonNode.Parse(arg.GetRawText());
                break;
        }
        if (node is JsonValue text0 && text0.GetValueKind() == JsonValueKind.String && text0.GetValue<string>().Length == 0
            || node is JsonArray { Count: 0 })
        {
            return null;
        }
        if (FieldValueRules.Check(field.Type, JsonNode.Parse(field.Config) as JsonObject, node, out _) is { } error)
        {
            throw ToolException.Invalid($"{Name()}: {error}");
        }
        return node;
    }

    private static string OptionId(FieldSpec spec, string value, string name)
    {
        var options = spec.Settings.Options ?? [];
        return options.FirstOrDefault(o => o.Id == value)?.Id
            ?? options.FirstOrDefault(o => !o.Archived && string.Equals(o.Label?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw ToolException.Invalid($"{name} has no option {value}: one of {string.Join(", ", options.Where(o => !o.Archived).Select(o => o.Label ?? o.Id))}.");
    }

    /// <summary>A person by id or e-mail, or "me" (the token's user's person).</summary>
    public static async Task<Guid> PersonIdAsync(PgpmDbContext db, ToolContext ctx, string value, CancellationToken ct)
    {
        var text = value.Trim();
        var persons = db.Persons.AsNoTracking().Where(p => !p.Deleted);
        Guid? id = text.Equals("me", StringComparison.OrdinalIgnoreCase)
            ? await persons.Where(p => p.UserId == ctx.Viewer.UserId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct)
            : Guid.TryParse(text, out var given) ? await persons.Where(p => p.Id == given).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct)
            : await persons.Where(p => p.Email == text.ToLowerInvariant()).OrderByDescending(p => p.IsActive).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        return id ?? throw ToolException.NotFound($"person {value} (search_persons has the ids)");
    }

    public static async Task<Dictionary<Guid, string>> PersonNamesAsync(PgpmDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? []
            : await db.Persons.AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    public static string EnumText<T>(T value) where T : struct, Enum => Data.EnumText.Of(value);
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PGPM.Config;

/// <summary>
/// The shape of a field's value by its type (docs/02 §7.5), and of a field's configuration (§7.4): the sync push checks
/// <c>field_values</c> with it, the configuration endpoints default values and template defaults, the package importer
/// its packages. Values of <c>score</c> fields are never stored.
/// </summary>
public static partial class FieldValueRules
{
    public const int MaxText = 1_000_000;

    /// <summary>Why <paramref name="value"/> doesn't fit a field of this type and configuration; null when it does (JSON null always does).</summary>
    public static string? Check(FieldType type, JsonObject? config, JsonNode? value, out IReadOnlyList<Guid> personIds)
    {
        personIds = [];
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }
        var kind = value.GetValueKind();
        switch (type)
        {
            case FieldType.Score:
                return "Score values are computed, never stored.";
            case FieldType.Text or FieldType.Textarea or FieldType.Markdown or FieldType.Url or FieldType.Email:
            {
                if (kind != JsonValueKind.String)
                {
                    return "A text.";
                }
                var text = value.GetValue<string>();
                var max = type == FieldType.Text ? Int(config, "maxLength") ?? 10_000 : MaxText;
                if (text.Length > max)
                {
                    return $"At most {max:N0} characters.";
                }
                if (type == FieldType.Url && text.Length > 0
                    && !(Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"))
                {
                    return "An http(s) or mailto address.";
                }
                if (type == FieldType.Email && text.Length > 0 && !EmailPattern().IsMatch(text))
                {
                    return "An e-mail address.";
                }
                return null;
            }
            case FieldType.Number:
            {
                if (!Sync.WireJson.TryDecimal(value, out var number))
                {
                    return "A number.";
                }
                if (Dec(config, "min") is { } min && number < min || Dec(config, "max") is { } max && number > max)
                {
                    return "Outside the field's min/max.";
                }
                return null;
            }
            case FieldType.Date:
                return kind == JsonValueKind.String && DateOnly.TryParseExact(value.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _) ? null : "A date yyyy-MM-dd.";
            case FieldType.Datetime:
                return kind == JsonValueKind.String && DateTimePattern().IsMatch(value.GetValue<string>())
                    && DateTime.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _)
                    ? null : "A UTC timestamp yyyy-MM-ddTHH:mm:ssZ.";
            case FieldType.Bool:
                return kind is JsonValueKind.True or JsonValueKind.False ? null : "true or false.";
            case FieldType.Select:
                return kind == JsonValueKind.String && OptionIds(config).Contains(value.GetValue<string>()) ? null : "One of the field's options (its id).";
            case FieldType.Multiselect:
            {
                if (value is not JsonArray list || list.Any(v => v?.GetValueKind() != JsonValueKind.String))
                {
                    return "A list of the field's option ids.";
                }
                var ids = list.Select(v => v!.GetValue<string>()).ToList();
                var options = OptionIds(config);
                return ids.Distinct().Count() != ids.Count ? "Each option once." : ids.All(options.Contains) ? null : "Unknown option.";
            }
            case FieldType.Person:
            {
                if (value is not JsonArray list || list.Any(v => v?.GetValueKind() != JsonValueKind.String || !Guid.TryParse(v.GetValue<string>(), out _)))
                {
                    return "A list of person ids.";
                }
                var ids = list.Select(v => Guid.Parse(v!.GetValue<string>())).ToList();
                if (ids.Distinct().Count() != ids.Count)
                {
                    return "Each person once.";
                }
                if (ids.Count > 1 && config?["multiple"]?.GetValueKind() != JsonValueKind.True)
                {
                    return "One person (the field takes one).";
                }
                if (ids.Count > 100)
                {
                    return "At most 100 persons.";
                }
                personIds = ids;
                return null;
            }
            case FieldType.Rating:
            {
                var allowNa = config?["allowNa"]?.GetValueKind() == JsonValueKind.True;
                if (kind == JsonValueKind.String && value.GetValue<string>() == "na")
                {
                    return allowNa ? null : "n/a is not allowed for this field.";
                }
                var min = Int(config, "min") ?? 0;
                var max = Int(config, "max") ?? 5;
                return Sync.WireJson.TryDecimal(value, out var rating) && rating == decimal.Truncate(rating)
                    && rating >= min && rating <= max
                    ? null : $"An integer from {min} to {max}{(allowNa ? " or \"na\"" : "")}.";
            }
            default:
                return "Unknown field type.";
        }
    }

    /// <summary>
    /// The problems of a field's configuration by its type (docs/02 §7.4; scores are checked by
    /// <see cref="ScoreConfigValidator"/> too): options with unique ids, ranges in order, known members' types.
    /// </summary>
    public static IReadOnlyList<string> CheckConfig(FieldType type, JsonNode? configNode)
    {
        var errors = new List<string>();
        if (configNode is not JsonObject config)
        {
            return ["The configuration is a JSON object."];
        }
        if (config["subheading"] is { } sub && (sub.GetValueKind() != JsonValueKind.String || sub.GetValue<string>().Length is 0 or > 100))
        {
            errors.Add("subheading: 1 to 100 characters.");
        }
        switch (type)
        {
            case FieldType.Text:
                if (config["maxLength"] is { } maxLength && Int(config, "maxLength") is not (>= 1 and <= 10_000))
                {
                    errors.Add($"maxLength: 1 to 10,000 ({maxLength.ToJsonString()}).");
                }
                break;
            case FieldType.Number:
                if (config["min"] is { } min && min.GetValueKind() is not (JsonValueKind.Number or JsonValueKind.Null)
                    || config["max"] is { } max && max.GetValueKind() is not (JsonValueKind.Number or JsonValueKind.Null))
                {
                    errors.Add("min and max are numbers.");
                }
                else if (Dec(config, "min") is { } lo && Dec(config, "max") is { } hi && lo > hi)
                {
                    errors.Add("min is above max.");
                }
                if (config["decimals"] is not null && Int(config, "decimals") is not (>= 0 and <= 6))
                {
                    errors.Add("decimals: 0 to 6.");
                }
                break;
            case FieldType.Select or FieldType.Multiselect:
            {
                if (config["options"] is not JsonArray options || options.Count == 0)
                {
                    errors.Add("options: at least one.");
                    break;
                }
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in options)
                {
                    if (option is not JsonObject o || o["id"]?.GetValueKind() != JsonValueKind.String || !OptionIdPattern().IsMatch(o["id"]!.GetValue<string>())
                        || o["label"]?.GetValueKind() != JsonValueKind.String || o["label"]!.GetValue<string>().Length is 0 or > 200)
                    {
                        errors.Add("Each option has an id (^[a-z0-9][a-z0-9_]{0,62}$) and a label.");
                        break;
                    }
                    if (!ids.Add(o["id"]!.GetValue<string>()))
                    {
                        errors.Add($"Option id {o["id"]!.GetValue<string>()} twice.");
                    }
                    if (o["points"] is { } points && points.GetValueKind() != JsonValueKind.Number)
                    {
                        errors.Add("points are numbers.");
                    }
                }
                if (type == FieldType.Multiselect && config["multiPoints"] is { } mp
                    && !(mp.GetValueKind() == JsonValueKind.String && mp.GetValue<string>() is "count" or "sum"))
                {
                    errors.Add("multiPoints: count or sum.");
                }
                break;
            }
            case FieldType.Rating:
            {
                var lo = Int(config, "min") ?? 0;
                var hi = Int(config, "max") ?? 5;
                if (config["min"] is not null && Int(config, "min") is null || config["max"] is not null && Int(config, "max") is null)
                {
                    errors.Add("min and max are integers.");
                }
                else if (lo >= hi)
                {
                    errors.Add("min must be below max.");
                }
                if (config["labels"] is JsonObject labels && labels.Any(l => !int.TryParse(l.Key, CultureInfo.InvariantCulture, out var n) || n < lo || n > hi
                    || l.Value?.GetValueKind() != JsonValueKind.String))
                {
                    errors.Add($"labels: texts keyed by values from {lo} to {hi}.");
                }
                break;
            }
        }
        return errors;
    }

    /// <summary>The ids of the field's options that may be chosen (archived ones not).</summary>
    public static HashSet<string> OptionIds(JsonObject? config) =>
        (config?["options"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => o["archived"]?.GetValueKind() != JsonValueKind.True && o["id"]?.GetValueKind() == JsonValueKind.String)
            .Select(o => o["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    private static int? Int(JsonObject? config, string key) =>
        Sync.WireJson.TryDecimal(config?[key], out var d) && d == decimal.Truncate(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;

    private static decimal? Dec(JsonObject? config, string key) => Sync.WireJson.TryDecimal(config?[key], out var d) ? d : null;

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,253}$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?Z$")]
    private static partial Regex DateTimePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_]{0,62}$")]
    private static partial Regex OptionIdPattern();
}

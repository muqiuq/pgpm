using System.Text.Json;

namespace PGPM.Config;

/// <summary>
/// Score fields (docs/03 §16, data model §7.4), as PGPM.WebUI/src/domain/score.ts computes them on the devices: for the
/// MCP layer and anything else on the server that shows a score. Both run the vectors in spec/score-vectors.json. In
/// <c>decimal</c>, rounded half away from zero once at the end.
/// <para>
/// A result is <c>null</c> when the score is incomplete (an input has no value) or can't be evaluated (a value that
/// doesn't fit its field, a select option without points, a configuration the validation would refuse, a cycle, an
/// overflow).
/// </para>
/// </summary>
public static class ScoreEvaluator
{
    /// <summary>A rating without <c>config.max</c> has the default scale 0–5.</summary>
    public const int DefaultRatingMax = 5;

    public const int MaxDecimals = 10;

    public static readonly IReadOnlyList<string> Ops = ["sum", "avg", "product", "count", "min", "max"];
    public static readonly IReadOnlyList<string> NaModes = ["rescale", "ignore", "zero"];

    /// <summary>The field types a score can take as input.</summary>
    public static readonly IReadOnlyList<string> NumericTypes = ["number", "rating", "select", "multiselect", "score"];

    private static readonly IReadOnlyDictionary<string, decimal> NoCaps = new Dictionary<string, decimal>();

    /// <summary>An input's value (or n/a) and its maximum (null: none known).</summary>
    private readonly record struct Input(bool Na, decimal Value, decimal? Max);

    private sealed record Settings(
        string Op, IReadOnlyList<string> Inputs, string Na, decimal? Max, IReadOnlyDictionary<string, decimal> Cap, decimal Scale,
        bool Normalize, int Decimals);

    /// <summary>
    /// The score's value for an item: <paramref name="fieldsByKey"/> holds every field definition (inputs and nested
    /// scores), <paramref name="valuesByKey"/> the item's field values (<c>field_values.value</c>) by field key.
    /// </summary>
    public static decimal? Evaluate(
        FieldSpec score, IReadOnlyDictionary<string, FieldSpec> fieldsByKey, IReadOnlyDictionary<string, JsonElement> valuesByKey)
    {
        try
        {
            return Evaluate(score, fieldsByKey, valuesByKey, []);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>The score's maximum for display ("58 / 65") and as an input of other scores: 100 when normalised.</summary>
    public static decimal? Max(FieldSpec score)
    {
        if (score.Type != "score")
        {
            return null;
        }
        return score.Settings.Normalize == true ? 100m : score.Settings.Max;
    }

    /// <summary>A field's maximum as a score input (docs/03 §16), before the cap. Null: none known.</summary>
    public static decimal? InputMax(FieldSpec field)
    {
        var c = field.Settings;
        var options = c.Options ?? [];
        var points = options.Where(o => o.Points is not null).Select(o => o.Points!.Value).ToList();
        return field.Type switch
        {
            "number" => c.Max,
            "rating" => c.Max ?? DefaultRatingMax,
            "select" => points.Count > 0 ? points.Max() : null,
            "multiselect" => c.MultiPoints == "sum" ? points.Sum() : options.Count,
            "score" => Max(field),
            _ => null,
        };
    }

    /// <summary>Round half away from zero (docs/03 §16 step 6): 2.5 → 3, −2.5 → −3, 0.125 → 0.13.</summary>
    public static decimal RoundHalfAwayFromZero(decimal value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    internal static bool IsValidDecimals(decimal decimals) =>
        decimals == decimal.Truncate(decimals) && decimals >= 0 && decimals <= MaxDecimals;

    private static Settings? SettingsOf(FieldSpec score)
    {
        if (score.Type != "score")
        {
            return null;
        }
        var c = score.Settings;
        var na = c.Na ?? "ignore";
        var decimals = c.Decimals ?? 0;
        if (c.Op is null || !Ops.Contains(c.Op) || !NaModes.Contains(na) || !IsValidDecimals(decimals))
        {
            return null;
        }
        if (c.Inputs is null || c.Inputs.Any(k => k is null))
        {
            return null;
        }
        return new Settings(c.Op, c.Inputs, na, c.Max, c.Cap ?? NoCaps, c.Scale ?? 1m, c.Normalize == true, (int)decimals);
    }

    private static decimal? Evaluate(
        FieldSpec score, IReadOnlyDictionary<string, FieldSpec> fields, IReadOnlyDictionary<string, JsonElement> values,
        HashSet<string> visiting)
    {
        var s = SettingsOf(score);
        if (s is null || !visiting.Add(score.Key))
        {
            return null;
        }
        try
        {
            var inputs = new List<Input>(s.Inputs.Count);
            foreach (var key in s.Inputs)
            {
                // 1. Any input missing (or unusable): incomplete.
                if (!fields.TryGetValue(key, out var field) || InputOf(field, fields, values, visiting) is not { } input)
                {
                    return null;
                }
                // 2. The per-input cap, on the value and on the maximum.
                if (s.Cap.TryGetValue(key, out var cap))
                {
                    input = new Input(input.Na, input.Na ? 0 : Math.Min(input.Value, cap), input.Max is { } max ? Math.Min(max, cap) : cap);
                }
                inputs.Add(input);
            }
            return Combine(s, inputs);
        }
        finally
        {
            visiting.Remove(score.Key);
        }
    }

    /// <summary>The input's value, or null when it is missing or can't be used.</summary>
    private static Input? InputOf(
        FieldSpec field, IReadOnlyDictionary<string, FieldSpec> fields, IReadOnlyDictionary<string, JsonElement> values,
        HashSet<string> visiting)
    {
        var max = InputMax(field);
        if (field.Type == "score")
        {
            return Evaluate(field, fields, values, visiting) is { } nested ? new Input(false, nested, max) : null;
        }
        if (!values.TryGetValue(field.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        var options = field.Settings.Options ?? [];
        switch (field.Type)
        {
            case "number":
                return NumberOf(value) is { } number ? new Input(false, number, max) : null;
            case "rating":
                if (value.ValueKind == JsonValueKind.String && value.GetString() == "na")
                {
                    return new Input(true, 0, max);
                }
                return NumberOf(value) is { } rating ? new Input(false, rating, max) : null;
            case "select":
                return value.ValueKind == JsonValueKind.String && PointsOf(options, value.GetString()!) is { } points
                    ? new Input(false, points, max)
                    : null;
            case "multiselect":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }
                var chosen = new List<string>();
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }
                    var id = element.GetString()!;
                    if (!chosen.Contains(id))
                    {
                        chosen.Add(id);
                    }
                }
                if (!chosen.All(id => options.Any(o => o.Id == id)))
                {
                    return null;
                }
                if (field.Settings.MultiPoints != "sum")
                {
                    return new Input(false, chosen.Count, max);
                }
                var sum = 0m;
                foreach (var id in chosen)
                {
                    if (PointsOf(options, id) is not { } p)
                    {
                        return null;
                    }
                    sum += p;
                }
                return new Input(false, sum, max);
            default:
                return null;
        }
    }

    private static decimal? NumberOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) ? d : null;

    private static decimal? PointsOf(IReadOnlyList<SelectOptionSpec> options, string id) =>
        options.FirstOrDefault(o => o.Id == id)?.Points;

    /// <summary>Steps 3–6 of docs/03 §16 over the inputs' values.</summary>
    private static decimal? Combine(Settings s, List<Input> inputs)
    {
        // 3. n/a: zero counts it as 0, ignore and rescale drop it.
        var remaining = s.Na == "zero"
            ? inputs.Select(i => i.Na ? i with { Na = false, Value = 0 } : i).ToList()
            : inputs.Where(i => !i.Na).ToList();
        // 4. The operation; nothing remaining: no result.
        if (remaining.Count == 0)
        {
            return null;
        }
        var values = remaining.Select(i => i.Value).ToList();
        var result = s.Op switch
        {
            "sum" => values.Sum(),
            "avg" => values.Sum() / values.Count,
            "product" => values.Aggregate(1m, (a, b) => a * b),
            "count" => values.Count(v => v != 0),
            "min" => values.Min(),
            _ => values.Max(),
        };
        // 3. (cont.) rescale, for sums: to max over the maxima of the answered inputs.
        if (s.Na == "rescale" && s.Op == "sum")
        {
            if (s.Max is not { } target || remaining.Any(i => i.Max is null))
            {
                return null;
            }
            var answered = remaining.Sum(i => i.Max!.Value);
            if (answered <= 0)
            {
                return null;
            }
            result = result * target / answered;
        }
        // 5. Scale, then normalise to 0–100.
        result *= s.Scale;
        if (s.Normalize)
        {
            if (s.Max is not { } max || max <= 0)
            {
                return null;
            }
            result = result * 100 / max;
        }
        // 6. Rounded once, here.
        return RoundHalfAwayFromZero(result, s.Decimals);
    }
}

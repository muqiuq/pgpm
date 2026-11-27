using System.Text.Json;
using PGPM.Config;

namespace PGPM.Tests.Config;

/// <summary>
/// spec/score-vectors.json (docs/03 §16), the vectors score.ts runs too; copied next to the tests by PGPM.Tests.csproj.
/// A case's fields are its sets in order, then its own; a later definition replaces an earlier one with the same key.
/// </summary>
internal static class ScoreVectors
{
    public sealed record EvaluationCase(
        string Name, IReadOnlyDictionary<string, FieldSpec> Fields, IReadOnlyDictionary<string, JsonElement> Values,
        IReadOnlyDictionary<string, decimal?> Expect, IReadOnlyDictionary<string, decimal?> ExpectMax);

    public sealed record ValidationCase(
        string Name, IReadOnlyDictionary<string, FieldSpec> Fields, FieldSpec Score, IReadOnlyList<ScoreConfigError> ExpectErrors);

    private static readonly Lazy<(List<EvaluationCase> Cases, List<ValidationCase> Validation)> Loaded = new(Load);

    public static IReadOnlyList<EvaluationCase> Cases => Loaded.Value.Cases;

    public static IReadOnlyList<ValidationCase> Validation => Loaded.Value.Validation;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "spec", "score-vectors.json");

    private static (List<EvaluationCase>, List<ValidationCase>) Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
        var root = document.RootElement;
        var sets = root.GetProperty("fieldSets").EnumerateObject()
            .ToDictionary(set => set.Name, set => set.Value.EnumerateArray().Select(FieldSpec.FromJson).ToList());

        Dictionary<string, FieldSpec> FieldsOf(JsonElement c)
        {
            var fields = new Dictionary<string, FieldSpec>();
            var own = c.TryGetProperty("fields", out var list) ? list.EnumerateArray().Select(FieldSpec.FromJson) : [];
            foreach (var field in c.GetProperty("use").EnumerateArray().SelectMany(name => sets[name.GetString()!]).Concat(own))
            {
                fields[field.Key] = field;
            }
            return fields;
        }

        static Dictionary<string, decimal?> ResultsOf(JsonElement c, string name) =>
            c.TryGetProperty(name, out var results)
                ? results.EnumerateObject().ToDictionary(r => r.Name, r => r.Value.ValueKind == JsonValueKind.Null ? null : (decimal?)r.Value.GetDecimal())
                : [];

        var cases = root.GetProperty("cases").EnumerateArray().Select(c => new EvaluationCase(
            c.GetProperty("name").GetString()!,
            FieldsOf(c),
            c.GetProperty("values").EnumerateObject().ToDictionary(v => v.Name, v => v.Value.Clone()),
            ResultsOf(c, "expect"),
            ResultsOf(c, "expectMax"))).ToList();

        var validation = root.GetProperty("validation").EnumerateArray().Select(c =>
        {
            var fields = FieldsOf(c);
            var score = c.TryGetProperty("replace", out var replace) ? FieldSpec.FromJson(replace) : fields[c.GetProperty("score").GetString()!];
            var errors = c.GetProperty("expectErrors").EnumerateArray()
                .Select(e => new ScoreConfigError(e.GetProperty("code").GetString()!, e.TryGetProperty("input", out var i) ? i.GetString() : null))
                .ToList();
            return new ValidationCase(c.GetProperty("name").GetString()!, fields, score, errors);
        }).ToList();

        return (cases, validation);
    }
}

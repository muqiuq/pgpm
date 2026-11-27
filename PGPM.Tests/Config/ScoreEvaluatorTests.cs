using System.Text.Json;
using PGPM.Config;

namespace PGPM.Tests.Config;

/// <summary>Score evaluation (docs/03 §16) against spec/score-vectors.json, the vectors score.ts runs too.</summary>
[TestClass]
public sealed class ScoreEvaluatorTests
{
    public static IEnumerable<object[]> CaseNames => ScoreVectors.Cases.Select(c => new object[] { c.Name });

    [TestMethod]
    [DynamicData(nameof(CaseNames))]
    public void EvaluatesTheVector(string name)
    {
        var c = ScoreVectors.Cases.Single(c => c.Name == name);
        foreach (var (key, expected) in c.Expect)
        {
            Assert.AreEqual(expected, ScoreEvaluator.Evaluate(c.Fields[key], c.Fields, c.Values), $"{key} of: {name}");
        }
        foreach (var (key, expected) in c.ExpectMax)
        {
            Assert.AreEqual(expected, ScoreEvaluator.Max(c.Fields[key]), $"max of {key} of: {name}");
        }
    }

    [TestMethod]
    public void TheVectorsCoverEveryOpAndNaMode()
    {
        var configs = ScoreVectors.Cases.SelectMany(c => c.Fields.Values).Select(f => f.Settings).ToList();
        foreach (var op in ScoreEvaluator.Ops)
        {
            Assert.IsTrue(configs.Any(c => c.Op == op), op);
        }
        foreach (var na in ScoreEvaluator.NaModes)
        {
            Assert.IsTrue(configs.Any(c => c.Na == na), na);
        }
        Assert.IsGreaterThan(40, ScoreVectors.Cases.Count);
    }

    [TestMethod]
    [DataRow("2.5", 0, "3")]
    [DataRow("-2.5", 0, "-3")]
    [DataRow("0.125", 2, "0.13")]
    [DataRow("-0.125", 2, "-0.13")]
    [DataRow("2.675", 2, "2.68")]
    [DataRow("-0.4", 0, "0")]
    public void RoundsHalfAwayFromZero(string value, int decimals, string expected) =>
        Assert.AreEqual(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            ScoreEvaluator.RoundHalfAwayFromZero(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), decimals));

    [TestMethod]
    public void AnOverflowIsNoResult()
    {
        var n = new FieldSpec("n", "number");
        var score = new FieldSpec("x", "score", new FieldConfig { Op = "product", Inputs = ["n", "n", "n"] });
        var fields = new Dictionary<string, FieldSpec> { ["n"] = n, ["x"] = score };
        var values = new Dictionary<string, JsonElement> { ["n"] = JsonSerializer.SerializeToElement(1e12m) };
        Assert.IsNull(ScoreEvaluator.Evaluate(score, fields, values));
        values["n"] = JsonSerializer.SerializeToElement(10m);
        Assert.AreEqual(1000m, ScoreEvaluator.Evaluate(score, fields, values));
    }

    [TestMethod]
    public void ReadsAConfigColumnAndIgnoresWhatScoresDontNeed()
    {
        using var json = JsonDocument.Parse(
            """{ "min": 0, "max": 5, "allowNa": true, "labels": { "0": "not at all", "5": "fully" }, "options": [{ "id": "a", "color": "#16a34a", "points": 2 }] }""");
        var config = FieldConfig.FromJson(json.RootElement);
        Assert.AreEqual(5m, config.Max);
        Assert.AreEqual(2m, config.Options![0].Points);
        Assert.AreSame(FieldConfig.Empty, FieldConfig.FromJson(JsonSerializer.SerializeToElement<object?>(null)));
        Assert.ThrowsExactly<JsonException>(() => FieldConfig.FromJson(JsonSerializer.SerializeToElement(new { max = "65" })));
    }

    [TestMethod]
    public void AScoreWithoutConfigurationHasNoResult()
    {
        var score = new FieldSpec("x", "score");
        Assert.IsNull(ScoreEvaluator.Evaluate(score, new Dictionary<string, FieldSpec>(), new Dictionary<string, JsonElement>()));
        Assert.IsNull(ScoreEvaluator.Max(score));
        Assert.IsNull(ScoreEvaluator.Max(new FieldSpec("t", "text")));
    }
}

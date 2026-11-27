using PGPM.Config;

namespace PGPM.Tests.Config;

/// <summary>Validation of score configurations on save (docs/03 §16) against spec/score-vectors.json.</summary>
[TestClass]
public sealed class ScoreConfigValidatorTests
{
    public static IEnumerable<object[]> CaseNames => ScoreVectors.Validation.Select(c => new object[] { c.Name });

    [TestMethod]
    [DynamicData(nameof(CaseNames))]
    public void ValidatesTheVector(string name)
    {
        var c = ScoreVectors.Validation.Single(c => c.Name == name);
        var errors = ScoreConfigValidator.Validate(c.Score, c.Fields.Values);
        CollectionAssert.AreEquivalent(c.ExpectErrors.ToList(), errors.ToList(), name);
    }

    [TestMethod]
    public void ReportsAMissingOp()
    {
        var errors = ScoreConfigValidator.Validate(new FieldSpec("x", "score"), []);
        CollectionAssert.AreEquivalent(
            new List<ScoreConfigError> { new(ScoreErrorCodes.InvalidOp), new(ScoreErrorCodes.NoInputs) }, errors.ToList());
    }
}

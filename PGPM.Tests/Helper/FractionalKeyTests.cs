using PGPM.Lib.Helper;

namespace PGPM.Tests.Helper;

/// <summary>Fractional index keys (data model §10) in the format of the npm package fractional-indexing.</summary>
[TestClass]
public sealed class FractionalKeyTests
{
    /// <summary>Keys the package produces (its README, PGPM.WebUI/src/domain/fractional.test.ts).</summary>
    [TestMethod]
    [DataRow("a0")]
    [DataRow("a1")]
    [DataRow("Zz")]
    [DataRow("Zy")]
    [DataRow("a1V")]
    [DataRow("a0G")]
    [DataRow("a0V")]
    [DataRow("b00")]
    [DataRow("b0z")]
    [DataRow("Y00z")]
    [DataRow("A000000000000000000000000001")]
    [DataRow("zzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void AcceptsThePackagesKeys(string key) => Assert.IsTrue(FractionalKey.IsValid(key), key);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("a")]
    [DataRow("b0")]
    [DataRow("Z")]
    [DataRow("a00")]
    [DataRow("a1V0")]
    [DataRow("0a")]
    [DataRow("a!")]
    [DataRow("a0 ")]
    [DataRow("aé")]
    [DataRow("A00000000000000000000000000")]
    public void RefusesAnythingElse(string? key) => Assert.IsFalse(FractionalKey.IsValid(key), key ?? "null");

    [TestMethod]
    public void RefusesKeysBeyondTheBound()
    {
        Assert.IsTrue(FractionalKey.IsValid("a0" + new string('V', FractionalKey.MaxLength - 2)));
        Assert.IsFalse(FractionalKey.IsValid("a0" + new string('V', FractionalKey.MaxLength - 1)));
    }

    [TestMethod]
    public void OrdersByKeyOrdinallyThenById()
    {
        var a = Guid.Parse("0193f3c2-7d1e-7c55-9a35-8e2f1a6b4c90");
        var b = Guid.Parse("0193f3c2-7d1e-7c55-9a35-8e2f1a6b4c91");
        // Ordinal: upper case before lower case ("Zz" < "a0"), unlike a culture's comparison.
        Assert.AreEqual(-1, FractionalKey.Compare("Zz", b, "a0", a));
        Assert.AreEqual(1, FractionalKey.Compare("a1", a, "a0V", b));
        Assert.AreEqual(-1, FractionalKey.Compare("a0", a, "a0", b));
        Assert.AreEqual(1, FractionalKey.Compare("a0", b, "a0", a));
        Assert.AreEqual(0, FractionalKey.Compare("a0", a, "a0", a));
    }

    /// <summary>generateKeyBetween(key, null) of the npm package fractional-indexing, for the rows the server appends.</summary>
    [TestMethod]
    [DataRow(null, "a0")]
    [DataRow("a0", "a1")]
    [DataRow("a0V", "a1")]
    [DataRow("az", "b00")]
    [DataRow("b0z", "b10")]
    [DataRow("Zz", "a0")]
    [DataRow("Yzz", "Z0")]
    public void AfterIsTheNextKeyOfThePackage(string? key, string expected)
    {
        Assert.AreEqual(expected, FractionalKey.After(key));
        Assert.IsTrue(FractionalKey.IsValid(expected));
        Assert.IsTrue(key is null || string.CompareOrdinal(key, expected) < 0);
    }
}

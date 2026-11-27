using PGPM.Lib.Helper;
using PGPM.Sync.Live;

namespace PGPM.Tests.Sync;

/// <summary>Who else is editing (docs/03 §18.6) is kept in memory: one user can't fill it (S-12, docs/06).</summary>
[TestClass]
public sealed class EditingRegistryTests
{
    [TestMethod]
    public void AUserKeepsAtMostFiftyOpenEditorsTheOldestEndFirst()
    {
        var registry = new EditingRegistry();
        var ended = new List<EditingEntry>();
        registry.Changed += (entry, started) =>
        {
            if (!started)
            {
                ended.Add(entry);
            }
        };
        var user = Ids.New();
        var other = Ids.New();
        var item = Ids.New();
        registry.Touch(item, "description", other, "Other");
        for (var i = 0; i < EditingRegistry.MaxPerUser + 5; i++)
        {
            registry.Touch(item, $"field:f{i}", user, "Busy");
        }
        Assert.AreEqual(EditingRegistry.MaxPerUser, registry.Active.Count(e => e.UserId == user));
        Assert.AreEqual(1, registry.Active.Count(e => e.UserId == other));
        Assert.AreEqual(5, ended.Count);
        Assert.IsTrue(registry.Active.Any(e => e.Target == $"field:f{EditingRegistry.MaxPerUser + 4}"), "the newest stays");
        Assert.IsFalse(registry.Active.Any(e => e.Target == "field:f0"), "the oldest went");
    }
}

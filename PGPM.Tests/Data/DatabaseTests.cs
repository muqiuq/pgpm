using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Tests.Data;

/// <summary>The database conventions (docs/02-data-model.md §1, docs/03 §3.2): snake_case, UTC, UUID v7, the migrator's guard.</summary>
[TestClass]
public sealed class DatabaseTests
{
    [TestMethod]
    public void TablesAndColumnsAreSnakeCase()
    {
        using var db = PgpmDbContext.Create(TestDefaults.ConnectionString("pgpm_test"));
        var entity = db.Model.FindEntityType(typeof(KeyValueEntity))!;
        Assert.AreEqual("key_values", entity.GetTableName());
        Assert.AreEqual("updated_at", entity.FindProperty(nameof(KeyValueEntity.UpdatedAt))!.GetColumnName());
    }

    /// <summary>A local or unspecified DateTime is stored as the same instant in UTC and read back as UTC.</summary>
    [TestMethod]
    public async Task DateTimesAreStoredAndReadAsUtc()
    {
        var key = $"test-{Guid.NewGuid():N}";
        var local = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);
        await using (var db = PgpmDbContext.Create(TestDefaults.ConnectionString("pgpm_test")))
        {
            db.KeyValues.Add(new KeyValueEntity { Key = key, Value = "x", UpdatedAt = local });
            await db.SaveChangesAsync();
        }
        await using (var db = PgpmDbContext.Create(TestDefaults.ConnectionString("pgpm_test")))
        {
            var read = await db.KeyValues.SingleAsync(k => k.Key == key);
            Assert.AreEqual(DateTimeKind.Utc, read.UpdatedAt.Kind);
            Assert.AreEqual(local.ToUniversalTime(), read.UpdatedAt);
        }
    }

    [TestMethod]
    public void EnumsAreLowerCaseText()
    {
        var converter = new LowerCaseEnumConverter<DayOfWeek>();
        Assert.AreEqual("wednesday", converter.ConvertToProvider(DayOfWeek.Wednesday));
        Assert.AreEqual(DayOfWeek.Friday, converter.ConvertFromProvider("friday"));
    }

    [TestMethod]
    public void NewIdsAreUuidVersion7()
    {
        var id = Ids.New();
        Assert.IsTrue(Ids.IsVersion7(id));
        Assert.IsFalse(Ids.IsVersion7(Guid.NewGuid()));
    }

    /// <summary>
    /// A database a newer PGPM migrated is refused, so an old image never runs on a newer schema. (The naming convention
    /// makes the history table's columns snake_case too.)
    /// </summary>
    [TestMethod]
    public void TheMigratorRefusesADatabaseANewerVersionMigrated()
    {
        using var db = PgpmDbContext.Create(TestDefaults.CreateDatabase("pgpm_test_newer"));
        DatabaseMigrator.Migrate(db, NullLogger.Instance);
        db.Database.ExecuteSql($"""INSERT INTO "__EFMigrationsHistory" (migration_id, product_version) VALUES ('29991231000000_FromTheFuture', '10.0.0')""");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => DatabaseMigrator.Migrate(db, NullLogger.Instance));
        StringAssert.Contains(error.Message, "29991231000000_FromTheFuture");
    }
}

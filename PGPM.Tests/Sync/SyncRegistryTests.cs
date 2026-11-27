using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Persons;
using PGPM.Sync;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>
/// The registry holds every synced table (PLM pitfall: a table the cursor forgot), and every key of every patch whitelist
/// reaches the database and comes back (PLM pitfall: fields the server silently ignored).
/// </summary>
[TestClass]
public sealed class SyncRegistryTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public void EverySyncedEntityIsRegistered()
    {
        var synced = typeof(ISyncedEntity).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISyncedEntity).IsAssignableFrom(t)).ToList();
        CollectionAssert.AreEquivalent(synced, SyncTables.All.Select(t => t.EntityType).ToList());
        using var db = app.NewDb();
        var mapped = db.Model.GetEntityTypes().Where(e => typeof(ISyncedEntity).IsAssignableFrom(e.ClrType)).Select(e => e.ClrType).ToList();
        CollectionAssert.AreEquivalent(synced, mapped);
        foreach (var table in SyncTables.All)
        {
            Assert.AreEqual(table.SqlTable(db), table.Name, "the wire name is the table's");
            var keys = table.Columns(db).Select(c => c.Key).ToHashSet();
            foreach (var key in table.Patchable.Concat(table.ServerOwned).Concat(table.Hidden))
            {
                Assert.IsTrue(keys.Contains(key.EndsWith(".*", StringComparison.Ordinal) ? key[..^2] : key), $"{table.Name}.{key} is no column");
            }
            foreach (var parent in table.Parents)
            {
                Assert.IsNotNull(SyncTables.Find(parent.Table), $"{table.Name}: parent {parent.Table}");
                Assert.IsTrue(keys.Contains(parent.ForeignKey), $"{table.Name}.{parent.ForeignKey}");
            }
            Assert.IsTrue(SyncTables.All.TakeWhile(t => t != table).Select(t => t.Name).ToHashSet().IsSupersetOf(table.Parents.Select(p => p.Table)),
                $"{table.Name} comes after its parents");
        }
    }

    /// <summary>A value of the column's type that differs from the row's (the keys whose rules need one their own).</summary>
    private static JsonNode? Sample(string key, WireColumn column, JsonNode? current)
    {
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        return key switch
        {
            "color" => "#12ab34",
            "leadCurrency" => "EUR",
            "userType" => current?.GetValue<string>() == "Guest" ? "Member" : "Guest",
            "email" => "round.trip@example.com",
            _ when column.IsJson => new JsonObject { ["sample"] = new JsonArray(1, 2) },
            _ when type == typeof(string) => $"{key} value",
            _ when type == typeof(Guid) => Ids.New().ToString(),
            _ when type == typeof(bool) => current?.GetValue<bool>() != true,
            _ when type == typeof(List<string>) => new JsonArray("alpha", "beta"),
            _ when type == typeof(decimal) => 1234.5m,
            _ when type == typeof(short) => (short)42,
            _ when type == typeof(DateOnly) => key == "projectStartDate" ? "2027-01-04" : "2027-03-04",
            _ => throw new AssertFailedException($"No sample for {key} ({type.Name})"),
        };
    }

    [TestMethod]
    public async Task EveryWhitelistedKeyRoundTrips()
    {
        var user = await UserAsync(app, "whitelist");
        var client = new SyncClient(app.ClientFor(user, "Notes:Create,Persons:Edit"));
        var note = Ids.New();
        var person = Ids.New();
        var setting = Ids.New();
        await client.SyncAllAsync([Insert("items", note, Note("Round trip")), Insert("persons", person, new JsonObject { ["displayName"] = "Manual" }),
            Insert("user_settings", setting, new JsonObject { ["key"] = "round-trip", ["value"] = new JsonObject { ["a"] = 1 } })]);
        // Leads and projects aren't created by clients yet: made here, patched through the sync.
        var lead = Ids.New();
        var project = Ids.New();
        await using (var db = app.NewDb())
        {
            db.Items.Add(new ItemEntity { Id = lead, Type = ItemType.Lead, Title = "Lead", OwnerUserId = user, StatusId = Ids.New(), Number = 1 });
            db.Items.Add(new ItemEntity { Id = project, Type = ItemType.Project, Title = "Project", OwnerUserId = user, StatusId = Ids.New(), Number = 1 });
            await db.SaveChangesAsync();
        }
        await client.SyncAllAsync();

        var rows = new Dictionary<string, Func<string, Guid>>
        {
            ["items"] = key => key.StartsWith("lead", StringComparison.Ordinal) || key == "statusId" ? lead
                : key.StartsWith("project", StringComparison.Ordinal) ? project : note,
            ["persons"] = _ => person,
            ["user_settings"] = _ => setting,
        };
        await using var model = app.NewDb();
        var failures = new List<string>();
        foreach (var table in SyncTables.All)
        {
            foreach (var pattern in table.Patchable)
            {
                Assert.IsTrue(rows.ContainsKey(table.Name), $"No row to patch in {table.Name}");
                var key = pattern.EndsWith(".*", StringComparison.Ordinal) ? pattern[..^2] + ".member" : pattern;
                var id = rows[table.Name](key);
                var column = table.Column(model, key.Split('.')[0])!;
                var row = client.Row(table.Name, id)!;
                var current = key.Contains('.', StringComparison.Ordinal) ? row[column.Key]?[key.Split('.')[1]] : row[key];
                var value = key.Contains('.', StringComparison.Ordinal) ? JsonValue.Create("member value") : Sample(key, column, current);
                var answer = await client.SyncAllAsync([Patch(table.Name, id, row.Int("rev"), (key, current?.DeepClone(), value))]);
                var result = answer.Arr("results")[0]!;
                var back = client.Row(table.Name, id)!;
                var stored = key.Contains('.', StringComparison.Ordinal) ? back[column.Key]?[key.Split('.')[1]] : back[key];
                if (result.Str("status") != "accepted" || !WireJson.Same(WireJson.Normalize(value, key.Contains('.') ? column with { IsJson = false, ClrType = typeof(string) } : column), stored))
                {
                    failures.Add($"{table.Name}.{key}: {result.ToJsonString()} → {stored?.ToJsonString()}");
                }
            }
        }
        Assert.AreEqual("", string.Join("\n", failures));
        await using var check = app.NewDb();
        var stamped = await check.Items.Where(i => i.Id == lead).Select(i => i.StatusChangedAt).SingleAsync();
        Assert.IsNotNull(stamped, "a status change sets status_changed_at");
        Assert.AreEqual(PersonSource.Manual, await check.Persons.Where(p => p.Id == person).Select(p => p.Source).SingleAsync());
    }
}

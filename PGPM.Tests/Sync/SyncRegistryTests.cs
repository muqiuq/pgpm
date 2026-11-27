using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Config;
using PGPM.Files;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Maps;
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
            "sortOrder" or "z" when type == typeof(string) => current?.GetValue<string>() == "a1" ? "a2" : "a1",
            _ when column.IsJson => new JsonObject { ["sample"] = new JsonArray(1, 2) },
            _ when type == typeof(string) => $"{key} value",
            _ when type == typeof(Guid) => Ids.New().ToString(),
            _ when type == typeof(bool) => current?.GetValue<bool>() != true,
            _ when type == typeof(List<string>) => new JsonArray("alpha", "beta"),
            _ when type == typeof(decimal) => 1234.5m,
            _ when type == typeof(short) => (short)42,
            _ when type == typeof(int) => 7,
            _ when type == typeof(double) => 12.5,
            _ when type == typeof(byte[]) => Convert.ToBase64String([1, 2, 3]),
            _ when type == typeof(DateOnly) => key == "projectStartDate" ? "2027-01-04" : "2027-03-04",
            _ => throw new AssertFailedException($"No sample for {key} ({type.Name})"),
        };
    }

    [TestMethod]
    public async Task EveryWhitelistedKeyRoundTrips()
    {
        var user = await UserAsync(app, "whitelist");
        var client = new SyncClient(app.ClientFor(user, "Notes:Create,Persons:Edit,Configuration:Manage,Maps:Create"));
        var note = Ids.New();
        var person = Ids.New();
        var setting = Ids.New();
        await client.SyncAllAsync([Insert("items", note, Note("Round trip")), Insert("persons", person, new JsonObject { ["displayName"] = "Manual" }),
            Insert("user_settings", setting, new JsonObject { ["key"] = "round-trip", ["value"] = new JsonObject { ["a"] = 1 } })]);
        // The rows of the other tables are made here and patched through the sync.
        var lead = Ids.New();
        var project = Ids.New();
        var f = new Fixture();
        await using (var db = app.NewDb())
        {
            db.Statuses.Add(new StatusEntity { Id = f.LeadStatus, ItemType = ItemType.Lead, Name = $"RT lead {f.Tag}", Category = StatusCategory.Open });
            db.Statuses.Add(new StatusEntity { Id = f.LeadStatus2, ItemType = ItemType.Lead, Name = $"RT lead 2 {f.Tag}", Category = StatusCategory.Active });
            db.Statuses.Add(new StatusEntity { Id = f.ProjectStatus, ItemType = ItemType.Project, Name = $"RT project {f.Tag}", Category = StatusCategory.Active });
            db.Sections.Add(new SectionEntity { Id = f.Section, Name = $"RT {f.Tag}", AppliesTo = ["idea", "project"] });
            db.FieldDefinitions.Add(new FieldDefinitionEntity { Id = f.Field, SectionId = f.Section, Key = $"rt_{f.Tag}", Label = "Text", Type = FieldType.Text });
            db.Items.Add(new ItemEntity { Id = lead, Type = ItemType.Lead, Title = "Lead", OwnerUserId = user, StatusId = f.LeadStatus, Number = 1 });
            db.Items.Add(new ItemEntity { Id = project, Type = ItemType.Project, Title = "Project", OwnerUserId = user, StatusId = f.ProjectStatus, Number = 1 });
            db.ItemSections.Add(new ItemSectionEntity { Id = f.ItemSection, ItemId = project, SectionId = f.Section });
            db.FieldValues.Add(new FieldValueEntity { Id = f.FieldValue, ItemId = project, FieldId = f.Field, Value = "\"start\"" });
            db.Milestones.Add(new MilestoneEntity { Id = f.Gate, ItemId = project, Kind = MilestoneKind.Gate, Title = "Gate" });
            db.DodItems.Add(new DodItemEntity { Id = f.Dod, MilestoneId = f.Gate, Text = "Criterion" });
            db.Assignments.Add(new AssignmentEntity { Id = f.Assignment, ItemId = project, PersonId = person });
            db.Files.Add(new FileEntity
            {
                Id = f.Cover, ItemId = note, Purpose = FilePurpose.Cover, FileName = "cover.png", ContentType = "image/png", SizeBytes = 10,
                Sha256 = new string('a', 64),
            });
            db.Maps.Add(new MapEntity { Id = f.Canvas, Kind = MapKind.Canvas, Name = "Canvas", OwnerUserId = user });
            db.Maps.Add(new MapEntity { Id = f.Matrix, Kind = MapKind.Matrix, Name = "Matrix", OwnerUserId = user, Matrix = "{}" });
            db.MapLayers.Add(new MapLayerEntity { Id = f.Layer, MapId = f.Canvas, Name = "Base" });
            db.MapLayers.Add(new MapLayerEntity { Id = f.Layer2, MapId = f.Canvas, Name = "Top", SortOrder = "a5" });
            db.MapElements.Add(new MapElementEntity { Id = f.Rect, MapId = f.Canvas, LayerId = f.Layer, Kind = ElementKind.Rect, Width = 10, Height = 10 });
            db.MapElements.Add(new MapElementEntity
            {
                Id = f.Line, MapId = f.Canvas, LayerId = f.Layer, Kind = ElementKind.Line, Width = 10, Height = 10, Points = "[[0,0],[1,1]]", Z = "a3",
            });
            db.SavedViews.Add(new SavedViewEntity { Id = f.View, OwnerUserId = user, ItemType = ViewItemType.Idea, Name = "Mine" });
            await db.SaveChangesAsync();
        }
        await client.SyncAllAsync();

        var rows = new Dictionary<string, Func<string, Guid>>
        {
            ["items"] = key => key.StartsWith("lead", StringComparison.Ordinal) || key == "statusId" ? lead
                : key.StartsWith("project", StringComparison.Ordinal) ? project : note,
            ["persons"] = _ => person,
            ["user_settings"] = _ => setting,
            ["saved_views"] = _ => f.View,
            ["files"] = _ => f.Cover,
            ["item_sections"] = _ => f.ItemSection,
            ["field_values"] = _ => f.FieldValue,
            ["milestones"] = _ => f.Gate,
            ["dod_items"] = _ => f.Dod,
            ["assignments"] = _ => f.Assignment,
            ["maps"] = key => key.StartsWith("matrix", StringComparison.Ordinal) ? f.Matrix : f.Canvas,
            ["map_layers"] = _ => f.Layer,
            ["map_elements"] = _ => f.Line,
        };
        // Values the tables' rules want (a status of the type, a file of the item, a line's points …).
        var samples = new Dictionary<(string, string), JsonNode?>
        {
            [("items", "statusId")] = f.LeadStatus2.ToString(),
            [("items", "imageFileId")] = f.Cover.ToString(),
            [("field_values", "value")] = "round trip",
            [("milestones", "status")] = "in_progress",
            [("milestones", "decision")] = "no_go",
            [("milestones", "onGoStatusId")] = f.ProjectStatus.ToString(),
            [("milestones", "onNoGoStatusId")] = f.ProjectStatus.ToString(),
            [("dod_items", "state")] = "done",
            [("files", "fileName")] = "renamed.png",
            [("map_elements", "layerId")] = f.Layer2.ToString(),
            [("map_elements", "points")] = new JsonArray(new JsonArray(0, 0), new JsonArray(5, 5)),
            [("map_elements", "startElementId")] = f.Rect.ToString(),
            [("map_elements", "endElementId")] = f.Rect.ToString(),
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
                var row = client.Row(table.Name, id);
                if (row is null)
                {
                    failures.Add($"{table.Name}.{key}: the row {id} wasn't delivered");
                    continue;
                }
                var current = key.Contains('.', StringComparison.Ordinal) ? row[column.Key]?[key.Split('.')[1]] : row[key];
                var value = key.Contains('.', StringComparison.Ordinal) ? JsonValue.Create("member value")
                    : samples.TryGetValue((table.Name, key), out var special) ? special?.DeepClone() : Sample(key, column, current);
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
        var gate = await check.Milestones.SingleAsync(m => m.Id == f.Gate);
        Assert.IsNotNull(gate.DecidedAt, "a decision sets decided_at");
        Assert.AreEqual(user, gate.DecidedBy);
    }

    /// <summary>The ids of the round trip's rows.</summary>
    private sealed class Fixture
    {
        public string Tag { get; } = Guid.NewGuid().ToString("N")[..10];
        public Guid LeadStatus { get; } = Ids.New();
        public Guid LeadStatus2 { get; } = Ids.New();
        public Guid ProjectStatus { get; } = Ids.New();
        public Guid Section { get; } = Ids.New();
        public Guid Field { get; } = Ids.New();
        public Guid ItemSection { get; } = Ids.New();
        public Guid FieldValue { get; } = Ids.New();
        public Guid Gate { get; } = Ids.New();
        public Guid Dod { get; } = Ids.New();
        public Guid Assignment { get; } = Ids.New();
        public Guid Cover { get; } = Ids.New();
        public Guid Canvas { get; } = Ids.New();
        public Guid Matrix { get; } = Ids.New();
        public Guid Layer { get; } = Ids.New();
        public Guid Layer2 { get; } = Ids.New();
        public Guid Rect { get; } = Ids.New();
        public Guid Line { get; } = Ids.New();
        public Guid View { get; } = Ids.New();
    }
}

using System.Text.Json.Nodes;
using PGPM.Lib.Helper;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>
/// The convergence property (docs/03 §11.13, docs/05 §5): three devices – two of one user, one of a colleague – make
/// random changes offline (create notes, shared or not; patch fields; delete), sync in random order through the real
/// endpoint in small pages, and resolve every conflict with <i>theirs</i>. Afterwards every device's rows equal what the
/// server delivers to its user from scratch, and nothing was refused.
/// </summary>
[TestClass]
public sealed class SyncConvergenceTests
{
    private const string Notes = "Notes:Create";

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>A device with an outbox: the view is its server rows with the pending changes on top (docs/02 §14).</summary>
    private sealed class Device(string name, Guid user, HttpClient http)
    {
        public string Name { get; } = name;

        public Guid User { get; } = user;

        public SyncClient Sync { get; } = new(http);

        public List<JsonObject> Outbox { get; } = [];

        public List<JsonObject> Resolved { get; } = [];

        public List<string> Refused { get; } = [];

        public int Conflicts { get; private set; }

        /// <summary>The live notes as the user sees them now: server rows, pending inserts, patches and deletes applied.</summary>
        public Dictionary<Guid, JsonObject> View()
        {
            var view = Sync.Rows("items").ToDictionary(r => Guid.Parse(r.Str("id")!), r => r.DeepClone().AsObject());
            foreach (var m in Outbox.Where(m => m.Str("table") == "items"))
            {
                var id = Guid.Parse(m.Str("id")!);
                switch (m.Str("op"))
                {
                    case "insert":
                        var row = m["row"]!.DeepClone().AsObject();
                        row["access"] = "manage";
                        row["rev"] = 0;
                        view[id] = row;
                        break;
                    case "patch" when view.TryGetValue(id, out var target):
                        foreach (var (key, change) in m["changes"]!.AsObject())
                        {
                            target[key] = change!["new"]?.DeepClone();
                        }
                        break;
                    case "delete":
                        view.Remove(id);
                        break;
                }
            }
            return view;
        }

        public async Task SyncAsync()
        {
            do
            {
                var chunk = Outbox.Take(4).ToList();
                var answer = await Sync.SyncAsync(chunk, limit: 3, resolved: Resolved.ToList());
                Resolved.Clear();
                Outbox.RemoveRange(0, chunk.Count);
                foreach (var result in answer.Arr("results"))
                {
                    switch (result.Str("status"))
                    {
                        case "partial":
                        case "conflict":
                            Conflicts++;
                            break;
                        case "rejected":
                            Refused.Add(result!.ToJsonString());
                            break;
                    }
                }
                foreach (var conflict in answer.Arr("conflicts"))
                {
                    Resolved.Add(new JsonObject
                    {
                        ["table"] = conflict.Str("table"), ["id"] = conflict.Str("id"), ["field"] = conflict.Str("field"), ["resolution"] = "theirs",
                    });
                }
                // A reset (a purge elsewhere raised the tombstone horizon above the cursor) pulls everything again.
                while (answer.Bool("more") || answer.Bool("reset"))
                {
                    answer = await Sync.SyncAsync(limit: 3);
                }
            }
            while (Outbox.Count > 0 || Resolved.Count > 0);
        }
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(23)]
    [DataRow(47)]
    public async Task ThreeDevicesConverge(int seed)
    {
        var random = new Random(seed);
        var team = await GroupAsync(app, "converge");
        var u1 = await UserAsync(app, "u1", team);
        var u2 = await UserAsync(app, "u2", team);
        var devices = new[]
        {
            new Device("A", u1, app.ClientFor(u1, Notes)), new Device("B", u1, app.ClientFor(u1, Notes)), new Device("C", u2, app.ClientFor(u2, Notes)),
        };
        foreach (var device in devices)
        {
            await device.SyncAsync();
        }
        string[] words = ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta"];
        string[] colors = ["#ff0000", "#00ff00", "#0000ff"];

        for (var round = 0; round < 8; round++)
        {
            foreach (var device in devices)
            {
                for (var n = random.Next(0, 5); n > 0; n--)
                {
                    var view = device.View();
                    var editable = view.Where(v => v.Value.Str("access") is "edit" or "manage").Select(v => v.Key).Order().ToList();
                    var manageable = view.Where(v => v.Value.Str("access") == "manage").Select(v => v.Key).Order().ToList();
                    var pick = random.Next(10);
                    if (pick < 3 || editable.Count == 0)
                    {
                        var id = Ids.New();
                        var batch = Ids.New();
                        device.Outbox.Add(Insert("items", id, Note($"{words[random.Next(words.Length)]} {round}", "start"), batch));
                        if (random.Next(2) == 0)
                        {
                            device.Outbox.Add(Insert("item_shares", Ids.New(), Share(id, team, "edit"), batch));
                        }
                    }
                    else if (pick < 9)
                    {
                        var id = editable[random.Next(editable.Count)];
                        var row = view[id];
                        var key = new[] { "title", "description", "pinned", "color" }[random.Next(4)];
                        JsonNode? value = key switch
                        {
                            "title" => $"{words[random.Next(words.Length)]} by {device.Name}",
                            "description" => $"text {random.Next(100)}",
                            "pinned" => !row.Bool("pinned"),
                            _ => colors[random.Next(colors.Length)],
                        };
                        var baseRev = row.Int("rev");
                        device.Outbox.Add(Patch("items", id, baseRev, (key, row[key]?.DeepClone(), value)));
                    }
                    else if (manageable.Count > 0)
                    {
                        // As the outbox coalesces (data model §14.1): a note not synced yet isn't deleted here, pending patches
                        // go into the delete, which carries the server's rev.
                        var id = manageable[random.Next(manageable.Count)];
                        if (device.Sync.Row("items", id) is not { } synced)
                        {
                            continue;
                        }
                        device.Outbox.RemoveAll(m => m.Str("op") == "patch" && m.Str("id") == id.ToString());
                        device.Outbox.Add(Delete("items", id, synced.Int("rev")));
                    }
                }
            }
            foreach (var device in devices.OrderBy(_ => random.Next()).Where(_ => random.Next(3) > 0))
            {
                await device.SyncAsync();
            }
        }
        for (var i = 0; i < 2; i++)
        {
            foreach (var device in devices)
            {
                await device.SyncAsync();
            }
        }

        foreach (var device in devices)
        {
            Assert.AreEqual("", string.Join("\n", device.Refused), $"{device.Name}: nothing is refused");
            var server = new SyncClient(app.ClientFor(device.User, Notes));
            await server.SyncAllAsync(limit: 1000);
            foreach (var table in new[] { "items", "item_shares" })
            {
                var mine = device.Sync.Rows(table).ToDictionary(r => r.Str("id")!);
                var theirs = server.Rows(table).ToDictionary(r => r.Str("id")!);
                CollectionAssert.AreEquivalent(theirs.Keys.ToList(), mine.Keys.ToList(), $"{device.Name} {table}: the same rows");
                foreach (var (id, row) in theirs)
                {
                    Assert.IsTrue(JsonNode.DeepEquals(row, mine[id]), $"{device.Name} {table} {id}: {mine[id].ToJsonString()} vs {row.ToJsonString()}");
                }
            }
        }
        var summary = string.Join(", ", devices.Select(d => $"{d.Name} {d.Sync.Rows("items").Count()} notes, {d.Conflicts} conflicts"));
        Assert.IsGreaterThan(0, devices.Sum(d => d.Sync.Rows("items").Count()), summary);
        Assert.IsGreaterThan(0, devices.Sum(d => d.Conflicts), $"the seed makes conflicts: {summary}");
    }
}

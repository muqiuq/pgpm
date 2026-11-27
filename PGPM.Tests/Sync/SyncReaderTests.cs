using System.Text.Json.Nodes;
using PGPM.Auth;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>
/// The pull skips the tables without a row above the cursor (docs/07 §1): from every cursor it answers exactly what reading
/// every table answers – rows, evictions, the cursor, <c>more</c> –, through inserts, patches, a re-stamp (sharing), an
/// eviction (unsharing) and a delete, with pages small enough to be full. Alone: other classes' commits between the two
/// pulls would change the head (the database is the run's).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SyncReaderTests
{
    private const string Rights = "Notes:Create,Maps:Create";

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task SkippingTablesWithoutChangesAnswersWhatReadingEveryTableAnswers()
    {
        var team = await GroupAsync(app, "reader-team");
        var owner = await UserAsync(app, "reader-owner", team);
        var reader = await UserAsync(app, "reader-reader", team);
        var ownerClient = new SyncClient(app.ClientFor(owner, Rights));
        var readerClient = new SyncClient(app.ClientFor(reader, Rights));
        await readerClient.SyncAllAsync();
        var start = readerClient.Cursor;

        var notes = Enumerable.Range(0, 4).Select(_ => Ids.New()).ToList();
        var batch = Ids.New();
        await ownerClient.SyncAllAsync([.. notes.SelectMany(id => new[]
        {
            Insert("items", id, Note($"Note {id}"), batch), Insert("item_shares", Ids.New(), Share(id, team, "read"), batch),
        })]);
        var map = Ids.New();
        var layer = Ids.New();
        var mapBatch = Ids.New();
        await ownerClient.SyncAllAsync([
            Insert("maps", map, new JsonObject { ["kind"] = "canvas", ["name"] = "Board", ["settings"] = new JsonObject() }, mapBatch),
            Insert("map_shares", Ids.New(), new JsonObject { ["mapId"] = map.ToString(), ["groupId"] = team.ToString(), ["access"] = "edit" }, mapBatch),
            Insert("map_layers", layer, new JsonObject { ["mapId"] = map.ToString(), ["name"] = "Layer", ["sortOrder"] = "a0", ["isVisible"] = true }),
        ]);
        await ownerClient.SyncAllAsync([Patch("items", notes[0], 1, ("title", $"Note {notes[0]}", "Renamed"))]);
        // A re-stamp (raised to edit), an eviction (unshared), a delete.
        using (await ownerClient.Http.PutJsonAsync($"/api/v1/items/{notes[1]}/shares",
                   new JsonArray(new JsonObject { ["groupId"] = team.ToString(), ["access"] = "edit" })))
        {
        }
        using (await ownerClient.Http.PutJsonAsync($"/api/v1/items/{notes[2]}/shares", new JsonArray()))
        {
        }
        await ownerClient.SyncAllAsync([Delete("items", notes[3], null, force: true)]);

        SessionCache.Entry? entry;
        await using (var sessions = app.NewDb())
        {
            entry = await SessionCache.LoadAsync(sessions, reader);
        }
        var viewer = new Viewer(reader, false, entry!.GroupIds, entry.Rights);
        long head;
        await using (var db = app.NewDb())
        {
            head = await SyncCounter.HeadAsync(db, CancellationToken.None);
        }
        var compared = 0;
        foreach (var limit in new[] { 2, 500 })
        {
            for (var since = start; since <= head; since++)
            {
                await using var a = app.NewDb();
                await using var b = app.NewDb();
                var skipping = await SyncReader.PullAsync(a, viewer, since, limit, 0, CancellationToken.None, skipUnchanged: true);
                var reading = await SyncReader.PullAsync(b, viewer, since, limit, 0, CancellationToken.None, skipUnchanged: false);
                Assert.AreEqual(Describe(reading), Describe(skipping), $"since {since}, limit {limit}");
                compared++;
            }
        }
        Assert.IsGreaterThan(10, compared);
        // And what the reader gets: the renamed note, the raised one, neither the unshared nor the deleted one; the map.
        await readerClient.SyncAllAsync();
        Assert.AreEqual("Renamed", readerClient.Row("items", notes[0]).Str("title"));
        Assert.AreEqual("edit", readerClient.Row("items", notes[1]).Str("access"));
        Assert.IsNull(readerClient.Row("items", notes[2]));
        Assert.IsNull(readerClient.Row("items", notes[3]));
        Assert.IsNotNull(readerClient.Row("map_layers", layer));
    }

    private static string Describe(PullPage page) => new JsonObject
    {
        ["head"] = page.Head,
        ["cursor"] = page.Cursor,
        ["more"] = page.More,
        ["reset"] = page.Reset,
        ["changes"] = new JsonObject(page.Changes.OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => KeyValuePair.Create(c.Key, (JsonNode?)new JsonArray([.. c.Value.Select(r => (JsonNode)r.DeepClone())])))),
        ["evictions"] = new JsonArray([.. page.Evictions.Select(e => (JsonNode)$"{e.Kind}:{e.Id}")]),
    }.ToJsonString();
}

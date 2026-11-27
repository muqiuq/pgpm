using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Tests.Sync;
using static PGPM.Tests.Content.ContentKit;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Content;

/// <summary>
/// Maps (docs/02 §9): created with Maps: Create, shared like items (re-stamp, evictions), layers and elements with their
/// rules, templates for everybody, the Trash.
/// </summary>
[TestClass]
public sealed class MapTests
{
    private const string Rights = "Notes:Create,Maps:Create";

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static JsonObject Map(string name, string kind = "canvas", JsonObject? matrix = null) => new()
    {
        ["kind"] = kind, ["name"] = name, ["settings"] = new JsonObject { ["grid"] = true }, ["matrix"] = matrix, ["isTemplate"] = false,
    };

    private static JsonObject Layer(Guid map, string name, string sort = "a0") => new()
    {
        ["mapId"] = map.ToString(), ["name"] = name, ["sortOrder"] = sort, ["isVisible"] = true, ["isLocked"] = false,
    };

    private static JsonObject Element(Guid map, Guid layer, string kind, double x = 0, double y = 0, JsonObject? more = null)
    {
        var row = new JsonObject
        {
            ["mapId"] = map.ToString(), ["layerId"] = layer.ToString(), ["kind"] = kind, ["z"] = "a0", ["x"] = x, ["y"] = y, ["width"] = 100, ["height"] = 50,
            ["style"] = new JsonObject(),
        };
        foreach (var (key, value) in more ?? [])
        {
            row[key] = value?.DeepClone();
        }
        return row;
    }

    [TestMethod]
    public async Task AMapWithLayersAndElementsFollowsItsRules()
    {
        var owner = await UserAsync(app, "mapper");
        var client = new SyncClient(app.ClientFor(owner, Rights));
        var (map, matrix, layer, matrixLayer, note, secret) = (Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var other = new SyncClient(app.ClientFor(await UserAsync(app, "other"), Rights));
        await other.SyncAllAsync([Insert("items", secret, Note("Not on your map"))]);
        var (card, rect, line, image, file, foreign) = (Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var answer = await client.SyncAllAsync(
        [
            Insert("maps", map, Map("Board")),
            Insert("maps", matrix, Map("Matrix", "matrix", new JsonObject { ["x"] = new JsonObject { ["field"] = "f", ["min"] = 0, ["max"] = 5 } })),
            Insert("map_layers", layer, Layer(map, "Base")),
            Insert("map_layers", matrixLayer, Layer(matrix, "Base")),
            Insert("items", note, Note("On the board")),
            Insert("map_elements", card, Element(map, layer, "item", 10.456, 20.004, new JsonObject { ["itemId"] = note.ToString() })),
            Insert("map_elements", rect, Element(map, layer, "rect")),
            Insert("map_elements", line, Element(map, layer, "line", more: new JsonObject
            {
                ["points"] = new JsonArray(new JsonArray(0, 0), new JsonArray(100, 50)), ["startElementId"] = rect.ToString(), ["endElementId"] = card.ToString(),
            })),
            Insert("map_elements", Ids.New(), Element(map, layer, "item", more: new JsonObject { ["itemId"] = secret.ToString() })),
            Insert("map_elements", Ids.New(), Element(matrix, matrixLayer, "item", more: new JsonObject { ["itemId"] = note.ToString() })),
            Insert("map_elements", Ids.New(), Element(map, layer, "rect", 2_000_000)),
            Insert("map_elements", Ids.New(), Element(map, matrixLayer, "rect")),
            Insert("files", file, new JsonObject
            {
                ["mapId"] = map.ToString(), ["purpose"] = "map_image", ["fileName"] = "photo.png", ["contentType"] = "image/png", ["sizeBytes"] = 10,
                ["sha256"] = new string('c', 64),
            }),
            Insert("files", foreign, new JsonObject
            {
                ["mapId"] = matrix.ToString(), ["purpose"] = "map_image", ["fileName"] = "other.png", ["contentType"] = "image/png", ["sizeBytes"] = 10,
                ["sha256"] = new string('d', 64),
            }),
            Insert("map_elements", image, Element(map, layer, "image", more: new JsonObject { ["fileId"] = file.ToString() })),
            Insert("map_elements", Ids.New(), Element(map, layer, "image", more: new JsonObject { ["fileId"] = foreign.ToString() })),
        ]);
        CollectionAssert.AreEqual(new[]
        {
            "accepted", "accepted", "accepted", "accepted", "accepted", "accepted", "accepted", "accepted", "parent_missing", "validation_failed",
            "validation_failed", "validation_failed", "accepted", "accepted", "accepted", "validation_failed",
        }, Codes(answer), answer["results"]!.ToJsonString());
        var stored = client.Row("map_elements", card)!;
        Assert.AreEqual(10.46, stored["x"]!.GetValue<double>(), "rounded to two decimals");
        Assert.AreEqual("manage", client.Row("maps", map).Str("access"));
        // The last layer stays.
        var keep = await client.SyncAllAsync([Delete("map_layers", matrixLayer, 1)]);
        Assert.AreEqual("validation_failed", Codes(keep)[0]);
        // Without Maps: Create no map; a template needs Configuration: Manage.
        var noMaps = new SyncClient(app.ClientFor(owner, "Notes:Create"));
        var refused = await noMaps.SyncAllAsync([Insert("maps", Ids.New(), Map("No right"))]);
        Assert.AreEqual("missing_right", Codes(refused)[0]);
        var template = Map("Template");
        template["isTemplate"] = true;
        Assert.AreEqual("missing_right", Codes(await client.SyncAllAsync([Insert("maps", Ids.New(), template)]))[0]);
    }

    [TestMethod]
    public async Task SharingAMapDeliversItAndUnsharingEvictsIt()
    {
        var team = await GroupAsync(app, "maps");
        var owner = await UserAsync(app, "mapowner");
        var member = await UserAsync(app, "mapmember", team);
        var ownerClient = new SyncClient(app.ClientFor(owner, Rights));
        var memberClient = new SyncClient(app.ClientFor(member, Rights));
        var (map, layer, element) = (Ids.New(), Ids.New(), Ids.New());
        await ownerClient.SyncAllAsync([Insert("maps", map, Map("Shared later")), Insert("map_layers", layer, Layer(map, "Base")),
            Insert("map_elements", element, Element(map, layer, "text", more: new JsonObject { ["text"] = "Hello" }))]);
        await memberClient.SyncAllAsync();
        Assert.IsNull(memberClient.Row("maps", map));

        using (var shared = await ownerClient.Http.PutJsonAsync($"/api/v1/maps/{map}/shares",
            new JsonArray(new JsonObject { ["groupId"] = team.ToString(), ["access"] = "edit" })))
        {
            Assert.AreEqual(HttpStatusCode.OK, shared.StatusCode, await shared.Content.ReadAsStringAsync());
        }
        await memberClient.SyncAllAsync();
        Assert.AreEqual("edit", memberClient.Row("maps", map).Str("access"));
        Assert.IsNotNull(memberClient.Row("map_elements", element), "the re-stamped subtree");
        // The member edits; cannot share (manage).
        var moved = await memberClient.SyncAllAsync([Patch("map_elements", element, 1, ("x", 0, 40), ("style.fill", null, "#fde68a"))]);
        Assert.AreEqual("accepted", Codes(moved)[0]);
        using (var refused = await memberClient.Http.PutJsonAsync($"/api/v1/maps/{map}/shares", new JsonArray()))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using (var unshared = await ownerClient.Http.PutJsonAsync($"/api/v1/maps/{map}/shares", new JsonArray()))
        {
            Assert.AreEqual(HttpStatusCode.OK, unshared.StatusCode);
        }
        var answer = await memberClient.SyncAsync();
        Assert.IsTrue(answer.Arr("evictions").Any(e => e.Str("kind") == "map" && e.Str("id") == map.ToString()), answer.ToJsonString());
        // Owner transfer: the old owner is evicted.
        using (var transfer = await ownerClient.Http.PostJsonNodeAsync($"/api/v1/maps/{map}/owner", new JsonObject { ["userId"] = member.ToString() }))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, transfer.StatusCode);
        }
        var old = await ownerClient.SyncAsync();
        Assert.IsTrue(old.Arr("evictions").Any(e => e.Str("id") == map.ToString()));
    }

    [TestMethod]
    public async Task ThePreviewIsTheLastWritersAndNoHistory()
    {
        var team = await GroupAsync(app, "previews");
        var (ana, ben) = (await UserAsync(app, "previewana", team), await UserAsync(app, "previewben", team));
        var (anaClient, benClient) = (new SyncClient(app.ClientFor(ana, Rights)), new SyncClient(app.ClientFor(ben, Rights)));
        var (map, layer) = (Ids.New(), Ids.New());
        await anaClient.SyncAllAsync([Insert("maps", map, Map("Previewed")), Insert("map_layers", layer, Layer(map, "Base"))]);
        using (var shared = await anaClient.Http.PutJsonAsync($"/api/v1/maps/{map}/shares",
            new JsonArray(new JsonObject { ["groupId"] = team.ToString(), ["access"] = "edit" })))
        {
            Assert.AreEqual(HttpStatusCode.OK, shared.StatusCode);
        }
        await benClient.SyncAllAsync();
        var (first, second) = (Convert.ToBase64String([1, 2, 3]), Convert.ToBase64String([4, 5, 6]));
        // Both left the map at once: each patch's old is the null both had seen – no conflict, the later one stays.
        Assert.AreEqual("accepted", Codes(await anaClient.SyncAllAsync([Patch("maps", map, 1, ("preview", null, first), ("name", "Previewed", "Renamed"))]))[0]);
        var answer = await benClient.SyncAllAsync([Patch("maps", map, 1, ("preview", null, second))]);
        Assert.AreEqual("accepted", Codes(answer)[0], answer.ToJsonString());
        Assert.AreEqual(0, answer.Arr("conflicts").Count);
        await anaClient.SyncAllAsync();
        Assert.AreEqual(second, anaClient.Row("maps", map).Str("preview"));
        // Too big is refused; the History has the rename, not the pictures.
        var big = Convert.ToBase64String(new byte[256 * 1024 + 1]);
        Assert.AreEqual("validation_failed", Codes(await benClient.SyncAllAsync([Patch("maps", map, 3, ("preview", second, big))]))[0]);
        await using var db = app.NewDb();
        var history = await db.ChangeLog.Where(c => c.Entity == "maps" && c.EntityId == map).Select(c => c.Changes).ToListAsync();
        Assert.IsTrue(history.Any(c => c.Contains("Renamed")), string.Join("\n", history));
        Assert.IsFalse(history.Any(c => c.Contains("\"preview\"")), string.Join("\n", history));
        // isTemplate is patched with Configuration: Manage only.
        Assert.AreEqual("missing_right", Codes(await anaClient.SyncAllAsync([Patch("maps", map, 3, ("isTemplate", false, true))]))[0]);
        var manager = new SyncClient(app.ClientFor(ana, Rights + ",Configuration:Manage"));
        Assert.AreEqual("accepted", Codes(await manager.SyncAllAsync([Patch("maps", map, 3, ("isTemplate", false, true))]))[0]);
    }

    [TestMethod]
    public async Task TemplatesReachEverybodyAndDeletedMapsAreInTheTrash()
    {
        var designer = await UserAsync(app, "designer");
        var client = new SyncClient(app.ClientFor(designer, "Maps:Create,Configuration:Manage"));
        var (template, layer) = (Ids.New(), Ids.New());
        var row = Map("Workshop");
        row["isTemplate"] = true;
        await client.SyncAllAsync([Insert("maps", template, row), Insert("map_layers", layer, Layer(template, "Frames"))]);
        var anybody = new SyncClient(app.ClientFor(await UserAsync(app, "anybody"), ""));
        await anybody.SyncAllAsync();
        Assert.AreEqual("read", anybody.Row("maps", template).Str("access"));
        Assert.IsNotNull(anybody.Row("map_layers", layer));

        var (map, element, note) = (Ids.New(), Ids.New(), Ids.New());
        var mapLayer = Ids.New();
        await client.SyncAllAsync([Insert("maps", map, Map("Doomed")), Insert("map_layers", mapLayer, Layer(map, "Base")),
            Insert("map_elements", element, Element(map, mapLayer, "rect"))]);
        await client.SyncAllAsync([Delete("maps", map, 1)]);
        Assert.IsNull(client.Row("map_elements", element));
        using var trash = await client.Http.GetAsync("/api/v1/trash");
        var entry = (await trash.JsonAsync()).Arr("items").Single(i => i.Str("rootId") == map.ToString())!;
        Assert.AreEqual(("map", "canvas", "Doomed"), (entry.Str("rootKind"), entry.Str("rootType"), entry.Str("title")));
        using var restored = await client.Http.PostEmptyAsync($"/api/v1/trash/{entry.Str("batchId")}/restore");
        Assert.AreEqual(3, (await restored.JsonAsync()).Int("restored"));
        await client.SyncAllAsync();
        Assert.IsNotNull(client.Row("map_elements", element));
        _ = note;
    }

    [TestMethod]
    public async Task DeletingAnItemTakesItsCardsAndRestoreReturnsThem()
    {
        var owner = await UserAsync(app, "cards");
        var client = new SyncClient(app.ClientFor(owner, Rights));
        var (map, layer, note, card) = (Ids.New(), Ids.New(), Ids.New(), Ids.New());
        await client.SyncAllAsync([Insert("maps", map, Map("Cards")), Insert("map_layers", layer, Layer(map, "Base")), Insert("items", note, Note("Card")),
            Insert("map_elements", card, Element(map, layer, "item", more: new JsonObject { ["itemId"] = note.ToString() }))]);
        await client.SyncAllAsync([Delete("items", note, client.Row("items", note).Int("rev"))]);
        Assert.IsNull(client.Row("map_elements", card), "the card goes with its item");
        await using var db = app.NewDb();
        var batch = await db.Items.Where(i => i.Id == note).Select(i => i.DeleteBatchId).SingleAsync();
        using var restored = await client.Http.PostEmptyAsync($"/api/v1/trash/{batch}/restore");
        Assert.AreEqual(2, (await restored.JsonAsync()).Int("restored"));
        await client.SyncAllAsync();
        Assert.IsNotNull(client.Row("map_elements", card));
    }
}

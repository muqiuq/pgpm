using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Config;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Maps;
using PGPM.Sync;
using PGPM.Tests.Content;
using PGPM.Tests.Sync;
using static PGPM.Tests.Mcp.McpKit;
using static PGPM.Tests.Mcp.McpToolKit;

namespace PGPM.Tests.Mcp;

/// <summary>
/// The map tools (docs/03 §7.3): maps as their sharing shows them, capped at the token's kind; cards added to a canvas as
/// <i>Add to map…</i> does; matrix maps computed as the devices compute them – from the values and scores the user sees.
/// </summary>
[TestClass]
public sealed class McpMapToolTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task MapsAreSeenAsTheyAreSharedAndCappedAtTheTokensKind()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-maps");
        var owner = await MemberAsync(app, "map-owner", group);
        var (_, readToken, writeToken) = await WriterAsync(app, "map-reader", group);
        var outsider = await MemberAsync(app, "map-outsider");
        var tag = ContentKit.Tag();
        var (map, layer) = await MapAsync(app, owner, $"Board {tag}", null, "read", group);
        var visible = await ItemAsync(app, owner, "note", "On the board", null, "read", group);
        var secret = await ItemAsync(app, owner, "note", "Only the owner's", null);
        var (card, text) = (Ids.New(), Ids.New());
        await SyncAsync(app, owner,
            SyncKit.Insert("map_elements", card, Element(map, layer, "item", 10, 20, new JsonObject { ["itemId"] = visible.ToString() })),
            SyncKit.Insert("map_elements", Ids.New(), Element(map, layer, "item", 300, 20, new JsonObject { ["itemId"] = secret.ToString() })),
            SyncKit.Insert("map_elements", text, Element(map, layer, "text", 10, 200, new JsonObject { ["text"] = "Customer journey" })));

        await using (var client = await ClientAsync(app, readToken))
        {
            var listed = await OkAsync(client, "list_maps", new { text = tag });
            var entry = listed.Each("items").Single();
            Assert.AreEqual((map.ToString(), "read", 3), (entry.S("id"), entry.S("access"), entry.GetProperty("elements").GetInt32()));
            var detail = await OkAsync(client, "get_map", new { id = map.ToString() });
            Assert.AreEqual(("canvas", 1, 3), (detail.S("kind"), detail.GetProperty("layers").GetArrayLength(), detail.GetProperty("elementsTotal").GetInt32()));
            Assert.AreEqual(2, detail.GetProperty("elementCounts").GetProperty("item").GetInt32());
            var items = detail.Each("elements").Where(e => e.S("kind") == "item").ToDictionary(e => e.S("id")!, e => e.GetProperty("item"));
            Assert.AreEqual("On the board", items[card.ToString()].S("title"));
            Assert.IsTrue(items.Values.Any(i => i.S("access") == "none" && i.Missing("title")), "a card of an item the user can't read is only its id");
            Assert.AreEqual("Customer journey", detail.Each("elements").Single(e => e.S("id") == text.ToString()).S("text"));
        }
        await using (var client = await ClientAsync(app, writeToken))
        {
            Assert.AreEqual("missing_access", (await ErrorAsync(client, "add_item_to_map", new { map = map.ToString(), item = visible.ToString() })).Code(),
                "read on the map");
        }
        var ownerToken = await WriterAsync(app, "map-owner-2", group);
        var (ownMap, _) = await MapAsync(app, ownerToken.User, $"Own {tag}");
        await using (var client = await ClientAsync(app, ownerToken.Read))
        {
            var own = (await OkAsync(client, "list_maps", new { text = tag })).Each("items").Single(m => m.S("id") == ownMap.ToString());
            Assert.AreEqual("read", own.S("access"), "the owner's manage, capped at read");
        }
        await using (var client = await ClientAsync(app, ownerToken.Write))
        {
            var own = (await OkAsync(client, "get_map", new { id = ownMap.ToString() }));
            Assert.AreEqual("edit", own.S("access"), "the owner's manage, capped at edit");
        }

        var stranger = await ClientAsync(app, (await TurnOnAsync(app, outsider, "read")).Value!);
        await using (stranger)
        {
            Assert.AreEqual(0, (await OkAsync(stranger, "list_maps", new { text = tag })).GetProperty("total").GetInt32());
            Assert.AreEqual("not_found", (await ErrorAsync(stranger, "get_map", new { id = map.ToString() })).Code(), "an unshared map's elements stay hidden");
        }
    }

    [TestMethod]
    public async Task AddItemToMapPlacesACardAsTheBrowserDoes()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-cards");
        var (user, read, write) = await WriterAsync(app, "card-writer", group);
        var owner = await MemberAsync(app, "card-owner", group);
        var stranger = await MemberAsync(app, "card-stranger");
        var (map, layer) = await MapAsync(app, owner, "Shared board", null, "edit", group);
        var anchor = Ids.New();
        await SyncAsync(app, owner, SyncKit.Insert("map_elements", anchor, Element(map, layer, "rect", 100, 100)));
        var top = Ids.New();
        await SyncAsync(app, owner, SyncKit.Insert("map_layers", top, new JsonObject
        {
            ["mapId"] = map.ToString(), ["name"] = "Notes", ["sortOrder"] = "a1", ["isVisible"] = true, ["isLocked"] = false,
        }));
        var item = await ItemAsync(app, user, "note", "Place me", null);
        var secret = await ItemAsync(app, stranger, "note", "Not yours", null);
        await using var client = await ClientAsync(app, write);

        var near = await OkAsync(client, "add_item_to_map", new { map = map.ToString(), item = item.ToString(), near = anchor.ToString() });
        Assert.AreEqual((240d, 100d, top.ToString()), (near.GetProperty("x").GetDouble(), near.GetProperty("y").GetDouble(), near.S("layerId")),
            "right of the element, on the top visible layer");
        var placed = await OkAsync(client, "add_item_to_map", new { map = map.ToString(), item = item.ToString(), x = 12.345, y = -50, layer = "base" });
        Assert.AreEqual((12.35d, -50d, layer.ToString()), (placed.GetProperty("x").GetDouble(), placed.GetProperty("y").GetDouble(), placed.S("layerId")),
            "an item may be on a map any number of times");
        var centred = await OkAsync(client, "add_item_to_map", new { map = map.ToString(), item = item.ToString() });
        Assert.AreEqual(220d, centred.GetProperty("width").GetDouble());

        await using (var db = app.NewDb())
        {
            var cards = await db.MapElements.Where(e => e.MapId == map && e.ItemId == item).ToListAsync();
            Assert.AreEqual(3, cards.Count);
            var onTop = cards.Where(c => c.LayerId == top).OrderBy(c => c.Z).Select(c => c.Z).ToList();
            CollectionAssert.AreEqual(new[] { "a0", "a1" }, onTop, "each on top of its layer");
            Assert.IsTrue(await db.ChangeLog.AnyAsync(c => c.MapId == map && c.Source == ChangeSource.Mcp && c.Action == "create"));
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.McpMapChanged && l.SubjectId == map));
        }
        Assert.AreEqual("not_found", (await ErrorAsync(client, "add_item_to_map", new { map = map.ToString(), item = secret.ToString() })).Code());
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "add_item_to_map", new { map = map.ToString(), item = item.ToString(), x = 1 })).Code());
        var (matrix, _) = await MapAsync(app, user, "Matrix", new JsonObject
        {
            ["x"] = new JsonObject { ["field"] = "x", ["min"] = 0, ["max"] = 1 }, ["y"] = new JsonObject { ["field"] = "y", ["min"] = 0, ["max"] = 1 },
        });
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "add_item_to_map", new { map = matrix.ToString(), item = item.ToString() })).Code());
        await using var reader = await ClientAsync(app, read);
        Assert.AreEqual("not_available", (await ErrorAsync(reader, "add_item_to_map", new { map = map.ToString(), item = item.ToString() })).Code());
    }

    [TestMethod]
    public async Task AMatrixMapPlacesItsItemsByTheScoresTheUserSees()
    {
        var team = await SyncKit.GroupAsync(app, "mcp-matrix");
        var analysts = await SyncKit.GroupAsync(app, "mcp-analysts");
        var (user, read, write) = await WriterAsync(app, "matrix-reader", team, analysts);
        var outsider = await MemberAsync(app, "matrix-outsider", team);
        var status = await ContentKit.StatusAsync(app, ItemType.Idea);
        var feasibility = await ContentKit.SectionAsync(app, ["idea"]);
        var ratings = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ratings.Add((await ContentKit.FieldAsync(app, feasibility, FieldType.Rating, new JsonObject { ["max"] = 5 })).Key);
        }
        var (_, score) = await ContentKit.FieldAsync(app, feasibility, FieldType.Score, new JsonObject
        {
            ["op"] = "sum", ["inputs"] = new JsonArray([.. ratings.Select(r => (JsonNode)r)]), ["max"] = 15,
        });
        var impactSection = await ContentKit.SectionAsync(app, ["idea"], (analysts, SectionAccess.View));
        var (_, impact) = await ContentKit.FieldAsync(app, impactSection, FieldType.Number, new JsonObject { ["min"] = 0, ["max"] = 100 });
        var (map, _) = await MapAsync(app, user, "Impact field", new JsonObject
        {
            ["x"] = new JsonObject { ["field"] = score, ["label"] = "Ease of implementation", ["min"] = 0, ["max"] = 15, ["split"] = 7.5 },
            ["y"] = new JsonObject { ["field"] = impact, ["label"] = "Business impact", ["min"] = 0, ["max"] = 100, ["split"] = 50 },
            ["quadrants"] = new JsonObject { ["tr"] = "Star initiatives", ["br"] = "Quick wins" },
            ["filter"] = new JsonObject { ["types"] = new JsonArray("idea"), ["groupIds"] = new JsonArray(team.ToString()) },
        }, "read", team);
        async Task<Guid> IdeaAsync(string title, int? each, int? value)
        {
            var id = await ItemAsync(app, user, "idea", title, status, "read", team);
            var fields = new Dictionary<string, object>();
            if (each is { } e)
            {
                foreach (var r in ratings)
                {
                    fields[r] = e;
                }
            }
            if (value is { } v)
            {
                fields[impact] = v;
            }
            if (fields.Count > 0)
            {
                await using var writer = await ClientAsync(app, write);
                await OkAsync(writer, "update_item", new { id = id.ToString(), fields });
            }
            return id;
        }
        var star = await IdeaAsync("Star", 4, 80);
        var limited = await IdeaAsync("Limited", 1, 20);
        var quick = await IdeaAsync("Quick", 5, 10);
        var tray = await IdeaAsync("Unassessed", null, 60);
        var unshared = await ItemAsync(app, user, "idea", "Not in the filter", status);
        await using var client = await ClientAsync(app, read);

        var detail = await OkAsync(client, "get_map", new { id = map.ToString() });
        var matrix = detail.GetProperty("matrix");
        Assert.AreEqual(("Ease of implementation", 7.5), (matrix.GetProperty("x").S("label"), matrix.GetProperty("x").GetProperty("split").GetDouble()));
        Assert.AreEqual("Limited interest", matrix.GetProperty("quadrants").S("bl"), "the default label where the map names none");
        var cards = matrix.Each("cards").ToDictionary(c => c.S("id")!);
        Assert.AreEqual(("tr", "Star initiatives", 12m, 80m), (cards[star.ToString()].S("quadrant"), cards[star.ToString()].S("quadrantLabel"),
            cards[star.ToString()].GetProperty("x").GetDecimal(), cards[star.ToString()].GetProperty("y").GetDecimal()));
        Assert.AreEqual(("bl", "br"), (cards[limited.ToString()].S("quadrant"), cards[quick.ToString()].S("quadrant")));
        var position = cards[quick.ToString()].GetProperty("position");
        Assert.AreEqual((1d, 0.1d), (position.GetProperty("x").GetDouble(), position.GetProperty("y").GetDouble()));
        var point = cards[quick.ToString()].GetProperty("point");
        Assert.AreEqual((1900d, 1180d), (point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble()), "the world point its card aims at");
        CollectionAssert.AreEqual(new[] { tray.ToString() }, matrix.Each("tray").Select(t => t.S("id")).ToList(), "not yet assessed");
        Assert.IsFalse(cards.ContainsKey(unshared.ToString()), "the filter's groups");

        var stars = await OkAsync(client, "search_items", new { map = map.ToString(), quadrant = "tr" });
        Assert.AreEqual((star.ToString(), "tr"), (stars.Each("items").Single().S("id"), stars.Each("items").Single().S("quadrant")));
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "search_items", new { quadrant = "tr" })).Code());

        // Without the impact section, the y axis has no values: every card is in the tray.
        await using var other = await ClientAsync(app, (await TurnOnAsync(app, outsider, "read")).Value!);
        var hidden = (await OkAsync(other, "get_map", new { id = map.ToString() })).GetProperty("matrix");
        Assert.AreEqual((0, false), (hidden.GetProperty("cards").GetArrayLength(), hidden.GetProperty("y").GetProperty("visible").GetBoolean()));
        Assert.AreEqual(4, hidden.GetProperty("tray").GetArrayLength());
        await using var db = app.NewDb();
        Assert.IsTrue(await db.Maps.AnyAsync(m => m.Id == map && m.Kind == MapKind.Matrix));
    }
}

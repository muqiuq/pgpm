using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PGPM.Lib.Helper;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>
/// <c>POST /api/v1/sync</c> end to end (docs/03 §11.13): push and pull round trips, per-field conflicts, delete against edit
/// both ways, duplicate pairs, replays, refusals, what each user sees, paging. Each test has users and a group of its own.
/// </summary>
[TestClass]
public sealed class SyncPushPullTests
{
    private const string Notes = "Notes:Create";

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<(Guid Group, Guid A, Guid B, SyncClient ClientA, SyncClient ClientB)> TwoMembersAsync()
    {
        var group = await GroupAsync(app, "team");
        var a = await UserAsync(app, "anna", group);
        var b = await UserAsync(app, "ben", group);
        var clientA = new SyncClient(app.ClientFor(a, Notes));
        var clientB = new SyncClient(app.ClientFor(b, Notes));
        await clientA.SyncAllAsync();
        await clientB.SyncAllAsync();
        return (group, a, b, clientA, clientB);
    }

    [TestMethod]
    public async Task ANoteWithItsSharesRoundTrips()
    {
        var (group, a, _, clientA, clientB) = await TwoMembersAsync();
        var id = Ids.New();
        var batch = Ids.New();
        var shareId = Ids.New();
        var answer = await clientA.SyncAllAsync([Insert("items", id, Note("Kick-off", "Agenda"), batch), Insert("item_shares", shareId, Share(id, group, "edit"), batch)]);

        var results = answer.Arr("results");
        Assert.AreEqual("accepted", results[0].Str("status"), results.ToJsonString());
        Assert.AreEqual("accepted", results[1].Str("status"), results.ToJsonString());
        Assert.AreEqual(1, results[0].Int("rev"));
        var row = clientA.Row("items", id)!;
        Assert.AreEqual("note", row.Str("type"));
        Assert.AreEqual("Kick-off", row.Str("title"));
        Assert.AreEqual(a.ToString(), row.Str("ownerUserId"));
        Assert.AreEqual("manage", row.Str("access"));
        Assert.IsGreaterThan(0, row.Int("number"));
        Assert.AreEqual(a.ToString(), row.Str("createdBy"));
        Assert.AreEqual(results[0].Long("seq"), row.Long("seq"));
        Assert.IsTrue(row.Str("createdAt")!.EndsWith('Z'), row.Str("createdAt"));
        Assert.AreEqual(group.ToString(), clientA.Row("item_shares", shareId).Str("groupId"));
        Assert.AreEqual(answer.Long("head"), clientA.Cursor);

        await clientB.SyncAllAsync();
        Assert.AreEqual("edit", clientB.Row("items", id).Str("access"));
        Assert.AreEqual("edit", clientB.Row("item_shares", shareId).Str("access"));

        await using var db = app.NewDb();
        var log = await db.ChangeLog.Where(c => c.ItemId == id).Select(c => c.Action).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { "create", "share" }, log);
    }

    [TestMethod]
    public async Task ConflictingFieldsAreHeldBackTheOthersApplied()
    {
        var (group, _, b, clientA, clientB) = await TwoMembersAsync();
        var id = Ids.New();
        var batch = Ids.New();
        await clientA.SyncAllAsync([Insert("items", id, Note("Website", "Draft"), batch), Insert("item_shares", Ids.New(), Share(id, group, "edit"), batch)]);
        await clientB.SyncAllAsync();

        // B changes the title first; A, still on rev 1, changes title and description.
        var byB = await clientB.SyncAllAsync([Patch("items", id, 1, ("title", "Website", "Website 2027"))]);
        Assert.AreEqual("accepted", byB.Arr("results")[0].Str("status"));
        var partial = await clientA.SyncAllAsync([Patch("items", id, 1, ("title", "Website", "Website relaunch"), ("description", "Draft", "Plan"))]);
        var result = partial.Arr("results")[0]!;
        Assert.AreEqual("partial", result.Str("status"), result.ToJsonString());
        CollectionAssert.AreEqual(new[] { "title" }, result.Arr("conflictFields").Select(f => f!.GetValue<string>()).ToList());
        Assert.AreEqual(3, result.Int("rev"));
        var conflict = partial.Arr("conflicts")[0]!;
        Assert.AreEqual("Website 2027", conflict.Str("theirs"));
        Assert.AreEqual(await DisplayNameAsync(app, b), conflict.Str("by"));
        Assert.AreEqual("items", conflict.Str("table"));
        Assert.AreEqual("title", conflict.Str("field"));
        Assert.AreEqual(3, conflict.Int("rev"));
        Assert.AreEqual("Website 2027", clientA.Row("items", id).Str("title"));
        Assert.AreEqual("Plan", clientA.Row("items", id).Str("description"));

        // Only a conflicting key: nothing applied, the row as current.
        var onlyConflict = await clientA.SyncAllAsync([Patch("items", id, 1, ("title", "Website", "Mine"))]);
        result = onlyConflict.Arr("results")[0]!;
        Assert.AreEqual("conflict", result.Str("status"));
        Assert.AreEqual("field", result.Str("kind"));
        Assert.AreEqual("Website 2027", result["current"].Str("title"));
        Assert.AreEqual(3, result.Int("rev"));

        // The value it already has: nothing to do, accepted.
        var noOp = await clientA.SyncAllAsync([Patch("items", id, 1, ("title", "Anything", "Website 2027"))]);
        Assert.AreEqual("accepted", noOp.Arr("results")[0].Str("status"));
        Assert.AreEqual(3, noOp.Arr("results")[0].Int("rev"));

        // The decisions are recorded: a field's, and a delete conflict's (field null: the whole row).
        await clientA.SyncAllAsync(resolved: [
            new JsonObject { ["table"] = "items", ["id"] = id.ToString(), ["field"] = "title", ["resolution"] = "theirs" },
            new JsonObject { ["table"] = "items", ["id"] = id.ToString(), ["field"] = null, ["resolution"] = "mine" },
        ]);
        await using var db = app.NewDb();
        var log = await db.ChangeLog.Where(c => c.ItemId == id).OrderBy(c => c.Id).Select(c => new { c.Action, c.Changes }).ToListAsync();
        CollectionAssert.AreEqual(new[] { "create", "share", "update", "update", "conflict", "conflict", "conflict_resolved", "conflict_resolved" },
            log.Select(l => l.Action).ToList());
        Assert.AreEqual(JsonValueKind.Null, JsonDocument.Parse(log[^1].Changes).RootElement.GetProperty("field").ValueKind);

        // Timestamps on the wire: UTC with microseconds, always six digits (docs/03 §11.4).
        StringAssert.Matches(clientA.Row("items", id).Str("updatedAt"), new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$"));
    }

    [TestMethod]
    public async Task DeleteAgainstEditBothWays()
    {
        var (group, _, _, clientA, clientB) = await TwoMembersAsync();
        var id = Ids.New();
        var batch = Ids.New();
        await clientA.SyncAllAsync([Insert("items", id, Note("Budget"), batch), Insert("item_shares", Ids.New(), Share(id, group, "edit"), batch)]);
        await clientB.SyncAllAsync();

        // B edits, A deletes what it saw (rev 1): changed since.
        await clientB.SyncAllAsync([Patch("items", id, 1, ("title", "Budget", "Budget 2027"))]);
        var stale = await clientA.SyncAsync([Delete("items", id, 1)], pull: false, apply: false);
        var result = stale.Arr("results")[0]!;
        Assert.AreEqual("conflict", result.Str("status"));
        Assert.AreEqual("changed_since_delete", result.Str("kind"));
        Assert.AreEqual("Budget 2027", result["current"].Str("title"));

        // Delete anyway.
        var forced = await clientA.SyncAllAsync([Delete("items", id, 1, force: true)]);
        Assert.AreEqual("accepted", forced.Arr("results")[0].Str("status"));
        Assert.IsNull(clientA.Row("items", id));

        // B edits the deleted note: deleted by another, with the batch to restore.
        var late = await clientB.SyncAsync([Patch("items", id, 2, ("title", "Budget 2027", "Budget 2028"))], pull: false, apply: false);
        result = late.Arr("results")[0]!;
        Assert.AreEqual("conflict", result.Str("status"));
        Assert.AreEqual("deleted_by_other", result.Str("kind"));
        Assert.IsTrue(result["current"].Bool("deleted"));
        Assert.IsNotNull(result["current"].Str("deleteBatchId"));

        // The tombstone reaches B.
        await clientB.SyncAllAsync();
        Assert.IsNull(clientB.Row("items", id));

        // A delete of a deleted row is done already.
        var again = await clientB.SyncAsync([Delete("items", id, 3)], pull: false, apply: false);
        Assert.AreEqual("accepted", again.Arr("results")[0].Str("status"));
    }

    [TestMethod]
    public async Task DuplicatePairsAreMerged()
    {
        var (group, a, _, clientA, _) = await TwoMembersAsync();
        var id = Ids.New();
        var batch = Ids.New();
        var first = Ids.New();
        var second = Ids.New();
        var answer = await clientA.SyncAllAsync([Insert("items", id, Note("Pairs"), batch), Insert("item_shares", first, Share(id, group, "read"), batch),
            Insert("item_shares", second, Share(id, group, "edit"), batch)]);
        var merged = answer.Arr("results")[2]!;
        Assert.AreEqual("accepted", merged.Str("status"), merged.ToJsonString());
        Assert.AreEqual(second.ToString(), merged.Str("id"));
        Assert.AreEqual(first.ToString(), merged.Str("mergedInto"));
        Assert.IsNull(clientA.Row("item_shares", second));

        // A setting inserted on two devices: the later value wins, in the first row.
        var device2 = new SyncClient(app.ClientFor(a, Notes));
        await device2.SyncAllAsync();
        var setting1 = Ids.New();
        var setting2 = Ids.New();
        await clientA.SyncAllAsync([Insert("user_settings", setting1, new JsonObject { ["key"] = "list:note", ["value"] = new JsonObject { ["search"] = "a" } })]);
        var later = await device2.SyncAllAsync([Insert("user_settings", setting2, new JsonObject { ["key"] = "list:note", ["value"] = new JsonObject { ["search"] = "b" } })]);
        Assert.AreEqual(setting1.ToString(), later.Arr("results")[0].Str("mergedInto"), later.ToJsonString());
        Assert.AreEqual("b", device2.Row("user_settings", setting1)!["value"].Str("search"));
        Assert.AreEqual(a.ToString(), device2.Row("user_settings", setting1).Str("userId"));
    }

    [TestMethod]
    public async Task AKnownMutationIsAnsweredWithTheStoredResult()
    {
        var (_, _, _, clientA, _) = await TwoMembersAsync();
        var id = Ids.New();
        var insert = Insert("items", id, Note("Once"));
        var first = await clientA.SyncAsync([insert], pull: false, apply: false);
        var second = await clientA.SyncAsync([insert], pull: false, apply: false);
        Assert.AreEqual(first.Arr("results").ToJsonString(), second.Arr("results").ToJsonString());
        Assert.AreEqual("accepted", second.Arr("results")[0].Str("status"));
        await using var db = app.NewDb();
        Assert.AreEqual(1, await db.Items.CountAsync(i => i.Id == id));

        // Another user can't take the mutation id.
        var other = new SyncClient(app.ClientFor(await UserAsync(app, "eve"), Notes));
        var stolen = await other.SyncAsync([insert], pull: false, apply: false);
        Assert.AreEqual("duplicate_id", stolen.Arr("results")[0].Str("code"));
    }

    [TestMethod]
    public async Task RefusedMutationsNeverAbortTheBatch()
    {
        var (group, a, _, clientA, _) = await TwoMembersAsync();
        var own = Ids.New();
        await clientA.SyncAllAsync([Insert("items", own, Note("Own"))]);
        var stranger = await UserAsync(app, "stranger");
        var strangerNote = Ids.New();
        await new SyncClient(app.ClientFor(stranger, Notes)).SyncAllAsync([Insert("items", strangerNote, Note("Private"))]);
        var groupId = group.ToString();
        var person = Ids.New();
        var okay = Ids.New();

        var answer = await clientA.SyncAsync(
        [
            Insert("items", Ids.New(), new JsonObject { ["type"] = "idea", ["title"] = "Later", ["statusId"] = Ids.New().ToString() }),
            Insert("items", Guid.NewGuid(), Note("Not v7")),
            Insert("items", Ids.New(), Note(" ")),
            Patch("items", strangerNote, 1, ("title", "Private", "Mine")),
            Patch("items", own, 1, ("number", 1, 2)),
            Patch("groups", group, 1, ("name", "x", "y")),
            Insert("groups", Ids.New(), new JsonObject { ["name"] = "Mine" }),
            Insert("item_shares", Ids.New(), Share(own, group, "edit")),
            Insert("nothing", Ids.New(), new JsonObject()),
            Insert("persons", person, new JsonObject { ["displayName"] = "Extern" }),
            Insert("items", Ids.New(), new JsonObject { ["type"] = "note", ["title"] = "Bad colour", ["color"] = "red" }),
            Insert("items", okay, Note("Fine")),
        ], pull: false, apply: false);
        var codes = answer.Arr("results").Select(r => r.Str("code") ?? r.Str("status")).ToList();
        CollectionAssert.AreEqual(new[]
        {
            "missing_right", "validation_failed", "validation_failed", "missing_access", "not_patchable", "not_patchable", "not_supported",
            "missing_access", "not_supported", "missing_right", "validation_failed", "accepted",
        }, codes, string.Join(", ", codes));
        Assert.IsNotNull(answer.Arr("results")[1]!["errors"]!["id"]);
        Assert.IsNotNull(answer.Arr("results")[2]!["errors"]!["title"]);
        Assert.IsNull(answer.Arr("results")[3]!["current"], "a row the user can't see is not shown");
        Assert.AreEqual("Own", answer.Arr("results")[4]!["current"].Str("title"));
        _ = groupId;

        // Without Notes: Create; with Persons: Edit a manual person.
        var noRights = new SyncClient(app.ClientFor(a, ""));
        var refused = await noRights.SyncAsync([Insert("items", Ids.New(), Note("No right"))], pull: false, apply: false);
        Assert.AreEqual("missing_right", refused.Arr("results")[0].Str("code"));
        var editor = new SyncClient(app.ClientFor(a, "Persons:Edit"));
        var created = await editor.SyncAllAsync([Insert("persons", person, new JsonObject { ["displayName"] = "Extern", ["email"] = "Ext@Example.COM", ["source"] = "microsoft" })]);
        Assert.AreEqual("accepted", created.Arr("results")[0].Str("status"));
        Assert.AreEqual("manual", editor.Row("persons", person).Str("source"));
        Assert.AreEqual("ext@example.com", editor.Row("persons", person).Str("email"));

        // Too many in one request.
        var tooMany = Enumerable.Range(0, 201).Select(_ => Insert("items", Ids.New(), Note("x"))).ToList();
        var body = new JsonObject { ["clientId"] = Ids.New().ToString(), ["since"] = 0, ["mutations"] = new JsonArray([.. tooMany]) };
        using var response = await clientA.Http.PostJsonNodeAsync("/api/v1/sync", body);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task EachUserSeesWhatTheyMay()
    {
        var (_, a, b, clientA, clientB) = await TwoMembersAsync();
        var privateNote = Ids.New();
        var setting = Ids.New();
        await clientA.SyncAllAsync([Insert("items", privateNote, Note("Mine only")),
            Insert("user_settings", setting, new JsonObject { ["key"] = "theme", ["value"] = "dark" })]);
        await clientB.SyncAllAsync();
        Assert.IsNull(clientB.Row("items", privateNote));
        Assert.IsNull(clientB.Row("user_settings", setting));
        Assert.AreEqual("dark", clientA.Row("user_settings", setting)!["value"]!.GetValue<string>());

        var fresh = new SyncClient(app.ClientFor(b, ""));
        await fresh.SyncAllAsync();
        var group = fresh.Rows("groups").First(g => g.Str("id") == GroupEntityIds.Members);
        Assert.AreEqual("Members", group.Str("name"));
        Assert.IsFalse(group.ContainsKey("normalizedName"));
        Assert.IsFalse(group.ContainsKey("entraAppRole"));
        Assert.IsTrue(fresh.Rows("persons").Any(p => p.Str("userId") == a.ToString()), "every user receives every person");
        Assert.IsTrue(fresh.Rows("persons").All(p => p.ContainsKey("seq") && p.ContainsKey("rev")));
    }

    [TestMethod]
    public async Task PagesMoveTheCursorAsPlmDoes()
    {
        var (_, a, _, clientA, _) = await TwoMembersAsync();
        var ids = Enumerable.Range(0, 7).Select(_ => Ids.New()).ToList();
        await clientA.SyncAsync(ids.Select(id => Insert("items", id, Note("Page"))), pull: false);
        var paged = new SyncClient(app.ClientFor(a, Notes));
        var pages = 0;
        JsonObject answer;
        do
        {
            answer = await paged.SyncAsync(limit: 2);
            pages++;
        }
        while ((answer.Bool("more") || answer.Bool("reset")) && pages < 1000);
        Assert.IsGreaterThan(3, pages);
        Assert.AreEqual(answer.Long("head"), paged.Cursor);
        CollectionAssert.IsSubsetOf(ids, paged.Rows("items").Select(r => Guid.Parse(r.Str("id")!)).ToList());
    }

    private static class GroupEntityIds
    {
        public static readonly string Members = PGPM.Auth.GroupEntity.MembersId.ToString();
    }
}

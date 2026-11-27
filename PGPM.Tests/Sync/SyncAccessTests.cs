using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Lib.Helper;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>
/// Access changes (docs/02 §11.5, docs/03 §6.3, §11.13): a share added re-stamps the subtree (new seqs, same revs, the new
/// <c>access</c>), a share removed evicts, an owner transfer evicts the old owner, a membership change, a group deletion, a
/// deleted device row and a purge reset; the Trash and the History.
/// </summary>
[TestClass]
public sealed class SyncAccessTests
{
    private const string Notes = "Notes:Create";

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static JsonArray Shares(params (Guid Group, string Access)[] shares) =>
        new([.. shares.Select(s => (JsonNode)new JsonObject { ["groupId"] = s.Group.ToString(), ["access"] = s.Access })]);

    [TestMethod]
    public async Task SharingDeliversRestampedRowsAndUnsharingEvicts()
    {
        var team = await GroupAsync(app, "team");
        var owner = await UserAsync(app, "owner");
        var reader = await UserAsync(app, "reader", team);
        var ownerClient = new SyncClient(app.ClientFor(owner, Notes));
        var readerClient = new SyncClient(app.ClientFor(reader));
        var id = Ids.New();
        await ownerClient.SyncAllAsync([Insert("items", id, Note("Secret plan"))]);
        await readerClient.SyncAllAsync();
        Assert.IsNull(readerClient.Row("items", id));
        var before = ownerClient.Row("items", id)!;

        using (var shared = await ownerClient.Http.PutJsonAsync($"/api/v1/items/{id}/shares", Shares((team, "read"))))
        {
            var list = JsonNode.Parse(await shared.Content.ReadAsStringAsync())!.AsArray();
            Assert.AreEqual(HttpStatusCode.OK, shared.StatusCode, list.ToJsonString());
            Assert.AreEqual("read", list[0].Str("access"));
        }
        await readerClient.SyncAllAsync();
        var delivered = readerClient.Row("items", id)!;
        Assert.AreEqual("read", delivered.Str("access"));
        Assert.AreEqual(before.Int("rev"), delivered.Int("rev"), "a re-stamp keeps the rev");
        Assert.IsGreaterThan(before.Long("seq"), delivered.Long("seq"));
        Assert.AreEqual(1, readerClient.Rows("item_shares").Count(s => s.Str("itemId") == id.ToString()));

        // Raised to edit: the row comes again with the new access, no eviction.
        using (var raised = await ownerClient.Http.PutJsonAsync($"/api/v1/items/{id}/shares", Shares((team, "edit"))))
        {
            Assert.AreEqual(HttpStatusCode.OK, raised.StatusCode);
        }
        var answer = await readerClient.SyncAllAsync();
        Assert.AreEqual("edit", readerClient.Row("items", id).Str("access"));
        Assert.AreEqual(0, answer.Arr("evictions").Count);

        // The reader can't share: that takes manage.
        using (var refused = await readerClient.Http.PutJsonAsync($"/api/v1/items/{id}/shares", Shares()))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.AreEqual("missing_access", await refused.CodeAsync());
        }

        // Unshared: an eviction; the owner sees the share's tombstone.
        using (var unshared = await ownerClient.Http.PutJsonAsync($"/api/v1/items/{id}/shares", Shares()))
        {
            Assert.AreEqual(HttpStatusCode.OK, unshared.StatusCode);
        }
        answer = await readerClient.SyncAllAsync();
        Assert.AreEqual(id.ToString(), answer.Arr("evictions").Single().Str("id"));
        Assert.AreEqual("item", answer.Arr("evictions").Single().Str("kind"));
        Assert.IsNull(readerClient.Row("items", id));
        await ownerClient.SyncAllAsync();
        Assert.IsFalse(ownerClient.Rows("item_shares").Any(s => s.Str("itemId") == id.ToString()));

        await using var db = app.NewDb();
        var actions = await db.ChangeLog.Where(c => c.ItemId == id && c.Action == "share").CountAsync();
        Assert.AreEqual(3, actions);
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == "item.shares_changed" && l.SubjectId == id));
    }

    [TestMethod]
    public async Task AnOwnerTransferEvictsTheOldOwner()
    {
        var owner = await UserAsync(app, "owner");
        var heir = await UserAsync(app, "heir");
        var ownerClient = new SyncClient(app.ClientFor(owner, Notes));
        var heirClient = new SyncClient(app.ClientFor(heir));
        var id = Ids.New();
        await ownerClient.SyncAllAsync([Insert("items", id, Note("Handover"))]);
        await heirClient.SyncAllAsync();

        using (var moved = await ownerClient.Http.PostJsonNodeAsync($"/api/v1/items/{id}/owner", new JsonObject { ["userId"] = heir.ToString() }))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, moved.StatusCode, await moved.Content.ReadAsStringAsync());
        }
        await heirClient.SyncAllAsync();
        Assert.AreEqual("manage", heirClient.Row("items", id).Str("access"));
        Assert.AreEqual(heir.ToString(), heirClient.Row("items", id).Str("ownerUserId"));
        var answer = await ownerClient.SyncAllAsync();
        Assert.AreEqual(id.ToString(), answer.Arr("evictions").Single().Str("id"));
        Assert.IsNull(ownerClient.Row("items", id));
    }

    [TestMethod]
    public async Task AMembershipChangeResetsTheUsersDevices()
    {
        var team = await GroupAsync(app, "team");
        var member = await UserAsync(app, "member");
        var client = new SyncClient(app.ClientFor(member));
        await client.SyncAllAsync();
        var epoch = client.AccessEpoch;

        using (var added = await app.Client(TestUsers.Admin).PutJsonAsync($"/api/v1/groups/{team}/members",
            new JsonObject { ["userIds"] = new JsonArray(member.ToString()) }))
        {
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
        }
        var answer = await client.SyncAsync(apply: false);
        Assert.IsTrue(answer.Bool("reset"));
        Assert.AreEqual(epoch + 1, answer.Int("accessEpoch"));
        Assert.AreEqual(0, answer.Long("cursor"));
        client.Apply(answer);
        await client.SyncAllAsync();
        Assert.IsFalse((await client.SyncAsync()).Bool("reset"), "after the reset the epoch is the server's");
    }

    [TestMethod]
    public async Task ADeletedGroupLeavesATombstoneAndResetsItsMembers()
    {
        var team = await GroupAsync(app, "team");
        var member = await UserAsync(app, "member", team);
        var owner = await UserAsync(app, "owner");
        var memberClient = new SyncClient(app.ClientFor(member));
        var ownerClient = new SyncClient(app.ClientFor(owner, Notes));
        var id = Ids.New();
        var batch = Ids.New();
        await ownerClient.SyncAllAsync([Insert("items", id, Note("Shared"), batch), Insert("item_shares", Ids.New(), Share(id, team, "read"), batch)]);
        await memberClient.SyncAllAsync();
        Assert.IsNotNull(memberClient.Row("items", id));

        using (var deleted = await app.Client(TestUsers.Admin).DeleteAsync($"/api/v1/groups/{team}"))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
        }
        Assert.IsTrue((await memberClient.SyncAsync(apply: false)).Bool("reset"));
        await ownerClient.SyncAllAsync();
        Assert.IsNull(ownerClient.Row("groups", team), "the tombstone removed it");
        Assert.IsFalse(ownerClient.Rows("item_shares").Any(s => s.Str("itemId") == id.ToString()));
        using var list = await app.Client(TestUsers.Admin).GetAsync($"/api/v1/groups/{team}");
        Assert.AreEqual(HttpStatusCode.NotFound, list.StatusCode);
    }

    [TestMethod]
    public async Task ADeviceWhoseRowWasDeletedResets()
    {
        var user = await UserAsync(app, "device");
        var client = new SyncClient(app.ClientFor(user, Notes));
        await client.SyncAllAsync([Insert("items", Ids.New(), Note("x"))]);
        await using (var db = app.NewDb())
        {
            await db.SyncClients.Where(c => c.Id == client.ClientId).ExecuteDeleteAsync();
        }
        Assert.IsTrue((await client.SyncAsync(apply: false)).Bool("reset"));
    }

    /// <summary>Alone: its purge raises the tombstone horizon, which resets the devices of tests running at the same time.</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task TheTrashRestoresAndPurges()
    {
        var team = await GroupAsync(app, "team");
        var owner = await UserAsync(app, "owner", team);
        var other = await UserAsync(app, "other", team);
        var ownerClient = new SyncClient(app.ClientFor(owner, Notes));
        var otherClient = new SyncClient(app.ClientFor(other));
        var id = Ids.New();
        var batch = Ids.New();
        await ownerClient.SyncAllAsync([Insert("items", id, Note("Old idea"), batch), Insert("item_shares", Ids.New(), Share(id, team, "read"), batch)]);
        await otherClient.SyncAllAsync();
        await ownerClient.SyncAllAsync([Delete("items", id, 1)]);

        var trash = await (await ownerClient.Http.GetAsync("/api/v1/trash?pageSize=500")).JsonAsync();
        var entry = trash.Arr("items").Single(e => e.Str("rootId") == id.ToString())!;
        Assert.AreEqual("Old idea", entry.Str("title"));
        Assert.AreEqual("item", entry.Str("rootKind"));
        Assert.AreEqual("note", entry.Str("rootType"));
        Assert.AreEqual(await DisplayNameAsync(app, owner), entry.Str("deletedByName"));
        var otherTrash = await (await otherClient.Http.GetAsync("/api/v1/trash?pageSize=500")).JsonAsync();
        Assert.IsFalse(otherTrash.Arr("items").Any(e => e.Str("rootId") == id.ToString()), "read access doesn't manage");
        var batchId = entry.Str("batchId");
        using (var refused = await otherClient.Http.PostEmptyAsync($"/api/v1/trash/{batchId}/restore"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var restored = await (await ownerClient.Http.PostEmptyAsync($"/api/v1/trash/{batchId}/restore")).JsonAsync();
        Assert.AreEqual(1, restored.Int("restored"));
        await otherClient.SyncAllAsync();
        Assert.AreEqual(3, otherClient.Row("items", id).Int("rev"), "created 1, deleted 2, restored 3");
        Assert.AreEqual("read", otherClient.Row("items", id).Str("access"));

        // Deleted again and purged: a device behind the purged rows resets.
        await ownerClient.SyncAllAsync([Delete("items", id, 3)]);
        var behind = new SyncClient(app.ClientFor(other)) { Cursor = otherClient.Cursor, AccessEpoch = otherClient.AccessEpoch };
        // Its first request registers the device (a reset: it has a cursor but no row yet); the cursor stays behind.
        await behind.SyncAsync(pull: false, apply: false);
        trash = await (await ownerClient.Http.GetAsync("/api/v1/trash?pageSize=500")).JsonAsync();
        batchId = trash.Arr("items").Single(e => e.Str("rootId") == id.ToString()).Str("batchId");
        using (var notAdmin = await ownerClient.Http.DeleteAsync($"/api/v1/trash/{batchId}"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        }
        using (var purged = await app.Client(TestUsers.Admin).DeleteAsync($"/api/v1/trash/{batchId}"))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, purged.StatusCode, await purged.Content.ReadAsStringAsync());
        }
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.Items.AnyAsync(i => i.Id == id));
            Assert.IsFalse(await db.ItemShares.AnyAsync(s => s.ItemId == id));
        }
        Assert.IsTrue((await behind.SyncAsync(apply: false)).Bool("reset"), "its cursor is below the tombstone horizon");
        using var gone = await ownerClient.Http.PostEmptyAsync($"/api/v1/trash/{batchId}/restore");
        Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// <summary>Alone, as the purge: old tombstones of <c>other</c> batches and old log rows go, the horizon follows.</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task ThePrunersDropOldTombstonesAndLogs()
    {
        var user = await UserAsync(app, "pruned");
        var client = new SyncClient(app.ClientFor(user));
        var setting = Ids.New();
        await client.SyncAllAsync([Insert("user_settings", setting, new JsonObject { ["key"] = "old", ["value"] = 1 })]);
        await client.SyncAllAsync([Delete("user_settings", setting, 1)]);
        var old = Clock.UtcNow.AddDays(-100);
        long tombstone;
        await using (var db = app.NewDb())
        {
            var row = await db.UserSettings.SingleAsync(s => s.Id == setting);
            tombstone = row.Seq;
            await db.DeleteBatches.Where(b => b.Id == row.DeleteBatchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.DeletedAt, old));
            await db.SyncMutations.Where(m => m.UserId == user).ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, old));
            db.SyncEvictions.Add(new PGPM.Sync.SyncEvictionEntity { UserId = user, Seq = tombstone, RootKind = PGPM.Sync.DeleteRootKind.Item, RootId = Ids.New(), At = old });
            await db.SaveChangesAsync();
        }
        await using (var db = app.NewDb())
        {
            Assert.IsGreaterThanOrEqualTo(1, await PGPM.Sync.TombstonePruner.RunOnceAsync(db, new PGPM.Sync.SyncOptions(), CancellationToken.None));
            var (mutations, evictions) = await PGPM.Sync.LogPruner.RunOnceAsync(db, CancellationToken.None);
            Assert.IsGreaterThanOrEqualTo(2, mutations);
            Assert.IsGreaterThanOrEqualTo(1, evictions);
        }
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.UserSettings.AnyAsync(s => s.Id == setting));
            Assert.IsFalse(await db.SyncMutations.AnyAsync(m => m.UserId == user));
            Assert.IsGreaterThanOrEqualTo(tombstone, await db.SyncState.Select(s => s.TombstoneHorizonSeq).SingleAsync());
        }
    }

    [TestMethod]
    public async Task TheHistoryNamesWhoChangedWhat()
    {
        var team = await GroupAsync(app, "team");
        var owner = await UserAsync(app, "owner", team);
        var stranger = await UserAsync(app, "stranger");
        var client = new SyncClient(app.ClientFor(owner, Notes));
        var id = Ids.New();
        await client.SyncAllAsync([Insert("items", id, Note("Draft"))]);
        await client.SyncAllAsync([Patch("items", id, 1, ("title", "Draft", "Final"))]);

        var history = await (await client.Http.GetAsync($"/api/v1/history?itemId={id}")).JsonAsync();
        var entries = history.Arr("items");
        Assert.AreEqual(2, history.Int("totalCount"));
        var update = entries[0]!;
        Assert.AreEqual("update", update.Str("action"));
        Assert.AreEqual("ui", update.Str("source"));
        Assert.AreEqual(await DisplayNameAsync(app, owner), update.Str("userName"));
        Assert.AreEqual("title", update["changes"]![0].Str("field"));
        Assert.AreEqual("Draft", update["changes"]![0].Str("old"));
        Assert.AreEqual("Final", update["changes"]![0].Str("new"));
        Assert.AreEqual("create", entries[1].Str("action"));
        Assert.IsNotNull(entries[1].Str("clientChangedAt"));

        using var refused = await app.ClientFor(stranger).GetAsync($"/api/v1/history?itemId={id}");
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual("missing_access", await refused.CodeAsync());
        using var missing = await client.Http.GetAsync($"/api/v1/history?itemId={Ids.New()}");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }
}

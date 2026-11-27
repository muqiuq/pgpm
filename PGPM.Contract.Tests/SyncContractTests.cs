using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static PGPM.Contract.Tests.ContractKit;

namespace PGPM.Contract.Tests;

/// <summary>
/// The sync round trip of a running PGPM (docs/03 §11.4, §11.13), as the browser client (PGPM.WebUI/src/local) talks
/// to it: the admin creates a group with Notes: Create and two users in it; Ana creates a note with its share in one
/// batch; Ben pulls it (access <c>edit</c>), follows the live channel and gets Ana's edit there, acknowledges it, and his
/// stale edit of the same field is a <c>conflict</c> of kind <c>field</c> with the server's row. Every name, status and
/// format the client relies on is asserted here against the real image.
/// </summary>
[TestClass]
public sealed partial class SyncContractTests
{
    /// <summary>The wire's timestamps: UTC, microseconds (docs/03 §11.4).</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$")]
    private static partial Regex WireTimestamp();

    [TestMethod]
    public async Task TwoUsersSyncANoteOverHttpAndTheLiveChannel()
    {
        using var admin = await FirstAdmin.SignInAsync();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var group = await admin.CallAsync(HttpMethod.Post, "/api/v1/groups",
            new { name = $"Contract team {stamp}", rights = new Dictionary<string, string> { ["Notes"] = "Create" } }, HttpStatusCode.Created);
        var groupId = group.GetProperty("id").GetString()!;
        using var ana = await UserAsync(admin, $"ana-{stamp}", $"Ana {stamp}", groupId);
        using var ben = await UserAsync(admin, $"ben-{stamp}", $"Ben {stamp}", groupId);
        var anaDevice = new Device(ana);
        var benDevice = new Device(ben);

        // Ana's first sync: everything from 0, the epoch to keep, the groups (without their server-only columns).
        var first = await anaDevice.SyncAsync();
        Assert.IsFalse(first.GetProperty("reset").GetBoolean());
        Assert.IsFalse(first.GetProperty("more").GetBoolean());
        Assert.AreEqual(first.GetProperty("head").GetInt64(), first.GetProperty("cursor").GetInt64());
        var groupRow = Rows(first, "groups").Single(r => r.GetProperty("id").GetString() == groupId);
        Assert.AreEqual($"Contract team {stamp}", groupRow.GetProperty("name").GetString());
        Assert.IsFalse(groupRow.TryGetProperty("normalizedName", out _));
        Assert.IsFalse(groupRow.TryGetProperty("entraAppRole", out _));

        // A note and its share, one batch (the create dialog), as the outbox has them.
        var batch = Guid.CreateVersion7();
        var noteId = Guid.CreateVersion7().ToString();
        var shareId = Guid.CreateVersion7().ToString();
        var created = await anaDevice.SyncAsync([
            Insert(batch, "items", noteId, new { id = noteId, type = "note", title = "Contract note", description = "Agenda", pinned = false, tags = Array.Empty<string>() }),
            Insert(batch, "item_shares", shareId, new { id = shareId, itemId = noteId, groupId, access = "edit" })]);
        var results = created.GetProperty("results").EnumerateArray().ToList();
        Assert.HasCount(2, results);
        foreach (var result in results)
        {
            Assert.AreEqual("accepted", result.GetProperty("status").GetString(), result.ToString());
            Assert.AreEqual(1, result.GetProperty("rev").GetInt32());
            Assert.IsTrue(result.GetProperty("seq").GetInt64() > 0);
            Assert.IsFalse(result.TryGetProperty("current", out _));
        }
        var note = Rows(created, "items").Single(r => r.GetProperty("id").GetString() == noteId);
        Assert.AreEqual("manage", note.GetProperty("access").GetString());
        Assert.AreEqual("note", note.GetProperty("type").GetString());
        Assert.IsTrue(note.GetProperty("number").GetInt32() > 0);
        Assert.AreEqual(ana.UserId, note.GetProperty("ownerUserId").GetString());
        Assert.IsFalse(note.GetProperty("deleted").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, note.GetProperty("deleteBatchId").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, note.GetProperty("tags").ValueKind);
        StringAssert.Matches(note.GetProperty("createdAt").GetString(), WireTimestamp());
        StringAssert.Matches(note.GetProperty("updatedAt").GetString(), WireTimestamp());
        Assert.AreEqual(ana.UserId, note.GetProperty("createdBy").GetString());
        var share = Rows(created, "item_shares").Single(r => r.GetProperty("id").GetString() == shareId);
        Assert.AreEqual("edit", share.GetProperty("access").GetString());

        // Ben pulls it from 0: shared with his group at edit.
        var benFirst = await benDevice.SyncAsync();
        var benNote = Rows(benFirst, "items").Single(r => r.GetProperty("id").GetString() == noteId);
        Assert.AreEqual("edit", benNote.GetProperty("access").GetString());
        Assert.AreEqual(1, benNote.GetProperty("rev").GetInt32());

        // The live channel: refused for another origin (HTTP 403 before the upgrade), welcomed for this one.
        using (var foreign = ben.Socket("https://evil.example"))
        {
            await Assert.ThrowsExactlyAsync<WebSocketException>(() => foreign.ConnectAsync(ben.LiveUrl, CancellationToken.None));
            Assert.AreEqual(HttpStatusCode.Forbidden, foreign.HttpStatusCode);
        }
        using var live = ben.Socket(ben.Origin);
        await live.ConnectAsync(ben.LiveUrl, CancellationToken.None);
        await SendAsync(live, new { type = "hello", clientId = benDevice.ClientId, since = benDevice.Cursor, accessEpoch = benDevice.Epoch, appVersion = "contract" });
        var welcome = await ReceiveAsync(live, "welcome");
        Assert.IsTrue(welcome.GetProperty("head").GetInt64() >= benDevice.Cursor);
        await SendAsync(live, new { type = "ping" });
        await ReceiveAsync(live, "pong");

        // Ana edits the title (pull: false, as while live): Ben gets the row on the live channel and acknowledges it.
        var edited = await anaDevice.SyncAsync([Patch(noteId, 1, "title", "Contract note", "Contract note 2")], pull: false);
        var accepted = edited.GetProperty("results")[0];
        Assert.AreEqual("accepted", accepted.GetProperty("status").GetString());
        Assert.AreEqual(2, accepted.GetProperty("rev").GetInt32());
        Assert.AreEqual(0, edited.GetProperty("changes").EnumerateObject().Count(), "a push without pull brings no rows");
        var changes = await ReceiveAsync(live, "changes");
        var liveNote = changes.GetProperty("changes").GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetString() == noteId);
        Assert.AreEqual("Contract note 2", liveNote.GetProperty("title").GetString());
        Assert.AreEqual("edit", liveNote.GetProperty("access").GetString());
        Assert.AreEqual(JsonValueKind.Array, changes.GetProperty("evictions").ValueKind);
        Assert.IsFalse(changes.GetProperty("more").GetBoolean());
        var cursor = changes.GetProperty("cursor").GetInt64();
        Assert.IsTrue(cursor > benDevice.Cursor);
        await SendAsync(live, new { type = "ack", cursor });
        benDevice.Cursor = cursor;

        // Ben's edit made on rev 1: every key conflicts – nothing applied, kind field, the server's row as current, and who
        // changed it in conflicts[].
        var stale = await benDevice.SyncAsync([Patch(noteId, 1, "title", "Contract note", "Ben's title")]);
        var conflict = stale.GetProperty("results")[0];
        Assert.AreEqual("conflict", conflict.GetProperty("status").GetString(), conflict.ToString());
        Assert.AreEqual("field", conflict.GetProperty("kind").GetString());
        Assert.AreEqual("title", conflict.GetProperty("conflictFields")[0].GetString());
        Assert.AreEqual("Contract note 2", conflict.GetProperty("current").GetProperty("title").GetString());
        var reported = stale.GetProperty("conflicts")[0];
        Assert.AreEqual("title", reported.GetProperty("field").GetString());
        Assert.AreEqual("Contract note 2", reported.GetProperty("theirs").GetString());
        Assert.AreEqual($"Ana {stamp}", reported.GetProperty("by").GetString());
        Assert.AreEqual(2, reported.GetProperty("rev").GetInt32());

        // Keep mine: the decision recorded, and the patch on theirs.
        var mine = await benDevice.SyncAsync([Patch(noteId, 2, "title", "Contract note 2", "Ben's title")],
            resolved: [new { table = "items", id = noteId, field = "title", resolution = "mine" }]);
        Assert.AreEqual("accepted", mine.GetProperty("results")[0].GetProperty("status").GetString());
        var history = await ana.CallAsync(HttpMethod.Get, $"/api/v1/history?itemId={noteId}");
        var actions = history.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        CollectionAssert.IsSubsetOf(new[] { "create", "update", "conflict", "conflict_resolved" }, actions);

        // A row Ben may not see (or that doesn't exist): missing_access without current – the client drops its row.
        var unknown = Guid.CreateVersion7().ToString();
        var missing = (await benDevice.SyncAsync([Patch(unknown, 1, "title", "x", "y")], pull: false)).GetProperty("results")[0];
        Assert.AreEqual("rejected", missing.GetProperty("status").GetString());
        Assert.AreEqual("missing_access", missing.GetProperty("code").GetString());
        Assert.IsFalse(missing.TryGetProperty("current", out _));

        // Unshared: Ben's device gets the eviction live.
        await ana.CallAsync(HttpMethod.Put, $"/api/v1/items/{noteId}/shares", Array.Empty<object>());
        JsonElement evicted;
        do
        {
            evicted = await ReceiveAsync(live, "changes");
            await SendAsync(live, new { type = "ack", cursor = evicted.GetProperty("cursor").GetInt64() });
        }
        while (!evicted.GetProperty("evictions").EnumerateArray().Any(e => e.GetProperty("id").GetString() == noteId));
        Assert.AreEqual("item", evicted.GetProperty("evictions").EnumerateArray().First(e => e.GetProperty("id").GetString() == noteId)
            .GetProperty("kind").GetString());

        await live.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
}

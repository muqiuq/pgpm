using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Mcp;
using PGPM.Mcp.Tools;
using PGPM.Sync;
using PGPM.Tests.Sync;
using static PGPM.Tests.Mcp.McpKit;

namespace PGPM.Tests.Mcp;

/// <summary>
/// The MCP server end to end (docs/03 §7.2–7.3): the SDK's client over Streamable HTTP against the test server. What each
/// token is offered, the item tools over notes – reading with the token's access, writing through the SyncWriter with the
/// source mcp (audit, History, live delivery) –, capped rights and access, strikes, rate limits, the origin check, and that
/// a token never reaches the API nor a session cookie /mcp.
/// </summary>
[TestClass]
public sealed class McpServerTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp(new Dictionary<string, string?>
    {
        ["Mcp:CallsPerMinute"] = "30",
        ["Mcp:WritesPerMinute"] = "6",
    });

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public void TheCatalogKeepsItsOwnRules()
    {
        var all = ToolCatalog.All;
        Assert.AreEqual(all.Count, all.Select(t => t.Name).Distinct().Count(), "unique names");
        Assert.IsTrue(all.All(t => System.Text.RegularExpressions.Regex.IsMatch(t.Name, "^[a-z][a-z0-9_]*$")), "snake case");
        Assert.IsTrue(all.Where(t => t.Writes).All(t => t.AuditEvents.Count > 0 && t.AuditEvents.All(AuditCatalog.Contains)), "a change names its audit events");
        Assert.IsTrue(all.All(t => t.InputSchema["type"]?.GetValue<string>() == "object"));
        // docs/03 §7.3: the catalogue v1.
        CollectionAssert.AreEquivalent(new[]
        {
            "search_items", "get_item", "create_note", "create_idea", "create_lead", "create_project", "update_item", "assign_person", "unassign_person",
            "link_items", "unlink_items", "add_milestone", "update_milestone", "set_dod_item", "record_gate_decision", "list_maps", "get_map",
            "add_item_to_map", "convert_to_project", "search_persons", "list_config",
        }, all.Select(t => t.Name).ToList());
        Assert.AreEqual(Area.Notes, ToolCatalog.Find("create_note")!.Area, "creating needs the type's right");
        Assert.AreEqual(Area.Projects, ToolCatalog.Find("create_project")!.Area);
        Assert.IsFalse(ToolCatalog.KindAllows(ToolCatalog.Find("update_item")!, McpTokenKind.Read));
        var convert = ToolCatalog.Find("convert_to_project")!;
        Assert.AreEqual((ToolKind.Action, Area.Projects, Level.Create), (convert.Kind, convert.Requires.Single().Area, convert.Requires.Single().Level));
        CollectionAssert.AreEquivalent(new[] { "search_items", "get_item", "list_maps", "get_map", "search_persons", "list_config" },
            all.Where(t => ToolCatalog.KindAllows(t, McpTokenKind.Read)).Select(t => t.Name).ToList(), "the read token reads, nothing else");
        Assert.IsFalse(all.Any(t => t.Kind == ToolKind.Delete || t.Destructive), "no delete tools in v1");
    }

    [TestMethod]
    public async Task TheReadTokenListsOnlyWhatReadsAndSeesWhatItsUserSees()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-read");
        var user = await MemberAsync(app, "reader", group);
        var other = await MemberAsync(app, "colleague", group);
        var mine = await NoteAsync(app, user, $"Mine {Guid.NewGuid():N}");
        var shared = await NoteAsync(app, other, $"Shared {Guid.NewGuid():N}", group);
        var hidden = await NoteAsync(app, other, $"Hidden {Guid.NewGuid():N}");
        var token = (await TurnOnAsync(app, user, "read")).Value!;
        await using var client = await ClientAsync(app, token);

        var tools = (await client.ListToolsAsync()).Select(t => t.ProtocolTool).ToList();
        CollectionAssert.AreEquivalent(new[] { "search_items", "get_item", "search_persons", "list_config", "list_maps", "get_map" },
            tools.Select(t => t.Name).ToList());
        Assert.IsTrue(tools.All(t => t.Annotations!.ReadOnlyHint == true));
        StringAssert.Contains(client.ServerInstructions, "PGPM");

        var found = await OkAsync(client, "search_items", new { type = "note" });
        var ids = found.GetProperty("items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("id").GetString()!)).ToList();
        CollectionAssert.IsSubsetOf(new[] { mine, shared }, ids);
        CollectionAssert.DoesNotContain(ids, hidden, "sharing decides");
        var own = found.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == mine.ToString());
        Assert.AreEqual("read", own.GetProperty("access").GetString(), "the owner's manage, capped at read");
        StringAssert.Matches(own.GetProperty("key").GetString(), new System.Text.RegularExpressions.Regex("^N-[0-9]+$"));
        StringAssert.EndsWith(own.GetProperty("url").GetString(), $"/notes/{mine}");

        var byText = await OkAsync(client, "search_items", new { text = "shared", sharedWith = group.ToString() });
        Assert.AreEqual(shared.ToString(), byText.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.AreEqual(1, byText.GetProperty("total").GetInt32());
        var byOwner = await OkAsync(client, "search_items", new { owner = "me", limit = 1 });
        Assert.IsTrue(byOwner.GetProperty("items").EnumerateArray().All(i => i.GetProperty("owner").GetProperty("userId").GetString() == user.ToString()));

        var detail = await OkAsync(client, "get_item", new { id = own.GetProperty("key").GetString() });
        Assert.AreEqual(mine.ToString(), detail.GetProperty("id").GetString());
        Assert.AreEqual(0, detail.GetProperty("shares").GetArrayLength());
        Assert.AreEqual("not_found", (await ErrorAsync(client, "get_item", new { id = hidden.ToString() })).GetProperty("error").GetString());

        var config = await OkAsync(client, "list_config");
        Assert.IsTrue(config.GetProperty("groups").EnumerateArray().Any(g => g.GetProperty("id").GetString() == group.ToString()));
        Assert.IsFalse(config.GetProperty("itemTypes")[0].GetProperty("canCreate").GetBoolean(), "a read token creates nothing");
        var persons = await OkAsync(client, "search_persons", new { text = "reader" });
        Assert.IsTrue(persons.GetProperty("items").EnumerateArray().Any(p => p.GetProperty("userId").GetString() == user.ToString()));

        await using var db = app.NewDb();
        var entries = await db.McpLog.Where(l => l.UserId == user).OrderBy(l => l.Id).ToListAsync();
        CollectionAssert.IsSubsetOf(new[] { "tools/list", "tools/call" }, entries.Select(e => e.Method).Distinct().ToList());
        var call = entries.First(e => e.Tool == "search_items" && e.Arguments!.Contains("shared", StringComparison.Ordinal));
        Assert.AreEqual(McpOutcomes.Ok, call.Outcome);
        StringAssert.Contains(McpLog.Unpack(call.Result), shared.ToString());
    }

    [TestMethod]
    public async Task TheWriteTokenCreatesANoteThroughTheSyncWriter()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-create");
        var (user, _, write) = await WriterAsync(app, "creator");
        await using var client = await ClientAsync(app, write);
        var title = $"From the assistant {Guid.NewGuid():N}";
        var note = await OkAsync(client, "create_note", new
        {
            title, description = "# Notes\nWritten over MCP.", pinned = true, color = "#AABBCC",
            share_with = new[] { new { group = group.ToString(), access = "read" } },
        });
        var id = Guid.Parse(note.GetProperty("id").GetString()!);
        Assert.AreEqual(("note", true, "#aabbcc", "edit"), (note.GetProperty("type").GetString(), note.GetProperty("pinned").GetBoolean(),
            note.GetProperty("color").GetString(), note.GetProperty("access").GetString()), "the owner's access capped at edit");
        Assert.AreEqual("read", note.GetProperty("shares")[0].GetProperty("access").GetString());

        await using var db = app.NewDb();
        var item = await db.Items.SingleAsync(i => i.Id == id);
        Assert.AreEqual((user, user, 1), (item.OwnerUserId, item.CreatedBy!.Value, item.Rev));
        Assert.IsGreaterThan(0L, item.Seq);
        Assert.IsTrue(await db.ItemShares.AnyAsync(s => s.ItemId == id && s.GroupId == group && s.Access == ItemAccess.Read));
        var history = await db.ChangeLog.Where(c => c.ItemId == id).ToListAsync();
        Assert.IsTrue(history.Any(c => c.Action == "create" && c.Source == ChangeSource.Mcp && c.UserId == user));
        Assert.IsTrue(await db.SyncMutations.AnyAsync(m => m.RowId == id && m.Op == "insert" && m.Status == "accepted"));

        var tokenId = await db.McpTokens.Where(t => t.UserId == user && t.Kind == McpTokenKind.Write).Select(t => t.Id).SingleAsync();
        var audit = await db.AuditLog.SingleAsync(l => l.Event == AuditEvents.McpItemCreated && l.SubjectId == id);
        Assert.AreEqual((AuditActor.Mcp, $"token:{tokenId}:rw", user), (audit.Actor, audit.ActorRef, audit.ActorUserId!.Value));
        StringAssert.Contains(AuditLog.ReadDetail(audit.Detail).Summary, title);
        var log = await db.McpLog.SingleAsync(l => l.Tool == "create_note" && l.TokenId == tokenId);
        Assert.AreEqual((McpOutcomes.Ok, audit.CorrelationId), (log.Outcome, log.CorrelationId));
        StringAssert.Contains(log.Summary, "created N-");

        // Refused by the table's own rules, as a client's mutation would be: nothing is created.
        var invalid = await ErrorAsync(client, "create_note", new { title = "x", color = "red" });
        Assert.AreEqual("invalid_argument", invalid.GetProperty("error").GetString());
        var unknownGroup = await ErrorAsync(client, "create_note", new { title = "x", share_with = new[] { new { group = "no such group" } } });
        Assert.AreEqual("not_found", unknownGroup.GetProperty("error").GetString());
        Assert.AreEqual(1, await db.Items.CountAsync(i => i.OwnerUserId == user));
    }

    [TestMethod]
    public async Task UpdateItemPatchesWhatItIsGivenAndExpectedRevCatchesAChangeMeanwhile()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-update");
        var (user, _, write) = await WriterAsync(app, "updater", group);
        var other = await MemberAsync(app, "author", group);
        var theirs = await NoteAsync(app, other, $"Theirs {Guid.NewGuid():N}", group);
        await using var client = await ClientAsync(app, write);

        var before = await OkAsync(client, "get_item", new { id = theirs.ToString() });
        Assert.AreEqual("edit", before.GetProperty("access").GetString());
        var rev = before.GetProperty("rev").GetInt32();
        var updated = await OkAsync(client, "update_item", new { id = theirs.ToString(), title = "Renamed", pinned = true, expectedRev = rev });
        Assert.AreEqual(("Renamed", true, rev + 1), (updated.GetProperty("title").GetString(), updated.GetProperty("pinned").GetBoolean(),
            updated.GetProperty("rev").GetInt32()));
        CollectionAssert.AreEquivalent(new[] { "title", "pinned" }, updated.GetProperty("changed").EnumerateArray().Select(c => c.GetString()).ToList());

        // Someone changes it in the browser; the assistant's expectedRev is stale.
        var browser = new SyncClient(app.ClientFor(other, "Notes:Create"));
        await browser.SyncAsync([SyncKit.Patch("items", theirs, rev + 1, ("description", null, "Edited in the browser"))], pull: false);
        var stale = await ErrorAsync(client, "update_item", new { id = theirs.ToString(), description = "Mine", expectedRev = rev + 1 });
        Assert.AreEqual("changed_meanwhile", stale.GetProperty("error").GetString());
        Assert.AreEqual("Edited in the browser", stale.GetProperty("current").GetProperty("description").GetString());
        var removed = await OkAsync(client, "update_item", new { id = theirs.ToString(), description = "", color = "#123456" });
        Assert.IsFalse(removed.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String, "an empty text removes it");

        await using var db = app.NewDb();
        var changes = await db.ChangeLog.Where(c => c.ItemId == theirs && c.Action == "update").OrderBy(c => c.Id).ToListAsync();
        Assert.AreEqual((ChangeSource.Mcp, user), (changes[0].Source, changes[0].UserId!.Value));
        Assert.IsTrue(changes.Any(c => c.Source == ChangeSource.Ui && c.UserId == other));
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.McpItemUpdated && l.SubjectId == theirs && l.Actor == AuditActor.Mcp));

        // Read access is not enough, and nothing beyond edit is ever offered.
        var readOnly = await SyncKit.GroupAsync(app, "mcp-readonly");
        await using (var memberships = app.NewDb())
        {
            memberships.UserGroups.Add(new PGPM.Auth.UserGroupEntity { UserId = user, GroupId = readOnly });
            await memberships.SaveChangesAsync();
        }
        app.Services.GetRequiredService<PGPM.Auth.SessionCache>().Forget(user);
        var batch = Ids.New();
        var readNote = Ids.New();
        await new SyncClient(app.ClientFor(other, "Notes:Create")).SyncAsync([SyncKit.Insert("items", readNote, SyncKit.Note("Read only"), batch),
            SyncKit.Insert("item_shares", Ids.New(), SyncKit.Share(readNote, readOnly, "read"), batch)], pull: false);
        var refused = await ErrorAsync(client, "update_item", new { id = readNote.ToString(), title = "No" });
        Assert.AreEqual("missing_access", refused.GetProperty("error").GetString());
        var foreign = await ErrorAsync(client, "update_item", new { id = theirs.ToString() });
        Assert.AreEqual("invalid_argument", foreign.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task AnMcpWriteReachesALiveClient()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-live");
        var (_, _, write) = await WriterAsync(app, "live-writer");
        var watcher = await MemberAsync(app, "watcher", group);
        var device = new SyncClient(app.ClientFor(watcher, "Notes:Create"));
        await device.SyncAllAsync();
        await using var live = await LiveSocket.ConnectAsync(app, watcher, "Notes:Create");
        Assert.AreEqual("welcome", (await live.HelloAsync(device)).Str("type"));

        await using var client = await ClientAsync(app, write);
        var note = await OkAsync(client, "create_note", new { title = "Live from MCP", share_with = new[] { new { group = group.ToString() } } });
        var id = note.GetProperty("id").GetString();
        var changes = await live.WaitForAsync(m => m.Str("type") == "changes" && m["changes"]!["items"] is JsonArray items
            && items.Any(r => r.Str("id") == id), TimeSpan.FromSeconds(10));
        Assert.IsNotNull(changes, "the watcher's browser got the assistant's note live");
        var row = changes["changes"]!["items"]!.AsArray().Single(r => r.Str("id") == id)!;
        Assert.AreEqual("edit", row.Str("access"), "shared with edit when share_with names no access");
    }

    [TestMethod]
    public async Task ATokenGetsNoMoreThanItsUsersRights()
    {
        var mcpOnly = await GroupWithRightsAsync(app, "mcp-only", (Area.Mcp, Level.Use));
        var user = await Sync.SyncKit.UserAsync(app, "no-create", mcpOnly);
        await SecondFactorAsync(app, user);
        var write = (await TurnOnAsync(app, user, "write")).Value!;
        await using var client = await ClientAsync(app, write);
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        CollectionAssert.Contains(names, "update_item", "editing needs access, not a right");
        CollectionAssert.DoesNotContain(names, "create_note", "no Notes: Create");
        var refused = await ErrorAsync(client, "create_note", new { title = "Not allowed" });
        Assert.AreEqual("not_available", refused.GetProperty("error").GetString());
        await using var db = app.NewDb();
        Assert.IsTrue(await db.McpLog.AnyAsync(l => l.UserId == user && l.Tool == "create_note" && l.Outcome == McpOutcomes.Denied && l.Strike));
    }

    [TestMethod]
    public async Task ThreeStrikesBlockTheTokenAndTellTheAdmins()
    {
        var user = await MemberAsync(app, "striker");
        var token = (await TurnOnAsync(app, user, "read")).Value!;
        await using (var client = await ClientAsync(app, token))
        {
            for (var i = 0; i < 2; i++)
            {
                Assert.AreEqual("not_available", (await ErrorAsync(client, "create_note", new { title = "x" })).GetProperty("error").GetString());
            }
            var ordinary = await ErrorAsync(client, "get_item", new { id = Guid.NewGuid().ToString() });
            Assert.AreEqual("not_found", ordinary.GetProperty("error").GetString(), "an ordinary error is no strike");
            await ErrorAsync(client, "drop_everything");
        }
        Assert.AreEqual("blocked", await RefusalAsync(app, token));
        var account = await AccountAsync(app, user);
        Assert.AreEqual(("blocked", McpTokenEnds.Strikes), (account.Read.State, account.Read.EndedWhy));
        await using (var db = app.NewDb())
        {
            var tokenId = await db.McpTokens.Where(t => t.UserId == user && t.Kind == McpTokenKind.Read).Select(t => t.Id).SingleAsync();
            var blocked = await db.AuditLog.SingleAsync(l => l.Event == AuditEvents.McpTokenBlocked && l.SubjectId == tokenId);
            StringAssert.Contains(AuditLog.ReadDetail(blocked.Detail).Summary, "Blocked after 3 refused calls");
            StringAssert.Contains(AuditLog.ReadDetail(blocked.Detail).Summary, "drop_everything");
            Assert.AreEqual(3, await db.McpLog.CountAsync(l => l.UserId == user && l.Strike));
        }
        var again = await TurnOnAsync(app, user, "read");
        Assert.AreEqual((true, 0), (again.Token.On, again.Token.Strikes), "switching it on clears the strikes");
    }

    [TestMethod]
    public async Task TooManyCallsWaitForTheirMinute()
    {
        var (user, read, write) = await WriterAsync(app, "busy");
        await using var client = await ClientAsync(app, write);
        var note = await OkAsync(client, "create_note", new { title = "Busy" });
        for (var i = 1; i < 6; i++)
        {
            await OkAsync(client, "update_item", new { id = note.GetProperty("id").GetString(), title = $"Busy {i}" });
        }
        var limited = await ErrorAsync(client, "update_item", new { id = note.GetProperty("id").GetString(), title = "One too many" });
        Assert.AreEqual("rate_limited", limited.GetProperty("error").GetString());
        Assert.IsGreaterThan(0, limited.GetProperty("retryAfterSeconds").GetInt32());
        await OkAsync(client, "get_item", new { id = note.GetProperty("id").GetString() });

        await using var reader = await ClientAsync(app, read);
        for (var i = 0; i < 30; i++)
        {
            await OkAsync(reader, "list_config");
        }
        Assert.AreEqual("rate_limited", (await ErrorAsync(reader, "list_config")).GetProperty("error").GetString(), "120 calls a minute (30 here)");
        await using var db = app.NewDb();
        Assert.AreEqual(2, await db.McpLog.CountAsync(l => l.UserId == user && l.Outcome == McpOutcomes.RateLimited));
        Assert.IsFalse(await db.McpLog.AnyAsync(l => l.UserId == user && l.Strike), "no strike");
    }

    [TestMethod]
    public async Task ATokenNeverReachesTheApiAndASessionNeverReachesMcp()
    {
        var user = await MemberAsync(app, "isolated");
        var token = (await TurnOnAsync(app, user, "read")).Value!;

        var api = app.CreateClient();
        api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/account/access")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/account/mcp")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await api.PostAsync("/api/v1/sync", new StringContent("{}"))).StatusCode);

        // The browser's session cookie, and the tests' sign-in (which the cookie scheme forwards to) both stop at /mcp.
        var browser = new Browser(app);
        Assert.AreEqual("done", (await browser.LoginAsync(TestUsers.Member, TestUsers.Password)).Next);
        using (var withCookie = await browser.SendAsync(HttpMethod.Post, McpSetup.Path, new { jsonrpc = "2.0", id = 1, method = "tools/list" }))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, withCookie.StatusCode, "the session cookie is no token");
        }
        using (var signedIn = await RawAsync(app, null, http: app.Client(TestUsers.Admin)))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, signedIn.StatusCode);
        }
    }

    [TestMethod]
    public async Task ARequestFromAnotherSiteIsRefused()
    {
        var user = await MemberAsync(app, "origin");
        var token = (await TurnOnAsync(app, user, "read")).Value!;
        using (var evil = await RawAsync(app, token, "https://evil.example"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, evil.StatusCode);
            StringAssert.Contains(await evil.Content.ReadAsStringAsync(), "origin");
        }
        using var own = await RawAsync(app, token, "http://localhost");
        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode, "the server's own origin");
    }

    [TestMethod]
    public async Task AnAdminsTokenSeesEverythingCappedAtItsKind()
    {
        var author = await MemberAsync(app, "private-author");
        var note = await NoteAsync(app, author, $"Private {Guid.NewGuid():N}");
        var admin = TestUsers.IdOf(TestUsers.Admin);
        var token = (await TurnOnAsync(app, admin, "read")).Value!;
        await using var client = await ClientAsync(app, token);
        var detail = await OkAsync(client, "get_item", new { id = note.ToString() });
        Assert.AreEqual("read", detail.GetProperty("access").GetString(), "an admin's manage, capped at read");
        Assert.IsTrue((await client.ListToolsAsync()).All(t => ToolCatalog.Find(t.Name)!.Kind == ToolKind.Read));
    }
}

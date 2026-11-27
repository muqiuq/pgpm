using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PGPM.Auth;
using PGPM.Audit;
using PGPM.Lib.Helper;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>
/// The live channel through <c>TestServer.CreateWebSocketClient()</c> (docs/03 §11.13): a change by A reaches B, one
/// unacknowledged batch at a time, the origin check, a revoked session (4401), an access epoch bump (reset, 4409), the
/// connection limit (4429), editing notices.
/// </summary>
[TestClass]
public sealed class LiveSyncTests
{
    private const string Notes = "Notes:Create";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp(new Dictionary<string, string?>
    {
        ["Sync:Live:AckTimeoutSeconds"] = "3",
        ["Sync:Live:MaxConnectionsPerUser"] = "3",
    });

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<(Guid Group, Guid A, Guid B, SyncClient ClientA, SyncClient ClientB)> TeamAsync()
    {
        var group = await GroupAsync(app, "live");
        var a = await UserAsync(app, "anna", group);
        var b = await UserAsync(app, "ben", group);
        var clientA = new SyncClient(app.ClientFor(a, Notes));
        var clientB = new SyncClient(app.ClientFor(b, Notes));
        await clientA.SyncAllAsync();
        await clientB.SyncAllAsync();
        return (group, a, b, clientA, clientB);
    }

    private static async Task<LiveSocket> LiveAsync(SyncClient client, Guid user)
    {
        var socket = await LiveSocket.ConnectAsync(app, user, Notes);
        var welcome = await socket.HelloAsync(client);
        Assert.AreEqual("welcome", welcome.Str("type"), welcome.ToJsonString());
        Assert.IsGreaterThanOrEqualTo(client.Cursor, welcome.Long("head"));
        return socket;
    }

    private static Func<JsonObject, bool> Carries(Guid id) =>
        m => m.Str("type") == "changes" && m["changes"]!["items"] is JsonArray items && items.Any(r => r.Str("id") == id.ToString());

    private static JsonArray SharedNote(Guid id, Guid group, string title)
    {
        var batch = Ids.New();
        return [Insert("items", id, Note(title), batch), Insert("item_shares", Ids.New(), Share(id, group, "edit"), batch)];
    }

    [TestMethod]
    public async Task AChangeByAReachesBLive()
    {
        var (group, _, b, clientA, clientB) = await TeamAsync();
        await using var live = await LiveAsync(clientB, b);
        var id = Ids.New();
        var started = DateTime.UtcNow;
        await clientA.SyncAsync(SharedNote(id, group, "Live note").Select(m => m!.AsObject()), pull: false);
        var changes = await live.WaitForAsync(Carries(id), Wait);
        Assert.IsNotNull(changes, "B got A's note");
        Assert.IsLessThan(TimeSpan.FromSeconds(1), DateTime.UtcNow - started, "within a second");
        var row = changes["changes"]!["items"]!.AsArray().Single(r => r.Str("id") == id.ToString())!;
        Assert.AreEqual("edit", row.Str("access"));
        Assert.IsTrue(changes.ContainsKey("evictions"));
        Assert.IsTrue(changes.ContainsKey("more"));
        Assert.IsGreaterThan(clientB.Cursor, changes.Long("cursor"));
        clientB.ApplyRows(changes["changes"]!.AsObject(), changes.Arr("evictions"));
        Assert.AreEqual("Live note", clientB.Row("items", id).Str("title"));
    }

    [TestMethod]
    public async Task OneUnacknowledgedBatchAtATime()
    {
        var (group, _, b, clientA, clientB) = await TeamAsync();
        await using var live = await LiveAsync(clientB, b);
        var first = Ids.New();
        await clientA.SyncAsync(SharedNote(first, group, "First").Select(m => m!.AsObject()), pull: false);
        var batch = await live.WaitForAsync(Carries(first), Wait);
        Assert.IsNotNull(batch);

        var second = Ids.New();
        await clientA.SyncAsync(SharedNote(second, group, "Second").Select(m => m!.AsObject()), pull: false);
        Assert.IsNull(await live.NextAsync(TimeSpan.FromMilliseconds(700)), "nothing before the ack");
        await live.AckAsync(batch);
        Assert.IsNotNull(await live.WaitForAsync(Carries(second), Wait), "the next batch after the ack");
    }

    [TestMethod]
    public async Task NoAckWithinTheTimeoutCloses()
    {
        var (group, _, b, clientA, clientB) = await TeamAsync();
        var live = await LiveAsync(clientB, b);
        await using var _ = live;
        var id = Ids.New();
        await clientA.SyncAsync(SharedNote(id, group, "Unacknowledged").Select(m => m!.AsObject()), pull: false);
        Assert.IsNotNull(await live.WaitForAsync(Carries(id), Wait));
        Assert.AreEqual(WebSocketCloseStatus.PolicyViolation, await live.ClosedAsync(TimeSpan.FromSeconds(8)));
    }

    [TestMethod]
    public async Task AnotherOriginIsRefused()
    {
        var user = await UserAsync(app, "origin");
        foreach (var origin in new[] { "https://evil.example", null })
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => LiveSocket.ConnectAsync(app, user, origin: origin));
            Assert.Contains("403", e.Message);
        }
        using var plain = await app.ClientFor(user).GetAsync("/api/v1/sync/live");
        Assert.AreEqual(HttpStatusCode.BadRequest, plain.StatusCode);
        using var anonymous = await app.Client().GetAsync("/api/v1/sync/live");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [TestMethod]
    public async Task ARevokedSessionCloses4401()
    {
        var user = await UserAsync(app, "revoked");
        var client = new SyncClient(app.ClientFor(user));
        await client.SyncAllAsync();
        await using var live = await LiveAsync(client, user);
        await using (var db = app.NewDb())
        {
            await new UserAccounts(db, app.Services.GetRequiredService<SessionCache>()).RevokeSessionsAsync(user, AuditWho.Cli);
        }
        var message = await live.WaitForAsync(m => m.Str("type") == "session", Wait);
        Assert.AreEqual("revoked", message.Str("state"));
        Assert.AreEqual(4401, (int?)await live.ClosedAsync(Wait));
    }

    [TestMethod]
    public async Task AnAccessEpochBumpResets4409()
    {
        var group = await GroupAsync(app, "epoch");
        var user = await UserAsync(app, "epoch");
        var client = new SyncClient(app.ClientFor(user));
        await client.SyncAllAsync();
        await using var live = await LiveAsync(client, user);
        using (var added = await app.Client(TestUsers.Admin).PutJsonAsync($"/api/v1/groups/{group}/members",
            new JsonObject { ["userIds"] = new JsonArray(user.ToString()) }))
        {
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode);
        }
        Assert.IsNotNull(await live.WaitForAsync(m => m.Str("type") == "reset", Wait));
        Assert.AreEqual(4409, (int?)await live.ClosedAsync(Wait));

        // A hello with the old epoch is a reset too.
        var again = await LiveSocket.ConnectAsync(app, user);
        await using var _ = again;
        Assert.AreEqual("reset", (await again.HelloAsync(client)).Str("type"));
        Assert.AreEqual(4409, (int?)await again.ClosedAsync(Wait));
    }

    [TestMethod]
    public async Task AtMostThreeConnectionsPerUserHere()
    {
        var user = await UserAsync(app, "many");
        var client = new SyncClient(app.ClientFor(user));
        await client.SyncAllAsync();
        var sockets = new List<LiveSocket>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                sockets.Add(await LiveAsync(client, user));
            }
            var fourth = await LiveSocket.ConnectAsync(app, user);
            sockets.Add(fourth);
            Assert.AreEqual(4429, (int?)await fourth.ClosedAsync(Wait));
        }
        finally
        {
            foreach (var socket in sockets)
            {
                await socket.DisposeAsync();
            }
        }
    }

    /// <summary>S-11 (docs/06): after a rights change the connection computes access with the new rights (a map template's).</summary>
    [TestMethod]
    public async Task ARightsChangeReachesTheConnectionsComputedAccess()
    {
        var group = await GroupAsync(app, "live-rights");
        var user = await UserAsync(app, "rita", group);
        var admin = TestUsers.IdOf(TestUsers.Admin);
        var configurer = new SyncClient(app.ClientFor(admin, "Maps:Create,Configuration:Manage"));
        var template = Ids.New();
        await configurer.SyncAllAsync([Insert("maps", template, new JsonObject
        {
            ["kind"] = "canvas", ["name"] = "Workshop", ["settings"] = new JsonObject(), ["isTemplate"] = true,
        })]);
        var client = new SyncClient(app.ClientFor(user));
        await client.SyncAllAsync();
        Assert.AreEqual("read", client.Row("maps", template).Str("access"));
        await using var socket = await LiveAsync(client, user);

        using (var granted = await app.Client(TestUsers.Admin).PutJsonAsync($"/api/v1/groups/{group}/rights",
                   new JsonObject { ["rights"] = new JsonObject { ["Configuration"] = "Manage" } }))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.OK, granted.StatusCode, await granted.Content.ReadAsStringAsync());
        }
        Assert.IsNotNull(await socket.WaitForAsync(m => m.Str("type") == "session" && m.Str("state") == "rights_changed", Wait));
        await configurer.SyncAllAsync([Patch("maps", template, 1, ("name", "Workshop", "Workshop 2027"))]);
        var delivered = await socket.WaitForAsync(m => m.Str("type") == "changes" && m["changes"]!["maps"] is JsonArray maps
            && maps.Any(r => r.Str("id") == template.ToString()), Wait);
        Assert.IsNotNull(delivered);
        Assert.AreEqual("manage", delivered["changes"]!["maps"]!.AsArray().Single(r => r.Str("id") == template.ToString()).Str("access"));
    }

    [TestMethod]
    public async Task EditingIsAnnouncedToTheReaders()
    {
        var (group, a, b, clientA, clientB) = await TeamAsync();
        var id = Ids.New();
        await clientA.SyncAllAsync(SharedNote(id, group, "Edited").Select(m => m!.AsObject()));
        await clientB.SyncAllAsync();
        await using var live = await LiveAsync(clientB, b);
        var body = new JsonObject { ["itemId"] = id.ToString(), ["target"] = "description" };
        using (var started = await clientA.Http.PostJsonNodeAsync("/api/v1/editing", body))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, started.StatusCode, await started.Content.ReadAsStringAsync());
        }
        var editing = await live.WaitForAsync(m => m.Str("type") == "editing", Wait);
        Assert.AreEqual(id.ToString(), editing.Str("itemId"));
        Assert.AreEqual("description", editing.Str("target"));
        Assert.AreEqual(a.ToString(), editing.Str("userId"));
        Assert.AreEqual(await DisplayNameAsync(app, a), editing.Str("user"));
        Assert.IsNotNull(editing.Str("since"));
        using (var ended = await clientA.Http.DeleteJsonAsync("/api/v1/editing", body))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, ended.StatusCode);
        }
        var end = await live.WaitForAsync(m => m.Str("type") == "editing_end", Wait);
        Assert.AreEqual(id.ToString(), end.Str("itemId"));

        // Without edit access there's nothing to announce.
        var stranger = await UserAsync(app, "stranger");
        using var refused = await app.ClientFor(stranger).PostJsonNodeAsync("/api/v1/editing", body);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [TestMethod]
    public async Task ARightsChangeIsAnnounced()
    {
        var group = await GroupAsync(app, "rights");
        var user = await UserAsync(app, "rights", group);
        var client = new SyncClient(app.ClientFor(user));
        await client.SyncAllAsync();
        await using var live = await LiveAsync(client, user);
        using (var changed = await app.Client(TestUsers.Admin).PutJsonAsync($"/api/v1/groups/{group}/rights", new JsonObject { ["rights"] = new JsonObject { ["Notes"] = "Create" } }))
        {
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        }
        var message = await live.WaitForAsync(m => m.Str("type") == "session", Wait);
        Assert.AreEqual("rights_changed", message.Str("state"));
    }

    [TestMethod]
    public async Task AShutdownSaysTheVersion()
    {
        var own = new TestApp();
        var user = await UserAsync(own, "deploy");
        var client = new SyncClient(own.ClientFor(user));
        await client.SyncAllAsync();
        var live = await LiveSocket.ConnectAsync(own, user);
        await using var _ = live;
        Assert.AreEqual("welcome", (await live.HelloAsync(client)).Str("type"));
        var stopping = Task.Run(own.Dispose);
        var app = await live.WaitForAsync(m => m.Str("type") == "app", Wait);
        Assert.AreEqual(ServerInfo.Version, app.Str("version"));
        Assert.AreEqual(WebSocketCloseStatus.EndpointUnavailable, await live.ClosedAsync(Wait));
        await stopping;
    }

    [TestMethod]
    public async Task PingIsAnswered()
    {
        var user = await UserAsync(app, "ping");
        var client = new SyncClient(app.ClientFor(user));
        await client.SyncAllAsync();
        await using var live = await LiveAsync(client, user);
        await live.SendAsync(new JsonObject { ["type"] = "ping" });
        Assert.IsNotNull(await live.WaitForAsync(m => m.Str("type") == "pong", Wait));
    }
}

using System.Net;
using System.Net.WebSockets;
using Microsoft.EntityFrameworkCore;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Tests.Sync;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Hosting;

/// <summary>
/// Administration → System (docs/03 §9, docs/01 §3): <c>GET /api/v1/system</c> tells the instance, the database, the files'
/// disk, the sync's head and horizon, every device with its cursor and live flag, the tasks' health; <c>DELETE
/// /api/v1/system/clients/{id}</c> makes a device start over – at its next HTTP sync, and at once on its live channel.
/// </summary>
[TestClass]
public sealed class SystemTests
{
    private const string Notes = "Notes:Create";
    private const string SystemView = "System:View";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<SystemStatusItem> StatusAsync()
    {
        using var response = await app.ClientWithRights(SystemView).GetAsync("/api/v1/system");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<SystemStatusItem>();
    }

    [TestMethod]
    public async Task TheStatusTellsTheInstanceTheStorageTheSyncAndEveryDevice()
    {
        var user = await UserAsync(app, "sys");
        var device = new SyncClient(app.ClientFor(user, Notes));
        await device.SyncAllAsync([Insert("items", Ids.New(), Note("System page"))]);
        await device.SyncAsync(pull: true);
        await using var socket = await LiveSocket.ConnectAsync(app, user, Notes);
        Assert.AreEqual("welcome", (await socket.HelloAsync(device)).Str("type"));
        var offline = new SyncClient(app.ClientFor(user, Notes));
        await offline.SyncAllAsync();
        await using (var db = app.NewDb())
        {
            db.KeyValues.Add(new KeyValueEntity { Key = "task.tombstone_pruner", Value = "3 tombstones pruned", UpdatedAt = Clock.UtcNow });
            db.KeyValues.Add(new KeyValueEntity { Key = "task.log_pruner", Value = "old", UpdatedAt = Clock.UtcNow.AddDays(-5) });
            await db.SaveChangesAsync();
        }

        var status = await StatusAsync();

        Assert.AreEqual(ServerInfo.Version, status.Instance.Version);
        Assert.IsGreaterThanOrEqualTo(0, status.Instance.UptimeSeconds);
        Assert.IsFalse(status.Instance.BackgroundTasks, "the tests run without background tasks");
        Assert.IsGreaterThan(0, status.Database.SizeBytes);
        Assert.IsFalse(string.IsNullOrEmpty(status.Database.ServerVersion));
        Assert.IsNull(status.Files.Problem);
        Assert.IsGreaterThanOrEqualTo(device.Cursor, status.Sync.Head);
        Assert.IsGreaterThanOrEqualTo(1, status.Sync.LiveConnections);
        var live = status.Clients.Single(c => c.Id == device.ClientId);
        Assert.IsTrue(live.Live);
        Assert.AreEqual(user, live.UserId);
        Assert.AreEqual(await DisplayNameAsync(app, user), live.UserName);
        Assert.AreEqual("test", live.AppVersion);
        Assert.AreEqual(status.Sync.Head - live.Cursor, live.Behind);
        Assert.IsFalse(status.Clients.Single(c => c.Id == offline.ClientId).Live);
        var pruner = status.Tasks.Single(t => t.Key == "task.tombstone_pruner");
        Assert.AreEqual("3 tombstones pruned", pruner.Summary);
        Assert.AreEqual(60, pruner.IntervalMinutes);
        // Overdue, but the background tasks are off here: nothing is stale.
        Assert.IsFalse(status.Tasks.Single(t => t.Key == "task.log_pruner").Stale);
        Assert.IsTrue(status.Tasks.Any(t => t.Key == "task.file_check" && t.LastRunAt is null));
        Assert.IsFalse(status.Tasks.Any(t => t.Key == "task.directory_sync"), "the directory sync is off");
    }

    [TestMethod]
    public async Task DeletingADeviceResetsItAtItsNextSyncAndOnItsLiveChannel()
    {
        var user = await UserAsync(app, "sysreset");
        var device = new SyncClient(app.ClientFor(user, Notes));
        await device.SyncAllAsync([Insert("items", Ids.New(), Note("Kept on the server"))]);
        await device.SyncAsync();
        var other = new SyncClient(app.ClientFor(user, Notes));
        await other.SyncAllAsync();
        await other.SyncAsync();
        await using var socket = await LiveSocket.ConnectAsync(app, user, Notes);
        Assert.AreEqual("welcome", (await socket.HelloAsync(device)).Str("type"));

        using var response = await app.ClientWithRights(SystemView, "sysadmin").DeleteAsync($"/api/v1/system/clients/{device.ClientId}");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync());

        Assert.IsNotNull(await socket.WaitForAsync(m => m.Str("type") == "reset", Wait), "the live channel is told");
        Assert.AreEqual((WebSocketCloseStatus)4409, await socket.ClosedAsync(Wait));
        var answer = await device.SyncAsync();
        Assert.IsTrue(answer.Bool("reset"), answer.ToJsonString());
        Assert.AreEqual(0, answer.Long("cursor"));
        // The other device of the same user is left alone; the reset device pulls everything again.
        Assert.IsFalse((await other.SyncAsync()).Bool("reset"));
        await device.SyncAllAsync();
        Assert.IsTrue(device.Rows("items").Any(r => r.Str("title") == "Kept on the server"));

        await using var db = app.NewDb();
        var entry = await db.AuditLog.AsNoTracking().Where(a => a.Event == AuditEvents.SystemClientReset).OrderByDescending(a => a.At).FirstAsync();
        Assert.AreEqual(user, entry.SubjectId);
        StringAssert.Contains(AuditLog.ReadDetail(entry.Detail).Summary, device.ClientId.ToString());
    }

    [TestMethod]
    public async Task AnUnknownDeviceIsNotFoundAndTheRightIsNeeded()
    {
        using var unknown = await app.ClientWithRights(SystemView).DeleteAsync($"/api/v1/system/clients/{Guid.NewGuid()}");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        var user = await UserAsync(app, "sysplain");
        using var refused = await app.ClientFor(user, Notes).GetAsync("/api/v1/system");
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Lib.Helper;
using PGPM.Persons;
using PGPM.Persons.DirectorySync;
using PGPM.Sync;
using PGPM.Tests.Support;
using PGPM.Tests.Sync;

namespace PGPM.Tests.DirectorySync;

/// <summary>
/// The directory sync against a fake Graph (docs/03 §8): a tenant of its own per test – full runs over several pages, delta
/// runs with changes, removals and disabled accounts, no seq for an unchanged person, 410 → a full run, 429/503 waited out,
/// the weekly full run, linking persons to users, a failure in the state and the audit, the cached token, the endpoints and
/// the task, and live delivery.
/// </summary>
[TestClass]
public sealed class DirectorySyncTests
{
    private static readonly FakeGraph Graph = new();
    private static FakeGraph.Tenant configured = null!;
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        configured = Graph.NewTenant();
        app = new TestApp(new Dictionary<string, string?>
        {
            ["Directory:Enabled"] = "true",
            ["Entra:TenantId"] = configured.Id.ToString(),
            ["Entra:ClientId"] = FakeGraph.ClientId,
            ["Entra:ClientSecret"] = FakeGraph.ClientSecret,
        }, services => services.AddHttpClient(GraphDirectoryClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Graph));
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static DirectoryCredentials Credentials(FakeGraph.Tenant tenant) => new(tenant.Id, FakeGraph.ClientId, FakeGraph.ClientSecret);

    private static async Task<DirectoryRunResult> RunAsync(FakeGraph.Tenant tenant, bool? full = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DirectorySyncService>().RunAsync(Credentials(tenant), full, CancellationToken.None);
    }

    private static async Task<Dictionary<Guid, PersonEntity>> PersonsAsync(FakeGraph.Tenant tenant)
    {
        await using var db = app.NewDb();
        return await db.Persons.AsNoTracking().Where(p => p.EntraTenantId == tenant.Id).ToDictionaryAsync(p => p.EntraObjectId!.Value);
    }

    private static async Task<DirectorySyncStateEntity> StateAsync(FakeGraph.Tenant tenant)
    {
        await using var db = app.NewDb();
        return await db.DirectorySyncState.AsNoTracking().SingleAsync(s => s.TenantId == tenant.Id);
    }

    private static (FakeGraph.Tenant Tenant, Guid Anna, Guid Ben, Guid Guest, Guid Disabled, Guid NoName) Directory()
    {
        var tenant = Graph.NewTenant();
        var anna = tenant.Put(FakeGraph.User("Anna Muster", "Anna.Muster@Example.com", job: "Engineer"));
        var ben = tenant.Put(FakeGraph.User("Ben Beispiel", "ben@example.com"));
        var guest = tenant.Put(FakeGraph.User("Gina Gast", "gina@partner.example", userType: "Guest"));
        var disabled = tenant.Put(FakeGraph.User("Old Account", "old@example.com", enabled: false));
        var noName = FakeGraph.User("x", "nn@example.com");
        noName["displayName"] = null;
        noName["userPrincipalName"] = "nn@example.com";
        return (tenant, anna, ben, guest, disabled, tenant.Put(noName));
    }

    [TestMethod]
    public async Task AFullRunReadsEveryPageAndKeepsTheDeltaLink()
    {
        var (tenant, anna, _, guest, disabled, noName) = Directory();
        var result = await RunAsync(tenant);
        Assert.AreEqual((true, "full", 5, 0, 0, 5), (result.Ok, result.Mode, result.Created, result.Updated, result.Deactivated, result.Seen), result.Error);
        Assert.AreEqual(3, tenant.Requests.Count, "five accounts, two a page");

        var persons = await PersonsAsync(tenant);
        var a = persons[anna];
        Assert.AreEqual((PersonSource.Microsoft, "Anna Muster", "anna.muster@example.com", "Engineer", "+41 44 000 00 00", "Member", true),
            (a.Source, a.DisplayName, a.Email, a.JobTitle, a.Phone, a.UserType, a.IsActive), "the e-mail lower-cased, the first business phone");
        Assert.AreEqual(("Guest", false, "nn@example.com"), (persons[guest].UserType, persons[disabled].IsActive, persons[noName].DisplayName),
            "a guest; a disabled account inactive; no name: the UPN");
        Assert.IsTrue(persons.Values.All(p => p.Seq > 0 && p.Rev == 1 && p.CreatedBy is null && p.DirectorySyncedAt is not null));

        var state = await StateAsync(tenant);
        Assert.AreEqual((DirectoryRunStatus.Ok, "full", 5), (state.LastStatus, state.LastMode, state.CreatedCount));
        StringAssert.Contains(state.DeltaLink, "$deltatoken=");
        Assert.IsNotNull(state.LastFullAt);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.ChangeLog.AnyAsync(c => c.Entity == "persons" && c.EntityId == a.Id && c.Source == ChangeSource.Directory && c.Action == "create"));
    }

    [TestMethod]
    public async Task ADeltaRunAppliesWhatChangedAndStampsNothingElse()
    {
        var (tenant, anna, ben, guest, disabled, noName) = Directory();
        await RunAsync(tenant);
        var before = await PersonsAsync(tenant);

        var nothing = await RunAsync(tenant);
        Assert.AreEqual(("delta", 0, 0, 0, 0), (nothing.Mode, nothing.Created, nothing.Updated, nothing.Deactivated, nothing.Seen));
        var full = await RunAsync(tenant, full: true);
        Assert.AreEqual(("full", 0, 0, 0, 5), (full.Mode, full.Created, full.Updated, full.Deactivated, full.Seen));
        var unchanged = await PersonsAsync(tenant);
        Assert.IsTrue(before.All(p => unchanged[p.Key].Seq == p.Value.Seq && unchanged[p.Key].Rev == p.Value.Rev), "a run that changes nothing stamps nothing");

        tenant.Change(anna, u => u["jobTitle"] = "Lead engineer");
        tenant.Remove(ben);
        tenant.Change(guest, u => u["accountEnabled"] = false);
        tenant.Change(disabled, u => u["accountEnabled"] = true);
        var newcomer = tenant.Put(FakeGraph.User("Nina Neu", "nina@example.com"));
        var requests = tenant.Requests.Count;
        var result = await RunAsync(tenant);
        Assert.AreEqual((true, "delta", 1, 2, 2, 5), (result.Ok, result.Mode, result.Created, result.Updated, result.Deactivated, result.Seen), result.Error);
        var pages = tenant.Requests.Skip(requests).Select(r => r.Url).ToList();
        StringAssert.Contains(pages[0], "$deltatoken=", "from the stored delta link");
        Assert.AreEqual(3, pages.Count, "five changes, two a page: next links followed");

        var after = await PersonsAsync(tenant);
        Assert.AreEqual(("Lead engineer", 2), (after[anna].JobTitle, after[anna].Rev));
        Assert.AreEqual((false, false), (after[ben].IsActive, after[ben].Deleted), "removed: inactive, never deleted");
        Assert.IsFalse(after[guest].IsActive);
        Assert.IsTrue(after[disabled].IsActive, "enabled again");
        Assert.IsTrue(after.ContainsKey(newcomer));
        Assert.AreEqual(before[noName].Seq, after[noName].Seq, "untouched");
        Assert.IsTrue(new[] { anna, ben, guest, disabled }.All(id => after[id].Seq > before[noName].Seq));
    }

    [TestMethod]
    public async Task AnExpiredDeltaLinkStartsAFullRun()
    {
        var (tenant, anna, ben, _, _, _) = Directory();
        await RunAsync(tenant);
        tenant.DeltaExpired = true;
        tenant.Forget(ben);
        tenant.Change(anna, u => u["department"] = "Research");
        var result = await RunAsync(tenant);
        Assert.AreEqual((true, "full", 1, 1), (result.Ok, result.Mode, result.Updated, result.Deactivated), result.Error);
        Assert.IsTrue(tenant.Requests.Any(r => r.Status == 410));
        var persons = await PersonsAsync(tenant);
        Assert.AreEqual(("Research", false), (persons[anna].Department, persons[ben].IsActive));
        Assert.IsFalse((await StateAsync(tenant)).DeltaLink!.Contains("skiptoken", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ThrottlingIsWaitedOutFromTheSamePage()
    {
        var (tenant, _, _, _, _, _) = Directory();
        await RunAsync(tenant);
        tenant.Put(FakeGraph.User("Paul Page", "paul@example.com"));
        tenant.Throttle.Enqueue((HttpStatusCode.TooManyRequests, 1));
        tenant.Throttle.Enqueue((HttpStatusCode.ServiceUnavailable, 1));
        var watch = Stopwatch.StartNew();
        var result = await RunAsync(tenant);
        Assert.AreEqual((true, 1), (result.Ok, result.Created), result.Error);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9), watch.Elapsed, "Retry-After honoured, twice");
        var last = tenant.Requests.TakeLast(3).ToList();
        Assert.AreEqual("429 503 200", string.Join(" ", last.Select(r => r.Status)));
        Assert.AreEqual(1, last.Select(r => r.Url).Distinct().Count(), "the same page again");
    }

    [TestMethod]
    public async Task TheWeeklyFullRunMakesUnseenPersonsInactive()
    {
        var (tenant, anna, _, _, _, _) = Directory();
        await RunAsync(tenant);
        tenant.Forget(anna);
        Assert.AreEqual("delta", (await RunAsync(tenant)).Mode);
        Assert.IsTrue((await PersonsAsync(tenant))[anna].IsActive, "a delta run can't see what it wasn't told");

        await using (var db = app.NewDb())
        {
            await db.DirectorySyncState.Where(s => s.TenantId == tenant.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastFullAt, Clock.UtcNow.AddDays(-8)));
        }
        var weekly = await RunAsync(tenant);
        Assert.AreEqual(("full", 1), (weekly.Mode, weekly.Deactivated));
        Assert.IsFalse((await PersonsAsync(tenant))[anna].IsActive);
        Assert.IsTrue((await StateAsync(tenant)).LastFullAt > Clock.UtcNow.AddMinutes(-1));
    }

    [TestMethod]
    public async Task APersonIsLinkedToTheUserWithTheSameIdentity()
    {
        var (tenant, anna, ben, _, _, _) = Directory();
        // Anna has a PGPM account linked to her Microsoft identity, with the manual person every local user gets.
        var annaUser = await SyncKit.UserAsync(app, "anna");
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == annaUser).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.MicrosoftTenantId, tenant.Id).SetProperty(x => x.MicrosoftObjectId, anna));
        }
        Guid annaPerson;
        await using (var db = app.NewDb())
        {
            annaPerson = await db.Persons.Where(p => p.UserId == annaUser).Select(p => p.Id).SingleAsync();
        }
        var first = await RunAsync(tenant);
        Assert.AreEqual(4, first.Created, "Anna's manual person became her directory person");
        var persons = await PersonsAsync(tenant);
        Assert.AreEqual((annaPerson, annaUser, PersonSource.Microsoft, "Anna Muster"),
            (persons[anna].Id, persons[anna].UserId!.Value, persons[anna].Source, persons[anna].DisplayName));

        // Ben's account is linked after the directory brought his person: the next run links it, his manual person lets go.
        var benUser = await SyncKit.UserAsync(app, "ben");
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == benUser).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.MicrosoftTenantId, tenant.Id).SetProperty(x => x.MicrosoftObjectId, ben));
        }
        tenant.Change(ben, u => u["jobTitle"] = "Designer");
        var second = await RunAsync(tenant);
        Assert.AreEqual(1, second.Linked);
        await using (var db = app.NewDb())
        {
            var linked = await db.Persons.SingleAsync(p => p.UserId == benUser);
            Assert.AreEqual((ben, "Designer"), (linked.EntraObjectId!.Value, linked.JobTitle));
            Assert.AreEqual(1, await db.Persons.CountAsync(p => p.UserId == benUser), "every user has one person");
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.PersonLinked && l.SubjectId == linked.Id && l.ActorRef == "directory"));
        }
    }

    [TestMethod]
    public async Task AFailureIsRecordedAndAudited()
    {
        var (tenant, _, _, _, _, _) = Directory();
        tenant.RefuseToken = true;
        var result = await RunAsync(tenant);
        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "invalid_client");
        Assert.IsFalse(result.Error!.Contains(FakeGraph.ClientSecret, StringComparison.Ordinal), "never the secret");
        var state = await StateAsync(tenant);
        Assert.AreEqual((DirectoryRunStatus.Failed, null), (state.LastStatus, state.DeltaLink));
        StringAssert.Contains(state.LastError, "AADSTS7000215");
        await using var db = app.NewDb();
        var entries = await db.AuditLog.Where(l => l.Event == AuditEvents.DirectorySyncFailed).ToListAsync();
        Assert.IsTrue(entries.Any(e => AuditLog.ReadDetail(e.Detail).Summary?.Contains("AADSTS7000215", StringComparison.Ordinal) == true));
        Assert.IsTrue(await db.KeyValues.AnyAsync(k => k.Key == DirectorySyncService.HealthKey));

        tenant.RefuseToken = false;
        Assert.IsTrue((await RunAsync(tenant)).Ok, "the next run starts over");
    }

    [TestMethod]
    public async Task TheTokenIsCachedBetweenRuns()
    {
        var (tenant, _, _, _, _, _) = Directory();
        await RunAsync(tenant);
        await RunAsync(tenant);
        await RunAsync(tenant, full: true);
        Assert.AreEqual(1, tenant.TokenRequests);
    }

    [TestMethod]
    public async Task ADirectoryChangeReachesALiveClient()
    {
        var (tenant, anna, _, _, _, _) = Directory();
        await RunAsync(tenant);
        var user = await SyncKit.UserAsync(app, "watch-directory");
        var device = new SyncClient(app.ClientFor(user));
        await device.SyncAllAsync();
        await using var live = await LiveSocket.ConnectAsync(app, user);
        Assert.AreEqual("welcome", (await live.HelloAsync(device)).Str("type"));
        tenant.Change(anna, u => u["jobTitle"] = "Live title");
        await RunAsync(tenant);
        var personId = (await PersonsAsync(tenant))[anna].Id.ToString();
        var changes = await live.WaitForAsync(m => m.Str("type") == "changes" && m["changes"]!["persons"] is JsonArray rows
            && rows.Any(r => r.Str("id") == personId && r.Str("jobTitle") == "Live title"), TimeSpan.FromSeconds(10));
        Assert.IsNotNull(changes, "everybody's directory follows Graph live");
    }

    [TestMethod]
    public async Task TheEndpointsShowTheStatusAndRunNowWakesTheTask()
    {
        configured.Put(FakeGraph.User("Configured Tenant", "ct@example.com"));
        var admin = app.Client(TestUsers.Admin);
        var status = await (await admin.GetAsync("/api/v1/directory/status")).ReadAsync<DirectoryStatusItem>();
        Assert.AreEqual((false, configured.Id.ToString(), FakeGraph.ClientId, true), (status.Sso.Enabled, status.Sso.TenantId, status.Sso.ClientId,
            status.Sso.ClientSecretSet));
        Assert.AreEqual((true, true, 6, 7), (status.Directory.Enabled, status.Directory.Configured, status.Directory.IntervalHours, status.Directory.FullSyncDays));
        var raw = await (await admin.GetAsync("/api/v1/directory/status")).Content.ReadAsStringAsync();
        Assert.IsFalse(raw.Contains(FakeGraph.ClientSecret, StringComparison.Ordinal), "never the secret");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(TestUsers.Member).GetAsync("/api/v1/directory/status")).StatusCode);

        var task = app.Services.GetRequiredService<DirectorySyncTask>();
        await task.StartAsync(CancellationToken.None);
        try
        {
            using (var run = await admin.PostAsync("/api/v1/directory/sync?full=true", null))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, run.StatusCode, await run.Content.ReadAsStringAsync());
            }
            DirectoryRunItem? last = null;
            for (var i = 0; i < 100 && last?.Status != DirectoryRunStatus.Ok; i++)
            {
                await Task.Delay(100);
                last = (await (await admin.GetAsync("/api/v1/directory/status")).ReadAsync<DirectoryStatusItem>()).LastRun;
            }
            Assert.AreEqual((DirectoryRunStatus.Ok, "full"), (last?.Status, last?.Mode), last?.Error);
            Assert.IsGreaterThanOrEqualTo(1, last!.Seen);
        }
        finally
        {
            await task.StopAsync(CancellationToken.None);
        }
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.DirectorySyncRequested && l.ActorUserId == TestUsers.IdOf(TestUsers.Admin)));

        using var off = new TestApp();
        using var refused = await off.Client(TestUsers.Admin).PostAsync("/api/v1/directory/sync", null);
        Assert.AreEqual((HttpStatusCode.Conflict, "directory_off"), (refused.StatusCode, await refused.CodeAsync()));
    }

    [TestMethod]
    public void ThePropertiesMapAsGraphSendsThem()
    {
        var person = new PersonEntity { DisplayName = "Kept", Email = "kept@example.com", JobTitle = "Kept", IsActive = true };
        DirectorySyncService.Apply(person, new JsonObject { ["jobTitle"] = null, ["userType"] = "guest", ["accountEnabled"] = false });
        Assert.AreEqual(("Kept", "kept@example.com", null, "Guest", false), (person.DisplayName, person.Email, person.JobTitle, person.UserType, person.IsActive),
            "a missing key keeps the value, null clears it");
        DirectorySyncService.Apply(person, new JsonObject { ["displayName"] = "  ", ["userPrincipalName"] = "k@example.com", ["businessPhones"] = new JsonArray() });
        Assert.AreEqual(("k@example.com", null), (person.DisplayName, person.Phone));
    }
}

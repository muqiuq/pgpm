using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Persons;

namespace PGPM.Tests.Auth;

/// <summary>Users, groups and rights for admins (PNMTD, docs/03 §4.5–§4.7, §6), through the API with the test sign-in.</summary>
[TestClass]
public sealed class AdministrationTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static HttpClient Admin => app.Client(TestUsers.Admin);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    [TestMethod]
    public async Task ANewUserHasAManualPersonATemporaryPasswordAndTheirGroups()
    {
        var name = Unique("new");
        using var response = await Admin.PostJsonAsync("/api/v1/users",
            new { username = name, displayName = "Ana Muster", email = "Ana@Example.com", groupIds = new[] { GroupEntity.MembersId } });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadAsync<CreatedUser>();

        Assert.AreEqual(("Ana Muster", "Ana@Example.com", true, true), (created.User.DisplayName, created.User.Email, created.User.MustChangePassword,
            created.User.HasPassword));
        Assert.AreEqual("Members", created.User.Groups.Single().Name);
        await using var db = app.NewDb();
        var person = await db.Persons.SingleAsync(p => p.UserId == created.User.Id);
        Assert.AreEqual((created.User.PersonId!.Value, PersonSource.Manual, "Ana Muster", "ana@example.com"), (person.Id, person.Source, person.DisplayName,
            person.Email));

        // The person follows the user's name and e-mail.
        await (await Admin.PutJsonAsync($"/api/v1/users/{created.User.Id}", new { displayName = "Ana Beispiel", email = "ana.b@example.com", isActive = true }))
            .ReadAsync<UserItem>();
        await using var after = app.NewDb();
        var renamed = await after.Persons.SingleAsync(p => p.Id == person.Id);
        Assert.AreEqual(("Ana Beispiel", "ana.b@example.com"), (renamed.DisplayName, renamed.Email));

        using var duplicate = await Admin.PostJsonAsync("/api/v1/users", new { username = name.ToUpperInvariant() });
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var invalid = await Admin.PostJsonAsync("/api/v1/users", new { username = "no spaces allowed", email = "not-an-address" });
        var errors = (await invalid.ProblemAsync()).GetProperty("errors");
        Assert.IsTrue(errors.TryGetProperty("username", out _) && errors.TryGetProperty("email", out _));
    }

    [TestMethod]
    public async Task TheLastActiveAdminCannotBeRemovedDemotedOrDeactivated()
    {
        // A database of its own: the run's has other admins.
        var connection = TestDefaults.CreateDatabase("pgpm_test_last_admin");
        using var lonely = new TestApp(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = connection,
            ["Auth:InitialAdmin:Username"] = "solo",
            ["Auth:InitialAdmin:Password"] = "solo-password-1",
        });
        Guid soloId;
        await using (var db = lonely.NewDb())
        {
            await Task.Delay(0);
            soloId = await db.Users.Where(u => u.Username == "solo").Select(u => u.Id).SingleAsync();
        }
        var browser = new Browser(lonely);
        var login = await browser.LoginAsync("solo", "solo-password-1");
        Assert.IsTrue(login.Me!.Flags.MustChangePassword, "the seeded admin changes the password first");
        await browser.PostAsync("/api/v1/account/password", new { currentPassword = "solo-password-1", newPassword = "solo-password-2" });

        foreach (var (method, body) in new (HttpMethod, object?)[]
        {
            (HttpMethod.Put, new { displayName = "solo", isAdmin = false, isActive = true }),
            (HttpMethod.Put, new { displayName = "solo", isActive = false }),
            (HttpMethod.Delete, null),
        })
        {
            using var refused = await browser.SendAsync(method, $"/api/v1/users/{soloId}", body);
            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode, method.Method);
            Assert.AreEqual("last_admin", await refused.CodeAsync());
        }
        var second = await (await browser.PostAsync("/api/v1/users", new { username = "second", isAdmin = true })).ReadAsync<CreatedUser>();
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Put, $"/api/v1/users/{second.User.Id}",
            new { displayName = "second", isAdmin = false, isActive = true }));
        Assert.AreEqual(HttpStatusCode.Conflict, await browser.StatusAsync(HttpMethod.Delete, $"/api/v1/users/{soloId}"), "the other is no admin any more");
    }

    [TestMethod]
    public async Task UsersAreListedSearchedAndSorted()
    {
        var name = Unique("zz-list");
        await (await Admin.PostJsonAsync("/api/v1/users", new { username = name, displayName = "Listed Person" })).ReadAsync<CreatedUser>();

        var found = await (await Admin.GetAsync($"/api/v1/users?search={name}")).ReadAsync<Paged<UserItem>>();
        Assert.AreEqual(1, found.TotalCount);
        var members = await (await Admin.GetAsync($"/api/v1/users?groupId={GroupEntity.MembersId}&pageSize=500")).ReadAsync<Paged<UserItem>>();
        Assert.IsTrue(members.Items.Any(u => u.Username == TestUsers.Member));
        Assert.IsFalse(members.Items.Any(u => u.Username == TestUsers.Viewer));
        var sorted = await (await Admin.GetAsync("/api/v1/users?sort=-username&pageSize=500")).ReadAsync<Paged<UserItem>>();
        CollectionAssert.AreEqual(sorted.Items.Select(u => u.Username.ToLowerInvariant()).OrderDescending(StringComparer.Ordinal).ToList(),
            sorted.Items.Select(u => u.Username.ToLowerInvariant()).ToList());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/users?sort=password")).StatusCode);
    }

    [TestMethod]
    public async Task GroupsHaveRightsMembersAndAnAuditTrail()
    {
        var name = $"Innovation {Guid.NewGuid():N}";
        var group = await (await Admin.PostJsonAsync("/api/v1/groups", new
        {
            name, description = "The innovation team",
            rights = new Dictionary<string, string> { ["Ideas"] = "Create", ["Mcp"] = "View", ["Maps"] = "None" },
        })).ReadAsync<GroupDetail>();
        Assert.AreEqual(2, group.Rights.Count);
        Assert.AreEqual(Level.View, group.Rights[Area.Mcp]);

        using (var wrongLevel = await Admin.PutJsonAsync($"/api/v1/groups/{group.Id}/rights", new { rights = new Dictionary<string, string> { ["Notes"] = "Manage" } }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongLevel.StatusCode);
            StringAssert.Contains((await wrongLevel.ProblemAsync()).GetProperty("errors").GetProperty("rights")[0].GetString(), "Notes has no level Manage");
        }
        var rights = await (await Admin.PutJsonAsync($"/api/v1/groups/{group.Id}/rights",
            new { rights = new Dictionary<string, string> { ["Notes"] = "Create", ["AuditLog"] = "View" } })).ReadAsync<GroupDetail>();
        CollectionAssert.AreEquivalent(new[] { Area.Notes, Area.AuditLog }, rights.Rights.Keys.ToArray());

        var renamed = await (await Admin.PutJsonAsync($"/api/v1/groups/{group.Id}", new { name = name + " team", entraAppRole = " pgpm.innovation" }))
            .ReadAsync<GroupDetail>();
        Assert.AreEqual(("PGPM.INNOVATION", 2), (renamed.EntraAppRole, renamed.Rights.Count), "upper-cased; no rights in the request: unchanged");
        renamed = await (await Admin.PutJsonAsync($"/api/v1/groups/{group.Id}", new { name = name + " team", entraAppRole = (string?)null })).ReadAsync<GroupDetail>();

        var members = await (await Admin.PutJsonAsync($"/api/v1/groups/{group.Id}/members",
            new { userIds = new[] { TestUsers.IdOf(TestUsers.Viewer) } })).ReadAsync<GroupDetail>();
        Assert.AreEqual(TestUsers.Viewer, members.Members.Single().Username);
        var access = await (await Admin.GetAsync($"/api/v1/users/{TestUsers.IdOf(TestUsers.Viewer)}/access")).ReadAsync<AccessItem>();
        Assert.IsTrue(access.Rights.Any(r => r.Area == Area.AuditLog && r.From.Contains(renamed.Name)));

        using (var taken = await Admin.PostJsonAsync("/api/v1/groups", new { name = name.ToUpperInvariant() + " TEAM" }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, taken.StatusCode);
        }
        Assert.AreEqual(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/groups/{group.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Admin.GetAsync($"/api/v1/groups/{group.Id}")).StatusCode);

        await using var db = app.NewDb();
        var entries = await db.AuditLog.Where(l => l.SubjectId == group.Id).OrderBy(l => l.At).ToListAsync();
        CollectionAssert.AreEqual(new[]
            {
                "group.created succeeded", "group.rights_changed failed", "group.rights_changed succeeded", "group.updated succeeded",
                "group.updated succeeded", "group.members_changed succeeded", "group.deleted succeeded",
            },
            entries.Select(e => $"{e.Event} {AuditLog.ReadDetail(e.Detail).Outcome.ToString().ToLowerInvariant()}").ToArray());
    }

    [TestMethod]
    public async Task TheSeededGroupsAreMembersAndViewers()
    {
        var groups = await (await Admin.GetAsync("/api/v1/groups")).ReadAsync<List<GroupItem>>();
        var members = groups.Single(g => g.Id == GroupEntity.MembersId);
        var viewers = groups.Single(g => g.Id == GroupEntity.ViewersId);
        Assert.AreEqual(("Members", "Viewers"), (members.Name, viewers.Name));
        CollectionAssert.AreEquivalent(new[] { Area.Notes, Area.Ideas, Area.Leads, Area.Projects, Area.Maps, Area.Mcp }, members.Rights.Keys.ToArray());
        Assert.IsTrue(members.Rights.Where(r => r.Key != Area.Mcp).All(r => r.Value == Level.Create));
        Assert.AreEqual(Level.Use, members.Rights[Area.Mcp]);
        Assert.AreEqual(0, viewers.Rights.Count);
    }

    /// <summary>The catalogue of docs/03 §6.2, exactly: every signed-in user reads it.</summary>
    [TestMethod]
    public async Task TheRightsCatalogueIsTheDocs()
    {
        var catalogue = await (await app.Client(TestUsers.Viewer).GetAsync("/api/v1/rights")).ReadAsync<List<RightsAreaItem>>();
        var shown = catalogue.Select(a => $"{a.Area}:{string.Join("<", a.Levels.Select(l => l.Level))}").ToList();
        CollectionAssert.AreEqual(new[]
        {
            "Notes:Create", "Ideas:Create", "Leads:Create", "Projects:Create", "Maps:Create", "Persons:Edit", "Configuration:Manage", "Directory:Manage",
            "Mcp:Use<View<Manage", "AuditLog:View", "System:View",
        }, shown);
    }

    [TestMethod]
    public async Task TheAuditLogShowsWhoChangedWhatAndFilters()
    {
        var name = Unique("audited");
        var created = await (await Admin.PostJsonAsync("/api/v1/users", new { username = name })).ReadAsync<CreatedUser>();
        await (await Admin.PutJsonAsync($"/api/v1/users/{created.User.Id}", new { displayName = "Audited Person", isActive = true })).ReadAsync<UserItem>();

        var auditor = app.ClientWithRights("AuditLog:View");
        var page = await (await auditor.GetAsync($"/api/v1/audit?userId={created.User.Id}")).ReadAsync<Paged<AuditEntryItem>>();
        var entries = page.Items.OrderBy(e => e.At).ToList();
        Assert.AreEqual("user.created", entries[0].Event);
        Assert.AreEqual((AuditActor.User, TestUsers.IdOf(TestUsers.Admin), TestUsers.Admin), (entries[0].Actor, entries[0].ActorUserId, entries[0].ActorName));
        Assert.AreEqual("POST /api/v1/users", entries[0].Request);
        var update = entries.Single(e => e.Event == "user.updated");
        Assert.IsTrue(update.Changes!.Any(c => c.Field == "DisplayName" && c.Old == name && c.New == "Audited Person"), "the interceptor's field diff");
        Assert.IsFalse(update.Changes!.Any(c => c.Field is "SecurityStamp" or "NormalizedUsername"), "noise is not recorded");
        StringAssert.Contains(update.Summary, "DisplayName");

        var searched = await (await auditor.GetAsync($"/api/v1/audit?search={name}&prefix=user")).ReadAsync<Paged<AuditEntryItem>>();
        Assert.IsTrue(searched.Items.All(e => e.Event.StartsWith("user.", StringComparison.Ordinal)) && searched.TotalCount >= 2);
        var failed = await (await auditor.GetAsync("/api/v1/audit?outcome=Failed&pageSize=5")).ReadAsync<Paged<AuditEntryItem>>();
        Assert.IsTrue(failed.Items.All(e => e.Outcome == AuditOutcome.Failed));
        var events = await (await auditor.GetAsync("/api/v1/audit/events")).ReadAsync<List<AuditEventInfo>>();
        Assert.IsTrue(events.Any(e => e.Event == "user.provisioned" && e.SubjectType == "user"));
    }

    [TestMethod]
    public async Task ASecretChangeIsRecordedWithoutItsValue()
    {
        var name = Unique("secret");
        var created = await (await Admin.PostJsonAsync("/api/v1/users", new { username = name })).ReadAsync<CreatedUser>();
        await (await Admin.PostAsync($"/api/v1/users/{created.User.Id}/reset-password", null)).ReadAsync<TemporaryPassword>();

        await using var db = app.NewDb();
        var entry = await db.AuditLog.SingleAsync(l => l.SubjectId == created.User.Id && l.Event == AuditEvents.PasswordReset);
        Assert.IsFalse(entry.Detail.Contains("pbkdf2", StringComparison.Ordinal));
        Assert.IsTrue(AuditLog.ReadDetail(entry.Detail).Changes!.Any(c => c.Field == "PasswordHash" && c.Kind == "secret" && c.Old is null && c.New is null));
    }

    [TestMethod]
    public void TheCommandLineAddsListsAndResetsUsers()
    {
        var name = Unique("cli");
        Assert.AreEqual(0, Cli.CommandLine.Run(["users", "add", name, "--groups", "members", "--display-name", "From The CLI"]));
        Assert.AreEqual(0, Cli.CommandLine.Run(["users", "list"]));
        Assert.AreEqual(0, Cli.CommandLine.Run(["users", "reset-password", name]));
        Assert.AreEqual(0, Cli.CommandLine.Run(["users", "set-admin", name, "on"]));
        Assert.AreEqual(0, Cli.CommandLine.Run(["users", "unlock", name]));
        Assert.AreEqual(0, Cli.CommandLine.Run(["users", "reset-totp", name]));
        Assert.AreEqual(0, Cli.CommandLine.Run(["groups", "list"]));
        Assert.AreEqual(2, Cli.CommandLine.Run(["users", "add", name + "x", "--groups", "no-such-group"]));
        Assert.AreEqual(1, Cli.CommandLine.Run(["users", "unlock", "nobody-at-all"]));
        Assert.AreEqual(2, Cli.CommandLine.Run(["users", "frobnicate"]));

        using var db = PgpmDbContext.Create(DatabaseSettings.ConnectionString(TestDefaults.Configuration));
        var user = db.Users.Include(u => u.Groups).Single(u => u.Username == name);
        Assert.AreEqual(("From The CLI", true, true, GroupEntity.MembersId), (user.DisplayName, user.IsAdmin, user.MustChangePassword, user.Groups.Single().GroupId));
        var entries = db.AuditLog.Where(l => l.SubjectId == user.Id).ToList();
        Assert.IsTrue(entries.All(e => e.Actor == AuditActor.System && e.ActorRef == "cli"));
        Assert.IsTrue(db.Persons.Any(p => p.UserId == user.Id));
    }

    /// <summary>A new installation: the first admin from Auth:InitialAdmin, or with a generated password (logged once); it must be changed.</summary>
    [TestMethod]
    public async Task AnEmptyDatabaseGetsTheFirstAdmin()
    {
        var connection = TestDefaults.CreateDatabase("pgpm_test_seed");
        using var fresh = new TestApp(new Dictionary<string, string?> { ["Database:ConnectionString"] = connection });
        await using (var db = fresh.NewDb())
        {
            var admin = await db.Users.SingleAsync();
            Assert.AreEqual(("admin", true, true), (admin.Username, admin.IsAdmin, admin.MustChangePassword));
            Assert.IsNull(admin.TemporaryPasswordExpiresAt, "a seeded password does not expire");
            Assert.IsTrue(await db.Persons.AnyAsync(p => p.UserId == admin.Id));
            Assert.AreEqual(2, await db.Groups.CountAsync(), "Members and Viewers");
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.UserCreated && l.ActorRef == "seed"));
        }
        // A second start changes nothing.
        await using (var db = PgpmDbContext.Create(connection))
        {
            var (again, password) = await new UserAccounts(db).SeedAsync(TestDefaults.Configuration);
            Assert.IsNull(again);
            Assert.IsNull(password);
            DatabaseMigrator.RunCustomMigrations(db, NullLogger.Instance);
            Assert.AreEqual(2, await db.Groups.CountAsync());
        }
    }
}

/// <summary>The rights model itself (docs/03 §6.1): levels in order, the highest of the groups, the admin flag, the policy names.</summary>
[TestClass]
public sealed class RightsTests
{
    [TestMethod]
    public void AHigherLevelIncludesTheLowerOnesOfItsArea()
    {
        var rights = new UserRights(false, new Dictionary<Area, Level> { [Area.Mcp] = Level.View, [Area.Projects] = Level.Create });
        Assert.IsTrue(rights.Has(Area.Mcp, Level.Use));
        Assert.IsTrue(rights.Has(Area.Mcp, Level.View));
        Assert.IsFalse(rights.Has(Area.Mcp, Level.Manage));
        Assert.IsTrue(rights.Has(Area.Projects, Level.Create));
        Assert.IsFalse(rights.Has(Area.Projects, Level.Manage), "a level the area doesn't have");
        Assert.IsFalse(rights.Has(Area.Notes, Level.Create));
        Assert.IsTrue(new UserRights(true, new Dictionary<Area, Level>()).Has(Area.System, Level.View), "an admin meets every right");
    }

    [TestMethod]
    public void SeveralGroupsGiveTheHighestLevelPerArea()
    {
        var combined = UserRights.Combine([(Area.Mcp, Level.Use), (Area.Mcp, Level.Manage), (Area.Mcp, Level.View), (Area.Notes, Level.Manage)]);
        Assert.AreEqual(Level.Manage, combined[Area.Mcp]);
        Assert.IsFalse(combined.ContainsKey(Area.Notes), "Notes has no Manage: it gives nothing");
    }

    [TestMethod]
    [DataRow("Projects:Create", true)]
    [DataRow("Mcp:View", true)]
    [DataRow("AuditLog:View", true)]
    [DataRow("Projects:Edit", false)]
    [DataRow("Users:Manage", false)]
    [DataRow("Projects", false)]
    public void RightsParseOnlyAsTheCatalogueHasThem(string value, bool valid) =>
        Assert.AreEqual(valid, RightsCatalog.TryParse(value, out _, out _));

    [TestMethod]
    public async Task ThePolicyProviderBuildsRightAndAdminPoliciesOnce()
    {
        var provider = new RightPolicyProvider(Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Authorization.AuthorizationOptions()));
        var policy = await provider.GetPolicyAsync("right:Notes:Create|Ideas:Create");
        Assert.AreSame(policy, await provider.GetPolicyAsync("right:Notes:Create|Ideas:Create"));
        var requirement = policy!.Requirements.OfType<RightRequirement>().Single();
        Assert.AreEqual("Notes:Create or Ideas:Create", requirement.ToString());
        Assert.IsTrue((await provider.GetPolicyAsync(RightPolicies.Admin))!.Requirements.OfType<AdminRequirement>().Any());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetPolicyAsync("right:Notes:Manage"));
        Assert.IsNull(await provider.GetPolicyAsync("something-else"));
    }

    [TestMethod]
    public void AServicesRefusalNamesTheRight()
    {
        var refusal = Api.ApiException.MissingRight(Area.Configuration, Level.Manage);
        Assert.AreEqual((403, "missing_right", "You need Configuration: Manage."), (refusal.Status, refusal.Code, refusal.Message));
        Assert.AreEqual("Configuration", refusal.Extensions!["area"]);
    }
}

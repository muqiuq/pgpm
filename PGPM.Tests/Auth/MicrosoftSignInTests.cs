using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PGPM.Api.V1.Contracts;
using PGPM.Auth;
using PGPM.Persons;
using PGPM.Tests.Support;

namespace PGPM.Tests.Auth;

/// <summary>
/// Microsoft SSO on PNMTD's cookie session (GGITSV's scenarios, docs/03 §5), through the real OpenID Connect handler
/// against a fake Entra (<see cref="FakeEntra"/>): the first sign-in creates the user, memberships follow the app roles of
/// each sign-in, groups without an app role and the admin flag are never touched, a deactivated user is refused; and,
/// new for PGPM, the default group and the user's person.
/// </summary>
[TestClass]
public sealed class MicrosoftSignInTests
{
    private static readonly FakeEntra entra = new();
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = entra.App();

    [ClassCleanup]
    public static void Stop()
    {
        app.Dispose();
        entra.Dispose();
    }

    private static HttpClient Admin => app.Client(TestUsers.Admin);

    private static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}"[..24] + "@example.com";

    private static async Task<Guid> CreateGroupAsync(string name, string? role = null) =>
        (await (await Admin.PostJsonAsync("/api/v1/groups", new { name = $"{name} {Guid.NewGuid():N}", entraAppRole = role })).ReadAsync<GroupDetail>()).Id;

    /// <summary>The browser's part: start, (Entra signs the user in), come back to /signin-oidc with the code, follow PGPM's redirect.</summary>
    private static async Task<(string Location, Browser Session)> SignInWithMicrosoftAsync(TestApp on, Guid objectId, string userName, string[] roles,
        string returnTo = "/", string name = "Jane Miller")
    {
        var session = new Browser(on);
        using var start = await session.GetAsync($"/api/v1/auth/sso/microsoft?returnTo={Uri.EscapeDataString(returnTo)}");
        Assert.AreEqual(HttpStatusCode.Redirect, start.StatusCode, await start.Content.ReadAsStringAsync());
        var authorize = QueryHelpers.ParseQuery(start.Headers.Location!.Query);

        entra.Nonce = authorize["nonce"].ToString();
        entra.NextClaims.Clear();
        entra.NextClaims["oid"] = objectId.ToString();
        entra.NextClaims["tid"] = FakeEntra.TenantId;
        entra.NextClaims["preferred_username"] = userName;
        entra.NextClaims["name"] = name;
        entra.NextClaims["email"] = userName;
        entra.NextClaims["roles"] = roles;

        var state = Uri.EscapeDataString(authorize["state"].ToString());
        using var callback = await session.GetAsync($"/signin-oidc?code=fake-code&state={state}");
        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode, await callback.Content.ReadAsStringAsync());
        StringAssert.Contains(entra.TokenRequests[^1], "code_verifier=");
        return (callback.Headers.Location!.OriginalString, session);
    }

    private static async Task<MeResponse> MeAsync(Browser session) => await (await session.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();

    private static async Task<UserItem> UserAsync(Guid id) => await (await Admin.GetAsync($"/api/v1/users/{id}")).ReadAsync<UserItem>();

    [TestMethod]
    public async Task WithSsoOffTheOptionsSayNoAndTheStartIs404()
    {
        using var off = new TestApp();
        using var client = off.Client();
        using var start = await client.GetAsync("/api/v1/auth/sso/microsoft");
        Assert.AreEqual(HttpStatusCode.NotFound, start.StatusCode);
        Assert.AreEqual("microsoft_sign_in_disabled", await start.CodeAsync());
        Assert.AreEqual(new SignInOptions(false, true), await (await client.GetAsync("/api/v1/auth/options")).ReadAsync<SignInOptions>());
        Assert.AreEqual(new SignInOptions(true, true), await (await app.Client().GetAsync("/api/v1/auth/options")).ReadAsync<SignInOptions>());
    }

    [TestMethod]
    public async Task TheStartRedirectsToEntraWithCodeFlowAndPkceAndThePublicRedirectUri()
    {
        using var published = entra.App(new Dictionary<string, string?> { ["PublicUrl"] = "https://pgpm.example.com" });
        using var response = await published.Client().GetAsync("/api/v1/auth/sso/microsoft?returnTo=/admin");
        var location = response.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual($"https://login.microsoftonline.com/{FakeEntra.TenantId}/oauth2/v2.0/authorize", location.GetLeftPart(UriPartial.Path));
        Assert.AreEqual(FakeEntra.ClientId, query["client_id"].ToString());
        Assert.AreEqual("code", query["response_type"].ToString());
        // The query, the default of the code flow: the handler leaves the parameter out, as the specification recommends.
        Assert.IsFalse(query.ContainsKey("response_mode"));
        Assert.AreEqual("S256", query["code_challenge_method"].ToString());
        Assert.IsFalse(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        CollectionAssert.AreEqual(new[] { "openid", "profile", "email", "User.Read" }, query["scope"].ToString().Split(' '));
        Assert.AreEqual("https://pgpm.example.com/signin-oidc", query["redirect_uri"].ToString());
        var cookies = response.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("pgpm.oidc.", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(2, cookies.Count, "correlation and nonce");
        Assert.IsTrue(cookies.All(c => c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task TheFirstSignInCreatesTheUserWithTheGroupsOfItsAppRolesAndAMicrosoftSession()
    {
        var role = $"PGPM.PURCHASING.{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        var purchasing = await CreateGroupAsync("Purchasing", role);
        var objectId = Guid.NewGuid();
        var userName = Unique("j.miller");

        var (location, session) = await SignInWithMicrosoftAsync(app, objectId, userName, [role.ToLowerInvariant()], returnTo: "/admin/users?page=2");
        var me = await MeAsync(session);

        Assert.AreEqual("/admin/users?page=2", location);
        Assert.AreEqual((userName, "Jane Miller", userName), (me.User.Username, me.User.DisplayName, me.User.Email));
        Assert.AreEqual((false, true, false), (me.User.HasPassword, me.User.HasMicrosoftAccount, me.User.IsAdmin));
        Assert.AreEqual(AuthMethod.Microsoft, me.Session.AuthMethod);
        Assert.AreEqual(new SessionFlags(false, false, false), me.Flags);
        CollectionAssert.AreEqual(new[] { purchasing }, me.User.GroupIds.ToArray());

        var user = await UserAsync(me.User.Id);
        Assert.AreEqual((objectId, FakeEntra.Tenant), (user.MicrosoftAccount!.ObjectId, user.MicrosoftAccount.TenantId));
        Assert.IsTrue(user.Groups.Single().Entra);
        var audit = await app.AuditAsync(me.User.Id);
        Assert.IsTrue(audit.Any(e => e.StartsWith("user.provisioned:", StringComparison.Ordinal)), string.Join("\n", audit));
        Assert.IsTrue(audit.Any(e => e.StartsWith("membership.added:", StringComparison.Ordinal) && e.Contains(role, StringComparison.Ordinal)));
        Assert.IsTrue(audit.Contains("auth.login_succeeded:Microsoft, first sign-in"));
        await using var db = app.NewDb();
        var provisioned = await db.AuditLog.SingleAsync(l => l.Event == "user.provisioned" && l.SubjectId == me.User.Id);
        Assert.AreEqual(PGPM.Audit.AuditActor.Entra, provisioned.Actor);
    }

    [TestMethod]
    public async Task ASignInReadsTheDirectoryNamesFromGraphWithTheUsersAccessToken()
    {
        entra.GraphRequests.Clear();
        var (_, session) = await SignInWithMicrosoftAsync(app, Guid.NewGuid(), Unique("graph"), []);
        var user = await UserAsync((await MeAsync(session)).User.Id);

        StringAssert.StartsWith(entra.GraphRequests.Single(), "/v1.0/me?$select=userPrincipalName,mail Bearer fake-access-token");
        Assert.AreEqual("j.miller@example.com", user.MicrosoftAccount!.UserName);
        Assert.IsGreaterThan(DateTime.UtcNow.AddMinutes(-5), user.MicrosoftAccount.DirectoryReadAt!.Value);
    }

    [TestMethod]
    public async Task ASignInWhenGraphFailsStillSignsInAndKeepsTheNamesOfTheLastRead()
    {
        var objectId = Guid.NewGuid();
        var userName = Unique("a.muster");
        var (_, first) = await SignInWithMicrosoftAsync(app, objectId, userName, []);
        var id = (await MeAsync(first)).User.Id;

        entra.GraphStatus = HttpStatusCode.Forbidden;
        try
        {
            var (location, second) = await SignInWithMicrosoftAsync(app, objectId, userName, []);
            Assert.AreEqual("/", location);
            Assert.AreEqual(id, (await MeAsync(second)).User.Id);
        }
        finally
        {
            entra.GraphStatus = HttpStatusCode.OK;
        }
        Assert.AreEqual("j.miller@example.com", (await UserAsync(id)).MicrosoftAccount!.UserName);
    }

    [TestMethod]
    public async Task ALaterSignInFollowsTheAppRolesAndLeavesOtherGroupsAndTheAdminFlagAlone()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (purchasingRole, salesRole) = ($"PGPM.PURCHASING.{suffix}", $"PGPM.SALES.{suffix}");
        var purchasing = await CreateGroupAsync("Purchasing", purchasingRole);
        var sales = await CreateGroupAsync("Sales", salesRole);
        var manual = await CreateGroupAsync("Manual");
        var objectId = Guid.NewGuid();
        var userName = Unique("k.brown");
        var (_, first) = await SignInWithMicrosoftAsync(app, objectId, userName, [purchasingRole, salesRole]);
        var userId = (await MeAsync(first)).User.Id;
        await (await Admin.PutJsonAsync($"/api/v1/groups/{manual}/members", new { userIds = new[] { userId } })).ReadAsync<GroupDetail>();
        await (await Admin.PutJsonAsync($"/api/v1/users/{userId}", new { displayName = "Kim Brown", isAdmin = true, isActive = true })).ReadAsync<UserItem>();

        // One role less, and a role that names no group.
        await SignInWithMicrosoftAsync(app, objectId, userName, [salesRole, "PGPM.ADMIN"]);
        var user = await UserAsync(userId);

        CollectionAssert.AreEquivalent(new[] { manual, sales }, user.Groups.Select(g => g.Id).ToArray());
        Assert.IsTrue(user.IsAdmin);
        Assert.AreEqual("Kim Brown", user.DisplayName);
        var audit = await app.AuditAsync(userId);
        Assert.IsTrue(audit.Any(e => e.StartsWith("membership.removed:", StringComparison.Ordinal) && e.Contains(purchasingRole, StringComparison.Ordinal)),
            string.Join("\n", audit));
        _ = purchasing;
    }

    [TestMethod]
    public async Task MembershipsEntraManagesCannotBeEditedByHand()
    {
        var role = $"PGPM.MANAGED.{Guid.NewGuid():N}"[..26];
        var managed = await CreateGroupAsync("Managed", role);
        using (var members = await Admin.PutJsonAsync($"/api/v1/groups/{managed}/members", new { userIds = new[] { TestUsers.IdOf(TestUsers.Plain) } }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, members.StatusCode);
            Assert.AreEqual("membership_managed_by_entra", await members.CodeAsync());
        }
        using var user = await Admin.PutJsonAsync($"/api/v1/users/{TestUsers.IdOf(TestUsers.Plain)}",
            new { displayName = "Test plain", isActive = true, groupIds = new[] { managed } });
        Assert.AreEqual("membership_managed_by_entra", await user.CodeAsync());
    }

    [TestMethod]
    public async Task AMicrosoftSessionIsNotHeldUpByTheTotpPolicyAndHasNoPasswordToChange()
    {
        using var required = entra.App(new Dictionary<string, string?> { ["Auth:TotpPolicy"] = "Required" });
        var (_, session) = await SignInWithMicrosoftAsync(required, Guid.NewGuid(), Unique("otp.free"), []);
        var me = await MeAsync(session);

        Assert.AreEqual(new SessionFlags(false, false, false), me.Flags);
        Assert.AreEqual(HttpStatusCode.OK, await session.StatusAsync(HttpMethod.Get, "/api/v1/rights"));
        using var change = await session.PostAsync("/api/v1/account/password", new { currentPassword = "anything-at-all", newPassword = "a-new-password-here" });
        Assert.AreEqual(HttpStatusCode.Conflict, change.StatusCode);
        Assert.AreEqual("password_not_set", await change.CodeAsync());
    }

    [TestMethod]
    public async Task ADeactivatedUserIsRefusedEvenThoughEntraLetThemIn()
    {
        var objectId = Guid.NewGuid();
        var userName = Unique("left");
        var (_, first) = await SignInWithMicrosoftAsync(app, objectId, userName, []);
        var userId = (await MeAsync(first)).User.Id;
        await (await Admin.PutJsonAsync($"/api/v1/users/{userId}", new { displayName = "Left", isActive = false })).ReadAsync<UserItem>();

        var (location, session) = await SignInWithMicrosoftAsync(app, objectId, userName, []);

        Assert.AreEqual("/sign-in?ssoError=deactivated", location);
        Assert.AreEqual(HttpStatusCode.Unauthorized, await session.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        Assert.IsTrue((await app.AuditAsync(userId)).Contains("auth.login_refused:Microsoft sign-in: the account is deactivated."));
    }

    [TestMethod]
    public async Task AnUnlinkedAccountWhoseNameBelongsToALocalUserIsRefusedNotLinkedByName()
    {
        var userName = Unique("p.local");
        var local = await (await Admin.PostJsonAsync("/api/v1/users", new { username = userName })).ReadAsync<CreatedUser>();

        var (location, _) = await SignInWithMicrosoftAsync(app, Guid.NewGuid(), userName, []);

        Assert.AreEqual("/sign-in?ssoError=account-conflict", location);
        Assert.IsNull((await UserAsync(local.User.Id)).MicrosoftAccount);
        await using var db = app.NewDb();
        Assert.AreEqual(1, await db.Users.CountAsync(u => u.NormalizedUsername == userName.ToLowerInvariant()));
    }

    [TestMethod]
    public async Task ALinkedAccountSignsInAsTheLinkedUserAndCanBeUnlinked()
    {
        var local = await (await Admin.PostJsonAsync("/api/v1/users", new { username = Unique("p.linked") })).ReadAsync<CreatedUser>();
        var objectId = Guid.NewGuid();
        var linked = await (await Admin.PutJsonAsync($"/api/v1/users/{local.User.Id}/microsoft-account", new { objectId })).ReadAsync<UserItem>();
        Assert.AreEqual((objectId, FakeEntra.Tenant), (linked.MicrosoftAccount!.ObjectId, linked.MicrosoftAccount.TenantId), "the tenant from Entra:TenantId");

        var (_, session) = await SignInWithMicrosoftAsync(app, objectId, Unique("other.name"), []);
        var me = await MeAsync(session);
        Assert.AreEqual(local.User.Id, me.User.Id);
        Assert.AreEqual(new SessionFlags(false, false, false), me.Flags, "a Microsoft session skips the temporary password");

        using (var taken = await Admin.PutJsonAsync($"/api/v1/users/{TestUsers.IdOf(TestUsers.Plain)}/microsoft-account", new { objectId }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, taken.StatusCode);
        }
        var unlinked = await (await Admin.DeleteAsync($"/api/v1/users/{local.User.Id}/microsoft-account")).ReadAsync<UserItem>();
        Assert.IsNull(unlinked.MicrosoftAccount);
        Assert.AreEqual(HttpStatusCode.Unauthorized, await session.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"), "its sessions end");
        var audit = await app.AuditAsync(local.User.Id);
        Assert.IsTrue(audit.Any(e => e.StartsWith("user.microsoft_linked:", StringComparison.Ordinal)));
        Assert.IsTrue(audit.Any(e => e.StartsWith("user.microsoft_unlinked:", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("https://evil.example/")]
    [DataRow("//evil.example/")]
    [DataRow("/\\evil.example/")]
    public async Task AReturnPathToAnotherSiteLeadsHome(string returnTo)
    {
        var (location, _) = await SignInWithMicrosoftAsync(app, Guid.NewGuid(), Unique("safe"), [], returnTo);
        Assert.AreEqual("/", location);
    }

    [TestMethod]
    public async Task ACancelledSignInLeadsToTheSignInPageWithTheReason()
    {
        var browser = new Browser(app);
        using var start = await browser.GetAsync("/api/v1/auth/sso/microsoft");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();

        using var callback = await browser.GetAsync($"/signin-oidc?error=access_denied&state={Uri.EscapeDataString(state)}");

        Assert.AreEqual("/sign-in?ssoError=cancelled", callback.Headers.Location!.OriginalString);
    }

    /// <summary>New for PGPM (docs/03 §5.3 step 5): a user without any group after the app roles goes into Entra:DefaultGroupId.</summary>
    [TestMethod]
    public async Task AUserWithoutAGroupGoesIntoTheDefaultGroup()
    {
        var role = $"PGPM.DEFAULTTEST.{Guid.NewGuid():N}"[..30];
        var roleGroup = await CreateGroupAsync("Role", role);
        using var withDefault = entra.App(new Dictionary<string, string?> { ["Entra:DefaultGroupId"] = GroupEntity.ViewersId.ToString() });

        var (_, plain) = await SignInWithMicrosoftAsync(withDefault, Guid.NewGuid(), Unique("default"), []);
        var (_, withRole) = await SignInWithMicrosoftAsync(withDefault, Guid.NewGuid(), Unique("role"), [role]);

        CollectionAssert.AreEqual(new[] { GroupEntity.ViewersId }, (await MeAsync(plain)).User.GroupIds.ToArray());
        CollectionAssert.AreEqual(new[] { roleGroup }, (await MeAsync(withRole)).User.GroupIds.ToArray(), "the app role's group is enough");
        var audit = await withDefault.AuditAsync((await MeAsync(plain)).User.Id);
        Assert.IsTrue(audit.Any(e => e.StartsWith("membership.added:", StringComparison.Ordinal) && e.Contains("Entra:DefaultGroupId", StringComparison.Ordinal)));
    }

    /// <summary>New for PGPM (docs/03 §5.3 step 6): the user's person is the directory person of the account, or one made from the claims.</summary>
    [TestMethod]
    public async Task TheSignInLinksTheDirectoryPersonOrCreatesOne()
    {
        var known = Guid.NewGuid();
        Guid directoryPerson;
        await using (var db = app.NewDb())
        {
            var person = new PersonEntity
            {
                Id = Lib.Helper.Ids.New(), Source = PersonSource.Microsoft, EntraTenantId = FakeEntra.Tenant, EntraObjectId = known,
                DisplayName = "Directory Person", Email = "directory@example.com", Department = "Innovation",
            };
            db.Persons.Add(person);
            await db.SaveChangesAsync();
            directoryPerson = person.Id;
        }

        var (_, linked) = await SignInWithMicrosoftAsync(app, known, Unique("dir"), [], name: "Claims Name");
        var (_, created) = await SignInWithMicrosoftAsync(app, Guid.NewGuid(), Unique("new"), [], name: "New Person");

        var linkedMe = (await MeAsync(linked)).User;
        Assert.AreEqual(directoryPerson, linkedMe.PersonId);
        var createdMe = (await MeAsync(created)).User;
        await using (var db = app.NewDb())
        {
            var person = await db.Persons.SingleAsync(p => p.Id == directoryPerson);
            Assert.AreEqual((linkedMe.Id, "Directory Person", "Innovation"), (person.UserId!.Value, person.DisplayName, person.Department),
                "the directory's fields stay the directory's");
            var made = await db.Persons.SingleAsync(p => p.UserId == createdMe.Id);
            Assert.AreEqual((made.Id, PersonSource.Microsoft, FakeEntra.Tenant, "New Person"), (createdMe.PersonId!.Value, made.Source, made.EntraTenantId!.Value,
                made.DisplayName));
            Assert.AreEqual(1, await db.Persons.CountAsync(p => p.UserId == linkedMe.Id), "exactly one person per user");
        }
    }

    /// <summary>A local user linked to Microsoft keeps their (manual) person: it becomes the account's Microsoft person.</summary>
    [TestMethod]
    public async Task ALinkedLocalUsersPersonBecomesTheMicrosoftPerson()
    {
        var local = await (await Admin.PostJsonAsync("/api/v1/users", new { username = Unique("keep"), displayName = "Kept Person" })).ReadAsync<CreatedUser>();
        var objectId = Guid.NewGuid();
        await (await Admin.PutJsonAsync($"/api/v1/users/{local.User.Id}/microsoft-account", new { objectId })).ReadAsync<UserItem>();

        var (_, session) = await SignInWithMicrosoftAsync(app, objectId, Unique("kept"), []);

        var me = (await MeAsync(session)).User;
        Assert.AreEqual(local.User.PersonId, me.PersonId);
        await using var db = app.NewDb();
        var person = await db.Persons.SingleAsync(p => p.Id == me.PersonId);
        Assert.AreEqual((PersonSource.Microsoft, objectId), (person.Source, person.EntraObjectId!.Value));
    }

    [TestMethod]
    public async Task WithoutAutoProvisionAnUnknownAccountIsRefused()
    {
        using var closed = entra.App(new Dictionary<string, string?> { ["Entra:AutoProvision"] = "false" });
        var userName = Unique("stranger");
        var (location, _) = await SignInWithMicrosoftAsync(closed, Guid.NewGuid(), userName, []);
        Assert.AreEqual("/sign-in?ssoError=no-account", location);
        await using var db = closed.NewDb();
        Assert.IsFalse(await db.Users.AnyAsync(u => u.NormalizedUsername == userName.ToLowerInvariant()));
    }
}

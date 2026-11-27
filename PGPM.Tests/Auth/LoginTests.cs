using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Lib.Helper;
using PGPM.Lib.Security;

namespace PGPM.Tests.Auth;

/// <summary>Sign-in, sessions, lockout, the rate limit, the gates and antiforgery with the real cookie scheme (docs/03 §4, from PNMTD).</summary>
[TestClass]
public sealed class LoginTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>A user with a password of its own (not temporary), in Members unless told otherwise.</summary>
    internal static async Task<(Guid Id, string Name, string Password)> NewUserAsync(TestApp on, bool admin = false, Guid[]? groups = null,
        bool temporary = false)
    {
        var name = $"u{Guid.NewGuid():N}"[..20];
        var password = $"pw-{Guid.NewGuid():N}";
        await using var db = on.NewDb();
        var (user, generated) = await new UserAccounts(db).CreateAsync(name, null, null, admin, groups ?? [GroupEntity.MembersId], temporary ? null : password,
            AuditWho.Cli);
        return (user.Id, name, generated ?? password);
    }

    [TestMethod]
    public async Task LoginSetsAStrictHttpOnlySessionCookieAndMeKnowsTheUser()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);

        var login = await browser.LoginAsync(name.ToUpperInvariant(), password);

        Assert.AreEqual("done", login.Next);
        Assert.AreEqual(id, login.Me!.User.Id);
        Assert.AreEqual(new SessionFlags(false, false, true), login.Me.Flags);
        var session = browser.LastSetCookies.Single(c => c.StartsWith("pgpm.auth=", StringComparison.Ordinal)).ToLowerInvariant();
        StringAssert.Contains(session, "httponly");
        StringAssert.Contains(session, "samesite=strict");
        StringAssert.Contains(session, "path=/;");
        StringAssert.Contains(session, "expires=");
        var csrf = browser.LastSetCookies.Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.IsFalse(csrf.Contains("httponly"), "the UI reads it");

        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.AreEqual(name, me.User.Username);
        Assert.IsFalse(me.User.IsAdmin);
        CollectionAssert.AreEqual(new[] { GroupEntity.MembersId }, me.User.GroupIds.ToArray());
        Assert.AreEqual(Level.Create, me.User.Rights[Area.Projects]);
        Assert.AreEqual(Level.Use, me.User.Rights[Area.Mcp]);
        Assert.IsFalse(me.User.Rights.ContainsKey(Area.AuditLog));
        Assert.AreEqual(AuthMethod.Password, me.Session.AuthMethod);
        Assert.IsNotNull(me.Session.SignedInUntil);
        await using (var db = app.NewDb())
        {
            var person = await db.Persons.SingleAsync(p => p.UserId == id);
            Assert.AreEqual((person.Id, Persons.PersonSource.Manual, name), (me.User.PersonId!.Value, person.Source, person.DisplayName));
        }
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.login_succeeded:password");
    }

    [TestMethod]
    public async Task MeSignedOutIs401()
    {
        using var response = await new Browser(app).GetAsync("/api/v1/auth/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("unauthorized", await response.CodeAsync());
    }

    /// <summary>Unknown, wrong and inactive look the same to the client; only the audit log tells them apart.</summary>
    [TestMethod]
    public async Task FailedLoginsAllLookTheSame()
    {
        var (id, name, password) = await NewUserAsync(app);
        var (inactiveId, inactiveName, inactivePassword) = await NewUserAsync(app);
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == inactiveId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        }

        var bodies = new List<string>();
        foreach (var (user, pw) in new[] { ("nobody-here", password), (name, "wrong-password"), (inactiveName, inactivePassword), ("", "") })
        {
            using var response = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = user, password = pw });
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, user);
            Assert.IsFalse(response.Headers.Contains("Set-Cookie"), user);
            var problem = await response.ProblemAsync();
            bodies.Add($"{problem.GetProperty("code").GetString()} {problem.GetProperty("detail").GetString()}");
        }
        Assert.AreEqual(1, bodies.Distinct().Count(), string.Join(" | ", bodies));
        Assert.AreEqual("invalid_credentials Wrong username or password.", bodies[0]);
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.login_failed:wrong password");
        CollectionAssert.Contains(await app.AuditAsync(inactiveId), "auth.login_failed:inactive");
        await using (var check = app.NewDb())
        {
            var unknown = await check.AuditLog.FromSql($"SELECT * FROM audit_log WHERE detail->>'username' = 'nobody-here'").ToListAsync();
            Assert.IsTrue(unknown.Any(e => e.ActorUserId is null && AuditLog.ReadDetail(e.Detail).Summary == "unknown user"));
        }
    }

    [TestMethod]
    public async Task FiveWrongPasswordsLockTheAccountUntilAnAdminUnlocks()
    {
        var (id, name, password) = await NewUserAsync(app);
        for (var i = 0; i < AuthService.FailuresBeforeLockout; i++)
        {
            using var wrong = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password = "wrong-password" });
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Locked: even the right password is refused, with the time to wait.
        using var locked = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.AreEqual("locked_out", await locked.CodeAsync());
        Assert.IsTrue(locked.Headers.RetryAfter?.Delta is { TotalSeconds: > 290 and <= 300 }, locked.Headers.RetryAfter?.ToString());
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.locked_out:5 minutes");
        var listed = await (await app.Client(TestUsers.Admin).GetAsync($"/api/v1/users/{id}")).ReadAsync<UserItem>();
        Assert.IsNotNull(listed.LockedUntil);

        Assert.AreEqual(HttpStatusCode.NoContent, (await app.Client(TestUsers.Admin).PostAsync($"/api/v1/users/{id}/unlock", null)).StatusCode);
        Assert.AreEqual("done", (await new Browser(app).LoginAsync(name, password)).Next);
    }

    [TestMethod]
    public void EachLockoutDoublesTheNextUpToAnHour() =>
        CollectionAssert.AreEqual(new[] { 5, 10, 20, 40, 60, 60 }, Enumerable.Range(0, 6).Select(n => (int)AuthService.LockoutFor(n).TotalMinutes).ToArray());

    [TestMethod]
    public async Task TheLoginIsRateLimitedPerAddress()
    {
        using var limited = new TestApp(new Dictionary<string, string?> { ["Auth:LoginRateLimit:PermitLimit"] = "3" });
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await new Browser(limited).PostAsync("/api/v1/auth/login", new { username = "nobody", password = "wrong-password" });
            statuses.Add(last.StatusCode);
        }
        CollectionAssert.AreEqual(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests },
            statuses);
        Assert.AreEqual("rate_limited", await last!.CodeAsync());
        Assert.IsNotNull(last!.Headers.RetryAfter);
        // Only the login: the rest of the API is not limited.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await new Browser(limited).GetAsync("/api/v1/rights")).StatusCode);
    }

    [TestMethod]
    public async Task CookieWritesNeedTheAntiforgeryToken()
    {
        var (_, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        browser.SendCsrfToken = false;
        using var without = await browser.PostAsync("/api/v1/account/sessions/revoke");
        Assert.AreEqual(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.AreEqual("csrf_token_invalid", await without.CodeAsync());

        browser.SendCsrfToken = true;
        Assert.AreEqual(HttpStatusCode.NoContent, await browser.StatusAsync(HttpMethod.Post, "/api/v1/account/sessions/revoke"));
        // Reads need no token.
        browser.SendCsrfToken = false;
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"));
    }

    [TestMethod]
    public async Task ATemporaryPasswordMustBeChangedBeforeAnythingElse()
    {
        var created = await (await app.Client(TestUsers.Admin).PostJsonAsync("/api/v1/users",
            new { username = $"t{Guid.NewGuid():N}"[..16], groupIds = new[] { GroupEntity.ViewersId } })).ReadAsync<CreatedUser>();
        Assert.AreEqual(20, created.TemporaryPassword.Password.Length);
        var browser = new Browser(app);

        var login = await browser.LoginAsync(created.User.Username, created.TemporaryPassword.Password);
        Assert.IsTrue(login.Me!.Flags.MustChangePassword);

        using var blocked = await browser.GetAsync("/api/v1/rights");
        Assert.AreEqual(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.AreEqual("must_change_password", await blocked.CodeAsync());
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/info"));

        using var wrongCurrent = await browser.PostAsync("/api/v1/account/password", new { currentPassword = "nope", newPassword = "a-new-password-1" });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.IsTrue((await wrongCurrent.ProblemAsync()).GetProperty("errors").TryGetProperty("currentPassword", out _));
        using var tooShort = await browser.PostAsync("/api/v1/account/password", new { currentPassword = created.TemporaryPassword.Password, newPassword = "short" });
        Assert.IsTrue((await tooShort.ProblemAsync()).GetProperty("errors").TryGetProperty("newPassword", out _));

        Assert.AreEqual(HttpStatusCode.NoContent, await browser.StatusAsync(HttpMethod.Post, "/api/v1/account/password",
            new { currentPassword = created.TemporaryPassword.Password, newPassword = "a-new-password-1" }));
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"), "the renewed cookie has no must-change claim");
        Assert.IsFalse((await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>()).Flags.MustChangePassword);
        Assert.AreEqual("done", (await new Browser(app).LoginAsync(created.User.Username, "a-new-password-1")).Next);
    }

    [TestMethod]
    public async Task ChangingThePasswordEndsTheOtherSessions()
    {
        var (_, name, password) = await NewUserAsync(app);
        var here = new Browser(app);
        var elsewhere = new Browser(app);
        await here.LoginAsync(name, password);
        await elsewhere.LoginAsync(name, password);

        Assert.AreEqual(HttpStatusCode.NoContent,
            await here.StatusAsync(HttpMethod.Post, "/api/v1/account/password", new { currentPassword = password, newPassword = "another-password-1" }));

        Assert.AreEqual(HttpStatusCode.OK, await here.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await elsewhere.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        Assert.IsFalse(elsewhere.Cookies.ContainsKey("pgpm.auth"), "the rejected session's cookie is cleared");
    }

    [TestMethod]
    public async Task AStaleCookieOnTheFilesOfTheUiIsLeftAlone()
    {
        // The service worker precaches the shell with the cookie the page has: an answer to a request that left before a
        // password change must not delete the fresh cookie. Only the API checks the session (and clears a rejected one).
        var (_, name, password) = await NewUserAsync(app);
        var here = new Browser(app);
        var elsewhere = new Browser(app);
        await here.LoginAsync(name, password);
        await elsewhere.LoginAsync(name, password);
        Assert.AreEqual(HttpStatusCode.NoContent,
            await here.StatusAsync(HttpMethod.Post, "/api/v1/account/password", new { currentPassword = password, newPassword = "another-password-1" }));

        foreach (var file in new[] { "/sw.js", "/index.html", "/assets/index-3f9a.js", "/manifest.webmanifest" })
        {
            Assert.AreEqual(HttpStatusCode.OK, await elsewhere.StatusAsync(HttpMethod.Get, file), file);
            Assert.IsEmpty(elsewhere.LastSetCookies, $"{file}: {string.Join(" | ", elsewhere.LastSetCookies)}");
        }
        Assert.IsTrue(elsewhere.Cookies.ContainsKey("pgpm.auth"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await elsewhere.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        Assert.IsFalse(elsewhere.Cookies.ContainsKey("pgpm.auth"), "the API clears the rejected session's cookie");
    }

    [TestMethod]
    public async Task SignOutEverywhereKeepsThisSession()
    {
        var (_, name, password) = await NewUserAsync(app);
        var here = new Browser(app);
        var elsewhere = new Browser(app);
        await here.LoginAsync(name, password);
        await elsewhere.LoginAsync(name, password);

        Assert.AreEqual(HttpStatusCode.NoContent, await here.StatusAsync(HttpMethod.Post, "/api/v1/account/sessions/revoke"));

        Assert.AreEqual(HttpStatusCode.OK, await here.StatusAsync(HttpMethod.Get, "/api/v1/rights"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await elsewhere.StatusAsync(HttpMethod.Get, "/api/v1/rights"));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task AnAdminsResetEndsTheSessionsAndTheTemporaryPasswordExpires()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        var reset = await (await app.Client(TestUsers.Admin).PostAsync($"/api/v1/users/{id}/reset-password", null)).ReadAsync<TemporaryPassword>();

        Assert.AreEqual(HttpStatusCode.Unauthorized, await browser.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        Assert.IsLessThan(TimeSpan.FromMinutes(1), (reset.ExpiresAt - Clock.UtcNow - UserAccounts.TemporaryPasswordLifetime).Duration());
        Assert.AreEqual(HttpStatusCode.Unauthorized, await new Browser(app).StatusAsync(HttpMethod.Post, "/api/v1/auth/login", new { username = name, password }));
        try
        {
            Clock.Provider = new FakeTimeProvider(DateTimeOffset.UtcNow + TimeSpan.FromDays(8));
            Assert.AreEqual(HttpStatusCode.Unauthorized,
                await new Browser(app).StatusAsync(HttpMethod.Post, "/api/v1/auth/login", new { username = name, password = reset.Password }));
        }
        finally
        {
            Clock.Provider = TimeProvider.System;
        }
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.login_failed:temporary password expired");
        Assert.IsTrue((await new Browser(app).LoginAsync(name, reset.Password)).Me!.Flags.MustChangePassword);
    }

    /// <summary>The admin flag is a change of the stamp: the user's sessions end.</summary>
    [TestMethod]
    public async Task TheAdminFlagEndsTheSession()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        var updated = await (await app.Client(TestUsers.Admin).PutJsonAsync($"/api/v1/users/{id}", new { displayName = name, isAdmin = true, isActive = true }))
            .ReadAsync<UserItem>();

        Assert.IsTrue(updated.IsAdmin);
        Assert.AreEqual(HttpStatusCode.Unauthorized, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"));
        Assert.IsTrue((await new Browser(app).LoginAsync(name, password)).Me!.User.IsAdmin);
        CollectionAssert.Contains(await app.AuditAsync(id), $"user.admin_changed:{name} is an admin now");
    }

    /// <summary>Groups need no sign-out: a session has its new rights from its next request (SessionCache.Forget).</summary>
    [TestMethod]
    public async Task AGroupChangeAppliesToTheOpenSession()
    {
        var (id, name, password) = await NewUserAsync(app, groups: []);
        var browser = new Browser(app);
        var login = await browser.LoginAsync(name, password);
        Assert.AreEqual(0, login.Me!.User.Rights.Count);
        using (var refused = await browser.GetAsync("/api/v1/audit"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            var problem = await refused.ProblemAsync();
            Assert.AreEqual(("missing_right", "AuditLog", "View"),
                (problem.GetProperty("code").GetString(), problem.GetProperty("area").GetString(), problem.GetProperty("level").GetString()));
        }
        var auditors = await (await app.Client(TestUsers.Admin).PostJsonAsync("/api/v1/groups",
            new { name = $"Auditors {Guid.NewGuid():N}", rights = new Dictionary<string, string> { ["AuditLog"] = "View" } })).ReadAsync<GroupDetail>();

        await (await app.Client(TestUsers.Admin).PutJsonAsync($"/api/v1/users/{id}", new { displayName = name, isActive = true, groupIds = new[] { auditors.Id } }))
            .ReadAsync<UserItem>();

        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/audit"), "the open session has the new right");
        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.AreEqual(Level.View, me.User.Rights[Area.AuditLog]);
        CollectionAssert.AreEqual(new[] { auditors.Id }, me.User.GroupIds.ToArray());
        CollectionAssert.Contains(await app.AuditAsync(id), $"user.groups_changed:{name}: none → {auditors.Name}");
    }

    [TestMethod]
    public async Task AnOldHashSignsInAndIsUpgraded()
    {
        var (id, name, _) = await NewUserAsync(app);
        await using (var db = app.NewDb())
        {
            var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("an older password", salt, 10_000,
                System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
            var legacy = $"pbkdf2-sha256$10000${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, legacy));
        }

        Assert.AreEqual("done", (await new Browser(app).LoginAsync(name, "an older password")).Next);

        await using (var db = app.NewDb())
        {
            var stored = await db.Users.Where(u => u.Id == id).Select(u => u.PasswordHash).SingleAsync();
            StringAssert.StartsWith(stored, $"pbkdf2-sha256${PasswordHasher.Iterations}$");
            Assert.IsTrue(PasswordHasher.Verify("an older password", stored));
        }
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.login_succeeded:password hash upgraded");
    }

    [TestMethod]
    public async Task LogoutEndsTheSession()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        Assert.AreEqual(HttpStatusCode.NoContent, await browser.StatusAsync(HttpMethod.Post, "/api/v1/auth/logout"));

        Assert.IsFalse(browser.Cookies.ContainsKey("pgpm.auth"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await browser.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.logout:");
    }

    [TestMethod]
    public async Task ADeactivatedUsersSessionEnds()
    {
        var (id, name, password) = await NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        await (await app.Client(TestUsers.Admin).PutJsonAsync($"/api/v1/users/{id}", new { displayName = name, isActive = false })).ReadAsync<UserItem>();

        Assert.AreEqual(HttpStatusCode.Unauthorized, await browser.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));
        CollectionAssert.Contains(await app.AuditAsync(id), $"user.updated:{name} deactivated");
    }

    [TestMethod]
    public async Task AMicrosoftOnlyAccountHasNoPasswordToSignInWithOrChange()
    {
        var name = $"sso{Guid.NewGuid():N}"[..20];
        await using (var db = app.NewDb())
        {
            db.Users.Add(new UserEntity
            {
                Id = Ids.New(), Username = name, NormalizedUsername = name, DisplayName = name, SecurityStamp = UserAccounts.NewStamp(),
                CreatedAt = Clock.UtcNow, MicrosoftTenantId = Guid.NewGuid(), MicrosoftObjectId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
        using var login = await new Browser(app).PostAsync("/api/v1/auth/login", new { username = name, password = "anything-at-all" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.AreEqual("invalid_credentials", await login.CodeAsync());
    }

    /// <summary>Auth:LocalLoginNetworks (GGITSV): the password only from the networks named; /auth/options says so.</summary>
    [TestMethod]
    public async Task TheLocalLoginCanBeLimitedToNetworks()
    {
        using var limited = new TestApp(new Dictionary<string, string?> { ["Auth:LocalLoginNetworks"] = "10.0.0.0/8, 192.168.1.20" });
        var (_, name, password) = await NewUserAsync(limited);

        // The test server's requests come from no address: not in the networks.
        using var refused = await new Browser(limited).PostAsync("/api/v1/auth/login", new { username = name, password });
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual("local_login_not_allowed", await refused.CodeAsync());
        var options = await (await new Browser(limited).GetAsync("/api/v1/auth/options")).ReadAsync<SignInOptions>();
        Assert.AreEqual(new SignInOptions(false, false), options);

        var rule = LocalLoginRule.Parse("10.0.0.0/8, 192.168.1.20, 2001:db8::/32");
        Assert.IsTrue(rule.IsAllowed(IPAddress.Parse("10.1.2.3")));
        Assert.IsTrue(rule.IsAllowed(IPAddress.Parse("::ffff:10.1.2.3")), "an IPv4-mapped address");
        Assert.IsTrue(rule.IsAllowed(IPAddress.Parse("192.168.1.20")));
        Assert.IsFalse(rule.IsAllowed(IPAddress.Parse("192.168.1.21")));
        Assert.IsTrue(rule.IsAllowed(IPAddress.Parse("2001:db8::1")));
        Assert.IsFalse(rule.IsAllowed(null));
        Assert.IsTrue(LocalLoginRule.Parse("").IsAllowed(null));
        Assert.IsTrue(LocalLoginRule.Parse("all").IsAllowed(IPAddress.Loopback));
        Assert.IsFalse(LocalLoginRule.Parse("none").IsAllowed(IPAddress.Loopback));
        Assert.ThrowsExactly<InvalidOperationException>(() => LocalLoginRule.Parse("10.0.0.0/8, intranet"));
    }

    /// <summary>A configuration where nobody could sign in stops the start (GGITSV), as does SSO without its registration.</summary>
    [TestMethod]
    public void ASettingWhereNobodyCanSignInStopsTheStart()
    {
        using var nobody = new TestApp(new Dictionary<string, string?> { ["Auth:LocalLoginNetworks"] = "none" });
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => nobody.Client());
        StringAssert.Contains(error.Message, "nobody could sign in");

        using var incomplete = new TestApp(new Dictionary<string, string?> { ["Entra:Enabled"] = "true", ["Entra:TenantId"] = Guid.NewGuid().ToString() });
        error = Assert.ThrowsExactly<InvalidOperationException>(() => incomplete.Client());
        StringAssert.Contains(error.Message, "Entra:ClientId");
    }

    [TestMethod]
    public async Task TheSeededTestAdminSignsIn()
    {
        var login = await new Browser(app).LoginAsync(TestUsers.Admin, TestUsers.Password);
        Assert.IsTrue(login.Me!.User.IsAdmin);
    }
}

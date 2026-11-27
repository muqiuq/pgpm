using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Lib.Helper;
using PGPM.Lib.Security;

namespace PGPM.Tests.Auth;

/// <summary>The codes themselves (docs/03 §4.4): RFC 6238's vectors, Base32, drift and replays.</summary>
[TestClass]
public sealed class TotpServiceTests
{
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>RFC 6238 Appendix B, SHA1 (8 digits); the 6-digit code is its last six.</summary>
    [TestMethod]
    [DataRow(59L, "94287082")]
    [DataRow(1111111109L, "07081804")]
    [DataRow(1234567890L, "89005924")]
    [DataRow(20000000000L, "65353130")]
    public void TheRfc6238VectorsMatch(long unixSeconds, string expected)
    {
        Assert.AreEqual(expected, TotpService.ComputeCodeForStep(RfcKey, unixSeconds / 30, 8));
        var secret = Base32.Encode(RfcKey);
        Assert.AreEqual("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", secret);
        Assert.AreEqual(expected[^6..], TotpService.ComputeCode(secret, DateTime.UnixEpoch.AddSeconds(unixSeconds)));
    }

    [TestMethod]
    public void Base32RoundTripsAndRefusesOtherCharacters()
    {
        for (var length = 0; length <= 40; length++)
        {
            var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
            CollectionAssert.AreEqual(data, Base32.Decode(Base32.Encode(data)), $"{length} bytes");
        }
        CollectionAssert.AreEqual(Base32.Decode("MZXW6YTBOI"), Base32.Decode(" mzxw 6ytb oi "), "spaces, as the UI groups it");
        Assert.ThrowsExactly<FormatException>(() => Base32.Decode("MZ1W"));
        Assert.AreEqual(32, TotpService.GenerateSecret().Length);
    }

    [TestMethod]
    public void ACodeIsValidOneStepEitherWayAndOnlyOnce()
    {
        var secret = TotpService.GenerateSecret();
        var now = new DateTime(2026, 11, 11, 12, 0, 15, DateTimeKind.Utc);
        var step = TotpService.StepAt(now);
        string At(int seconds) => TotpService.ComputeCode(secret, now.AddSeconds(seconds));

        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(0), now, null, out var matched));
        Assert.AreEqual(step, matched);
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(-30), now, null, out _));
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(30), now, null, out _));
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check(secret, At(-60), now, null, out _));
        Assert.AreEqual(TotpCheck.Reused, TotpService.Check(secret, At(0), now, step, out _));
        Assert.AreEqual(TotpCheck.Valid, TotpService.Check(secret, At(30), now, step, out _), "a later step after the one used");
        Assert.AreEqual(TotpCheck.Invalid, TotpService.Check(secret, "12a456", now, null, out _));
    }

    [TestMethod]
    public void TheEnrollmentUriNamesPgpm() =>
        Assert.AreEqual("otpauth://totp/PGPM:ana%40example.com?secret=ABC&issuer=PGPM&algorithm=SHA1&digits=6&period=30",
            TotpService.BuildOtpAuthUri("PGPM", "ana@example.com", "ABC"));
}

/// <summary>
/// 2FA with the real cookie scheme (PNMTD): setup, the code step, replays, lockout, turning it off, the admin's reset.
/// Time is frozen and moved by hand: each new code needs a new 30-second step. The frozen clock is everyone's, so these
/// run on their own.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TwoFactorLoginTests
{
    private static TestApp app = null!;
    private static FakeTimeProvider time = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestInitialize]
    public void Freeze()
    {
        time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Clock.Provider = time;
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    internal static string Code(string secret) => TotpService.ComputeCode(secret, Clock.UtcNow);

    /// <summary>A 6-digit code that is not the current one (nor one step either way).</summary>
    internal static string WrongCode(string secret)
    {
        var near = new[] { -30, 0, 30 }.Select(s => TotpService.ComputeCode(secret, Clock.UtcNow.AddSeconds(s))).ToHashSet();
        return Enumerable.Range(0, 1000).Select(i => (123456 + i).ToString(CultureInfo.InvariantCulture)).First(c => !near.Contains(c));
    }

    /// <summary>Signed in on <paramref name="browser"/>, with 2FA set up through the API; the clock is a step on.</summary>
    internal static async Task<string> EnrollAsync(Browser browser)
    {
        var setup = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        using var confirm = await browser.PostAsync("/api/v1/account/totp/confirm", new { code = Code(setup.Secret) });
        Assert.AreEqual(HttpStatusCode.NoContent, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
        ((FakeTimeProvider)Clock.Provider).Advance(TimeSpan.FromSeconds(30));
        return setup.Secret;
    }

    private static async Task<(Guid Id, string Name, string Password, string Secret)> EnrolledUserAsync()
    {
        var (id, name, password) = await LoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        return (id, name, password, await EnrollAsync(browser));
    }

    [TestMethod]
    public async Task SettingUpNeedsACodeFromTheNewSecretAndEndsTheOtherSessions()
    {
        var (id, name, password) = await LoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        var other = new Browser(app);
        await browser.LoginAsync(name, password);
        await other.LoginAsync(name, password);

        var setup = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        Assert.AreEqual(32, setup.Secret.Length);
        StringAssert.StartsWith(setup.OtpauthUri, $"otpauth://totp/PGPM:{name}?secret={setup.Secret}&issuer=PGPM");
        await using (var db = app.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            Assert.IsFalse(user.IsTotpEnabled);
            Assert.IsFalse(user.PendingTotpSecretProtected!.Contains(setup.Secret, StringComparison.OrdinalIgnoreCase), "stored encrypted");
        }

        using var wrong = await browser.PostAsync("/api/v1/account/totp/confirm", new { code = WrongCode(setup.Secret) });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);

        Assert.AreEqual(HttpStatusCode.NoContent, await browser.StatusAsync(HttpMethod.Post, "/api/v1/account/totp/confirm", new { code = Code(setup.Secret) }));

        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.IsTrue(me.User.IsTotpEnabled);
        Assert.AreEqual(new SessionFlags(false, false, false), me.Flags);
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"), "this session continues");
        Assert.AreEqual(HttpStatusCode.Unauthorized, await other.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"), "the other one ends");
        await using (var db = app.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            Assert.IsNull(user.PendingTotpSecretProtected);
            Assert.AreEqual(TotpService.StepAt(Clock.UtcNow), user.TotpLastUsedStep, "the confirming code counts as used");
        }
        var audit = await app.AuditAsync(id);
        CollectionAssert.Contains(audit, "account.totp_enabled:");
        Assert.IsTrue(audit.Any(e => e.StartsWith("account.totp_setup_started", StringComparison.Ordinal)));

        using var again = await browser.PostAsync("/api/v1/account/totp/setup");
        Assert.AreEqual("totp_already_enabled", await again.CodeAsync());
    }

    [TestMethod]
    public async Task ASetupExpiresAfterFifteenMinutes()
    {
        var (_, name, password) = await LoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);

        var first = await (await browser.PostAsync("/api/v1/account/totp/setup")).ReadAsync<TotpSetup>();
        time.Advance(TwoFactorService.PendingSetupLifetime + TimeSpan.FromSeconds(1));
        using var expired = await browser.PostAsync("/api/v1/account/totp/confirm", new { code = Code(first.Secret) });
        Assert.AreEqual("totp_setup_expired", await expired.CodeAsync());
    }

    [TestMethod]
    public async Task ASignInWithTwoFactorTakesThePasswordThenTheCode()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var browser = new Browser(app);

        var step1 = await browser.LoginAsync(name, password);

        Assert.AreEqual(new LoginResponse("totp", null), step1);
        Assert.IsFalse(browser.Cookies.ContainsKey("pgpm.auth"), "not signed in yet");
        var ticket = browser.LastSetCookies.Single(c => c.StartsWith("pgpm.2fa=", StringComparison.Ordinal)).ToLowerInvariant();
        StringAssert.Contains(ticket, "path=/api/v1/auth");
        StringAssert.Contains(ticket, "max-age=300");
        StringAssert.Contains(ticket, "httponly");
        Assert.AreEqual(HttpStatusCode.Unauthorized, await browser.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));

        using (var wrong = await browser.PostAsync("/api/v1/auth/login/totp", new { code = WrongCode(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.AreEqual("invalid_code", await wrong.CodeAsync());
        }
        time.Advance(TimeSpan.FromMilliseconds(1));

        var done = await (await browser.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })).ReadAsync<LoginResponse>();
        Assert.AreEqual("done", done.Next);
        Assert.AreEqual((id, true), (done.Me!.User.Id, done.Me.User.IsTotpEnabled));
        Assert.IsFalse(browser.Cookies.ContainsKey("pgpm.2fa"), "used up");
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"));
        var audit = await app.AuditAsync(id);
        CollectionAssert.Contains(audit, "auth.totp_failed:wrong code");
        Assert.AreEqual("auth.login_succeeded:password and 2FA", audit[^1]);
    }

    [TestMethod]
    public async Task ACodeCountsOnlyOnce()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var code = Code(secret);
        var first = new Browser(app);
        await first.LoginAsync(name, password);
        Assert.AreEqual(HttpStatusCode.OK, await first.StatusAsync(HttpMethod.Post, "/api/v1/auth/login/totp", new { code }));

        var second = new Browser(app);
        Assert.AreEqual("totp", (await second.LoginAsync(name, password)).Next);
        using var replay = await second.PostAsync("/api/v1/auth/login/totp", new { code });
        Assert.AreEqual("invalid_code", await replay.CodeAsync());
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.totp_failed:code already used");

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.AreEqual(HttpStatusCode.OK, await second.StatusAsync(HttpMethod.Post, "/api/v1/auth/login/totp", new { code = Code(secret) }));
    }

    /// <summary>Wrong codes count like wrong passwords, and the right password in between does not reset the count.</summary>
    [TestMethod]
    public async Task WrongCodesCountTowardsTheLockout()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, await browser.StatusAsync(HttpMethod.Post, "/api/v1/auth/login/totp", new { code = WrongCode(secret) }));
        }
        Assert.AreEqual("totp", (await browser.LoginAsync(name, password)).Next);
        for (var i = 0; i < 2; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, await browser.StatusAsync(HttpMethod.Post, "/api/v1/auth/login/totp", new { code = WrongCode(secret) }));
        }

        using var locked = await browser.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.AreEqual("locked_out", await locked.CodeAsync());
        Assert.AreEqual(HttpStatusCode.TooManyRequests, await new Browser(app).StatusAsync(HttpMethod.Post, "/api/v1/auth/login", new { username = name, password }));
        CollectionAssert.Contains(await app.AuditAsync(id), "auth.locked_out:5 minutes");
    }

    [TestMethod]
    public async Task TheCodeStepExpiresAfterFiveMinutesOrWhenTheAccountChanges()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();

        using (var none = await new Browser(app).PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) }))
        {
            Assert.AreEqual("login_expired", await none.CodeAsync());
        }
        var forged = new Browser(app);
        forged.Cookies["pgpm.2fa"] = "CfDJ8-forged";
        Assert.AreEqual("login_expired", await (await forged.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })).CodeAsync());

        var late = new Browser(app);
        await late.LoginAsync(name, password);
        time.Advance(TwoFactorService.LoginStepLifetime + TimeSpan.FromSeconds(1));
        Assert.AreEqual("login_expired", await (await late.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })).CodeAsync());

        var changed = new Browser(app);
        await changed.LoginAsync(name, password);
        await using (var db = app.NewDb())
        {
            await new UserAccounts(db).ResetPasswordAsync(id, "another-password-1", AuditWho.Cli);
        }
        Assert.AreEqual("login_expired", await (await changed.PostAsync("/api/v1/auth/login/totp", new { code = Code(secret) })).CodeAsync());
    }

    [TestMethod]
    public async Task TurningItOffNeedsThePasswordAndACode()
    {
        var (id, name, password) = await LoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        var secret = await EnrollAsync(browser);

        using (var wrongPassword = await browser.PostAsync("/api/v1/account/totp/disable", new { password = "not-the-password", code = Code(secret) }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
            Assert.IsTrue((await wrongPassword.ProblemAsync()).GetProperty("errors").TryGetProperty("password", out _));
        }
        using (var wrongCode = await browser.PostAsync("/api/v1/account/totp/disable", new { password, code = WrongCode(secret) }))
        {
            Assert.IsTrue((await wrongCode.ProblemAsync()).GetProperty("errors").TryGetProperty("code", out _));
        }

        Assert.AreEqual(HttpStatusCode.NoContent, await browser.StatusAsync(HttpMethod.Post, "/api/v1/account/totp/disable", new { password, code = Code(secret) }));

        var me = await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>();
        Assert.AreEqual((false, true), (me.User.IsTotpEnabled, me.Flags.TotpSetupSuggested));
        var audit = await app.AuditAsync(id);
        CollectionAssert.Contains(audit, "auth.totp_failed:turning 2FA off refused: wrong password");
        CollectionAssert.Contains(audit, "account.totp_disabled:");
        Assert.AreEqual("totp_not_enabled", await (await browser.PostAsync("/api/v1/account/totp/disable", new { password, code = Code(secret) })).CodeAsync());
    }

    [TestMethod]
    public async Task AnAdminsResetTurnsItOffEndsTheSessionsAndLiftsALockout()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var session = new Browser(app);
        await session.LoginAsync(name, password);
        Assert.AreEqual(HttpStatusCode.OK, await session.StatusAsync(HttpMethod.Post, "/api/v1/auth/login/totp", new { code = Code(secret) }));
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LockoutEnd, Clock.UtcNow.AddHours(1)));
        }

        // The seeded admin on the same server: its session cache forgets the user at once.
        var admin = new Browser(app);
        await admin.LoginAsync(TestUsers.Admin, TestUsers.Password);
        Assert.AreEqual(HttpStatusCode.NoContent, await admin.StatusAsync(HttpMethod.Post, $"/api/v1/users/{id}/reset-totp"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, await session.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"), "the sessions end");
        var item = await (await admin.GetAsync($"/api/v1/users/{id}")).ReadAsync<UserItem>();
        Assert.AreEqual((false, (DateTime?)null), (item.IsTotpEnabled, item.LockedUntil));
        var login = await new Browser(app).LoginAsync(name, password);
        Assert.AreEqual(("done", true), (login.Next, login.Me!.Flags.TotpSetupSuggested), "the next sign-in offers the setup again");
    }

    /// <summary>The day's code (docs/03 §4.2): due at the first 04:00 after it, asked for over the page, the session goes on.</summary>
    [TestMethod]
    public async Task TheDaysCodeIsAskedForAndTheSessionGoesOn()
    {
        var (id, name, password, secret) = await EnrolledUserAsync();
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Post, "/api/v1/auth/login/totp", new { code = Code(secret) }));
        var options = app.Services.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;
        Assert.IsFalse((await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>()).Session.TotpDue);

        time.SetUtcNow(SessionRules.TotpDue(Clock.UtcNow, options)!.Value.AddMinutes(1));

        using (var held = await browser.GetAsync("/api/v1/rights"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, held.StatusCode);
            Assert.AreEqual(SessionRules.TotpDueCode, await held.CodeAsync());
        }
        Assert.IsTrue((await (await browser.GetAsync("/api/v1/auth/me")).ReadAsync<MeResponse>()).Session.TotpDue, "who it is still answers, and says so");
        Assert.AreEqual("invalid_code", await (await browser.PostAsync("/api/v1/auth/totp/daily", new { code = WrongCode(secret) })).CodeAsync());
        Assert.AreEqual(HttpStatusCode.NoContent, await browser.StatusAsync(HttpMethod.Post, "/api/v1/auth/totp/daily", new { code = Code(secret) }));
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"), "the same session goes on");
        var audit = await app.AuditAsync(id);
        CollectionAssert.Contains(audit, "auth.totp_daily_confirmed:");
        CollectionAssert.Contains(audit, "auth.totp_failed:wrong code (the day's code)");
    }

    /// <summary>Auth:LoginDays: after 30 days (at 04:00) the session ends with login_expired; a new sign-in starts a new month.</summary>
    [TestMethod]
    public async Task AfterThirtyDaysTheSignInIsAskedForAgain()
    {
        var (_, name, password) = await LoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        var options = app.Services.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;
        var ends = SessionRules.LoginEnds(Clock.UtcNow, options)!.Value;

        time.SetUtcNow(ends.AddMinutes(-1));
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"), "a minute before");
        time.SetUtcNow(ends.AddMinutes(1));
        using var expired = await browser.GetAsync("/api/v1/rights");
        Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.AreEqual(SessionRules.LoginExpiredCode, await expired.CodeAsync());
        Assert.IsFalse(browser.Cookies.ContainsKey(AuthSetup.CookieName), "the cookie is removed");
        Assert.AreEqual("done", (await browser.LoginAsync(name, password)).Next);
    }

    /// <summary><c>last_seen_at</c>: what the gates let through, at most every five minutes.</summary>
    [TestMethod]
    public async Task LastSeenIsWrittenAtMostEveryFiveMinutes()
    {
        var (id, name, password) = await LoginTests.NewUserAsync(app);
        var browser = new Browser(app);
        await browser.LoginAsync(name, password);
        async Task<DateTime?> SeenAsync()
        {
            await using var db = app.NewDb();
            return await db.Users.Where(u => u.Id == id).Select(u => u.LastSeenAt).SingleAsync();
        }

        await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights");
        var first = await SeenAsync();
        Assert.IsNotNull(first);
        time.Advance(TimeSpan.FromMinutes(2));
        await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights");
        Assert.AreEqual(first, await SeenAsync(), "at most every five minutes");
        time.Advance(LastSeen.Every);
        await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights");
        Assert.IsGreaterThan(first.Value, (await SeenAsync())!.Value);
    }
}

/// <summary>Auth:TotpPolicy (docs/03 §4.4): RequiredForAdmins and Required hold every call but the setup – for local sessions.</summary>
[TestClass]
[DoNotParallelize]
public sealed class TotpPolicyTests
{
    private static TestApp forAdmins = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => forAdmins = new TestApp(new Dictionary<string, string?> { ["Auth:TotpPolicy"] = "RequiredForAdmins" });

    [ClassCleanup]
    public static void Stop() => forAdmins.Dispose();

    [TestInitialize]
    public void Freeze() => Clock.Provider = new FakeTimeProvider(DateTimeOffset.UtcNow);

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    [TestMethod]
    public async Task AnAdminWithoutItMaySetItUpAndNothingElse()
    {
        var (_, name, password) = await LoginTests.NewUserAsync(forAdmins, admin: true);
        var browser = new Browser(forAdmins);

        var login = await browser.LoginAsync(name, password);

        Assert.AreEqual(new SessionFlags(false, true, false), login.Me!.Flags);
        using (var held = await browser.GetAsync("/api/v1/users"))
        {
            Assert.AreEqual("totp_setup_required", await held.CodeAsync());
        }
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/auth/me"));

        var secret = await TwoFactorLoginTests.EnrollAsync(browser);

        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/users"));
        using var disable = await browser.PostAsync("/api/v1/account/totp/disable", new { password, code = TwoFactorLoginTests.Code(secret) });
        Assert.AreEqual("totp_required", await disable.CodeAsync());
    }

    [TestMethod]
    public async Task RequiredForAdminsLeavesOthersAPrompt()
    {
        var (_, name, password) = await LoginTests.NewUserAsync(forAdmins);
        var browser = new Browser(forAdmins);
        Assert.AreEqual(new SessionFlags(false, false, true), (await browser.LoginAsync(name, password)).Me!.Flags);
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Get, "/api/v1/rights"));
    }

    /// <summary>The password comes first: its gate holds the setup, the 2FA gate lets the password change through.</summary>
    [TestMethod]
    public async Task ATemporaryPasswordIsChangedBeforeTheSetup()
    {
        var (_, name, temporary) = await LoginTests.NewUserAsync(forAdmins, admin: true, temporary: true);
        var browser = new Browser(forAdmins);
        Assert.AreEqual(new SessionFlags(true, true, false), (await browser.LoginAsync(name, temporary)).Me!.Flags);

        Assert.AreEqual("must_change_password", await (await browser.PostAsync("/api/v1/account/totp/setup")).CodeAsync());
        Assert.AreEqual(HttpStatusCode.NoContent,
            await browser.StatusAsync(HttpMethod.Post, "/api/v1/account/password", new { currentPassword = temporary, newPassword = "a-new-password-1" }));
        Assert.AreEqual("totp_setup_required", await (await browser.GetAsync("/api/v1/rights")).CodeAsync());
        Assert.AreEqual(HttpStatusCode.OK, await browser.StatusAsync(HttpMethod.Post, "/api/v1/account/totp/setup"));
    }
}

/// <summary>When the sign-in and the day's code are due: 04:00 in PGPM's zone, whatever the season.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SessionRulesTests
{
    private TimeZoneInfo zone = null!;

    [TestInitialize]
    public void Zurich()
    {
        zone = Clock.TimeZone;
        Clock.TimeZone = Clock.FromName("Europe/Zurich");
    }

    [TestCleanup]
    public void Restore() => Clock.TimeZone = zone;

    private static DateTime Local(int year, int month, int day, int hour, int minute = 0) =>
        Clock.ToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified));

    private static string AsLocal(DateTime? instant) => Clock.ToLocal(instant!.Value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [TestMethod]
    public void TheCodeIsDueAtTheFirstFourOClockAfterIt()
    {
        var options = new AuthOptions();
        Assert.AreEqual("2026-11-17 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), options)));
        Assert.AreEqual("2026-11-16 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 3, 59), options)), "before four: the same morning");
        Assert.AreEqual("2026-11-17 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 4, 0), options)), "at four exactly: the next day");
        Assert.AreEqual("2026-11-18 04:00", AsLocal(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), new AuthOptions { TotpDays = 2 })));
        Assert.IsNull(SessionRules.TotpDue(Local(2026, 11, 16, 9, 0), new AuthOptions { TotpDays = 0 }));
    }

    [TestMethod]
    public void BothDaylightSavingChangesStillEndTheDayAtFour()
    {
        var options = new AuthOptions();
        var spring = SessionRules.TotpDue(Local(2026, 3, 28, 12, 0), options);
        var autumn = SessionRules.TotpDue(Local(2026, 10, 24, 12, 0), options);
        Assert.AreEqual("2026-03-29 04:00", AsLocal(spring));
        Assert.AreEqual("2026-10-25 04:00", AsLocal(autumn));
        Assert.AreEqual(TimeSpan.FromHours(15), spring - Local(2026, 3, 28, 12, 0), "an hour less");
        Assert.AreEqual(TimeSpan.FromHours(17), autumn - Local(2026, 10, 24, 12, 0), "an hour more");
    }

    [TestMethod]
    public void TheSignInIsDueAtFourAfterThirtyDays()
    {
        var options = new AuthOptions();
        Assert.AreEqual("2026-12-17 04:00", AsLocal(SessionRules.LoginEnds(Local(2026, 11, 16, 9, 0), options)));
        Assert.AreEqual("2026-12-16 04:00", AsLocal(SessionRules.LoginEnds(Local(2026, 11, 16, 2, 0), options)));
        Assert.IsNull(SessionRules.LoginEnds(Local(2026, 11, 16, 9, 0), new AuthOptions { LoginDays = 0 }));
    }
}

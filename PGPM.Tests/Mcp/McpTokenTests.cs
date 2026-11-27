using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Lib.Helper;
using PGPM.Mcp;
using static PGPM.Tests.Mcp.McpKit;

namespace PGPM.Tests.Mcp;

/// <summary>
/// The token model (docs/03 §7.1): two tokens per user, the value once and only its hash kept, on/off/regenerate, the write
/// token's second factor (TOTP or a Microsoft link), the ends – time, stamp, deactivation, the right –, the expiry task, an
/// admin's off with a reason, <i>Revoke all</i>, the status.
/// </summary>
[TestClass]
public sealed class McpTokenTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task EveryUserHasTwoTokensAndTheValueIsShownOnce()
    {
        var user = await MemberAsync(app, "tokens");
        var account = await AccountAsync(app, user);
        Assert.IsTrue(account.Enabled && account.CanUse);
        StringAssert.EndsWith(account.Url, "/mcp");
        Assert.AreEqual((false, "off", false), (account.Read.On, account.Read.State, account.Read.HasValue));
        Assert.AreEqual((false, false), (account.Write.On, account.Write.HasValue));

        var issued = await TurnOnAsync(app, user, "read");
        StringAssert.StartsWith(issued.Value, McpTokenValues.ReadPrefix);
        Assert.AreEqual(51, issued.Value!.Length);
        Assert.IsTrue(issued.Token.On);
        Assert.AreEqual(issued.Value[^4..], issued.Token.Hint);
        Assert.AreEqual(Items.ItemAccess.Read, issued.Token.Access);
        Assert.IsNull((await TurnOnAsync(app, user, "read")).Value, "shown once: switching it on again shows nothing");

        await using var db = app.NewDb();
        var rows = await db.McpTokens.Where(t => t.UserId == user).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(McpTokenValues.Hash(issued.Value), rows.Single(t => t.Kind == McpTokenKind.Read).Hash, "only the hash is kept");
        Assert.IsFalse(await db.McpTokens.AnyAsync(t => t.Hash == issued.Value));
        var audit = await app.AuditAsync(user);
        Assert.IsTrue(audit.Any(a => a.StartsWith("mcp.token_on:", StringComparison.Ordinal) && a.Contains("a new value was made")), string.Join("\n", audit));
    }

    [TestMethod]
    public async Task SwitchingOnNeedsMcpUseAndATokenWithoutItIsRefused()
    {
        var nobody = await Sync.SyncKit.UserAsync(app, "no-mcp");
        using (var refused = await app.ClientFor(nobody, "").PostAsync("/api/v1/account/mcp/read/on", null))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        Assert.IsFalse((await AccountAsync(app, nobody)).CanUse);

        var user = await MemberAsync(app, "loses-mcp");
        var token = (await TurnOnAsync(app, user, "read")).Value!;
        Assert.IsNull(await RefusalAsync(app, token));
        await using (var db = app.NewDb())
        {
            await db.UserGroups.Where(m => m.UserId == user).ExecuteDeleteAsync();
        }
        Assert.AreEqual("no_right", await RefusalAsync(app, token), "a user without MCP: Use");
        using var off = await Account(app, user, "").PostAsync("/api/v1/account/mcp/read/off", null);
        Assert.AreEqual(HttpStatusCode.OK, off.StatusCode, "switching off needs no right");
    }

    [TestMethod]
    public async Task TheWriteTokenNeedsTotpOrAMicrosoftAccount()
    {
        var user = await MemberAsync(app, "second-factor");
        using (var refused = await Account(app, user).PostAsync("/api/v1/account/mcp/write/on", null))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.AreEqual("totp_required", await refused.CodeAsync());
        }
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.MicrosoftTenantId, Guid.NewGuid()).SetProperty(x => x.MicrosoftObjectId, Guid.NewGuid()));
        }
        var issued = await TurnOnAsync(app, user, "write");
        StringAssert.StartsWith(issued.Value, McpTokenValues.WritePrefix);
        Assert.AreEqual(Items.ItemAccess.Edit, issued.Token.Access);
        Assert.IsTrue(issued.Token.Until > Clock.UtcNow.AddHours(11.9) && issued.Token.Until <= Clock.UtcNow.AddHours(12));
        Assert.IsNull(await RefusalAsync(app, issued.Value));

        var withTotp = await MemberAsync(app, "totp");
        await SecondFactorAsync(app, withTotp);
        Assert.IsNotNull((await TurnOnAsync(app, withTotp, "write")).Value);
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == withTotp).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsTotpEnabled, false));
        }
        var token = await WriteTokenAsync(withTotp);
        Assert.AreEqual("totp", McpTokenState.Of(token.Row, token.User, new McpOptions(), Clock.UtcNow).Why, "2FA off: the write token is off");

        static async Task<(McpTokenEntity Row, McpTokenUser User)> WriteTokenAsync(Guid id)
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<McpTokenService>();
            return ((await service.RowsAsync(id, CancellationToken.None)).Write, await service.UserAsync(id, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task RegeneratingStopsTheOldValueAtOnce()
    {
        var user = await MemberAsync(app, "regenerate");
        var first = (await TurnOnAsync(app, user, "read")).Value!;
        using var regenerate = await Account(app, user).PostAsync("/api/v1/account/mcp/read/regenerate", null);
        Assert.AreEqual("no-store", regenerate.Headers.CacheControl?.ToString());
        var second = (await regenerate.ReadAsync<McpTokenIssued>()).Value!;
        Assert.AreNotEqual(first, second);
        Assert.AreEqual("unknown", await RefusalAsync(app, first));
        await using var client = await ClientAsync(app, second);
        Assert.IsNotEmpty(await client.ListToolsAsync(), "the new one is on, as the old one was");
    }

    [TestMethod]
    public async Task RefusedTokensSayWhatToDo()
    {
        using (var none = await RawAsync(app, null))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, none.StatusCode);
        }
        using (var unknown = await RawAsync(app, McpTokenValues.New(McpTokenKind.Read)))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.StatusCode);
            StringAssert.Contains(unknown.Headers.WwwAuthenticate.ToString(), "invalid_token");
        }
        var user = await MemberAsync(app, "refused");
        var token = (await TurnOnAsync(app, user, "read")).Value!;
        using (var off = await Account(app, user).PostAsync("/api/v1/account/mcp/read/off", null))
        {
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode);
        }
        using var refused = await RawAsync(app, token);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, "a known token that is only off");
        var body = System.Text.Json.JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual("off", body.GetProperty("error").GetString());
        StringAssert.Contains(body.GetProperty("message").GetString(), "Account → Security");
        Assert.AreEqual((McpTokenEnds.Off, false), ((await AccountAsync(app, user)).Read.EndedWhy, (await AccountAsync(app, user)).Read.On));
        await using var db = app.NewDb();
        Assert.IsTrue(await db.McpLog.AnyAsync(l => l.UserId == user && l.Method == "auth" && l.Outcome == McpOutcomes.Unauthenticated && l.ErrorCode == "off"));
    }

    [TestMethod]
    public async Task TokensEndWithTheirTimeTheStampAndTheUser()
    {
        var (user, read, write) = await WriterAsync(app, "ends");
        await using (var db = app.NewDb())
        {
            await db.McpTokens.Where(t => t.UserId == user && t.Kind == McpTokenKind.Write)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.OnUntil, Clock.UtcNow.AddMinutes(-1)));
        }
        Assert.AreEqual("expired", await RefusalAsync(app, write));
        Assert.IsNull(await RefusalAsync(app, read), "the read token goes on");

        // The read token lasts Mcp:ReadDaysAfterOnline after its user was last online in the web UI.
        await using (var db = app.NewDb())
        {
            await db.McpTokens.Where(t => t.UserId == user && t.Kind == McpTokenKind.Read)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.OnSince, Clock.UtcNow.AddDays(-8)));
            await db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, Clock.UtcNow.AddDays(-2)));
        }
        Assert.IsNull(await RefusalAsync(app, read), "online two days ago");
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, Clock.UtcNow.AddDays(-7.5)));
        }
        Assert.AreEqual("expired", await RefusalAsync(app, read));

        var ended = await app.Services.GetRequiredService<McpTokenExpiryTask>().RunAsync(CancellationToken.None);
        Assert.IsGreaterThanOrEqualTo(2, ended);
        var account = await AccountAsync(app, user);
        Assert.AreEqual((McpTokenEnds.Expired, McpTokenEnds.Expired), (account.Read.EndedWhy, account.Write.EndedWhy));
        await using (var db = app.NewDb())
        {
            var ids = await db.McpTokens.Where(t => t.UserId == user).Select(t => t.Id).ToListAsync();
            Assert.AreEqual(2, await db.AuditLog.CountAsync(l => l.Event == AuditEvents.McpTokenExpired && ids.Contains(l.SubjectId!.Value)));
            Assert.IsTrue(await db.KeyValues.AnyAsync(k => k.Key == McpTokenExpiryTask.HealthKey));
        }

        // A password change (a new stamp) ends both; so does deactivation.
        var other = await MemberAsync(app, "stamp");
        var token = (await TurnOnAsync(app, other, "read")).Value!;
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == other).ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, UserAccounts.NewStamp()));
        }
        Assert.AreEqual("stamp", await RefusalAsync(app, token));
        var third = await MemberAsync(app, "inactive");
        var value = (await TurnOnAsync(app, third, "read")).Value!;
        await using (var db = app.NewDb())
        {
            await db.Users.Where(u => u.Id == third).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        }
        Assert.AreEqual("inactive", await RefusalAsync(app, value));
    }

    [TestMethod]
    public void TheStateFollowsTheRules()
    {
        var now = Clock.UtcNow;
        var options = new McpOptions();
        var user = new McpTokenUser(Guid.NewGuid(), "u", "U", true, "s1", false, true, null);
        var write = new McpTokenEntity { Kind = McpTokenKind.Write, Hash = "h", OnSince = now.AddHours(-1), OnUntil = now.AddHours(11), OnStamp = "s1" };
        Assert.AreEqual("on", McpTokenState.Of(write, user, options, now).Why, "a Microsoft account is the second factor");
        Assert.AreEqual("totp", McpTokenState.Of(write, user with { HasMicrosoftAccount = false }, options, now).Why);
        Assert.AreEqual("on", McpTokenState.Of(write, user with { HasMicrosoftAccount = false, IsTotpEnabled = true }, options, now).Why);
        Assert.AreEqual("stamp", McpTokenState.Of(write, user with { SecurityStamp = "s2" }, options, now).Why);
        Assert.AreEqual("inactive", McpTokenState.Of(write, user with { IsActive = false }, options, now).Why);
        Assert.AreEqual("no_right", McpTokenState.Of(write, user with { CanUse = false }, options, now).Why);
        Assert.AreEqual("expired", McpTokenState.Of(write, user, options, now.AddHours(11)).Why);
        Assert.AreEqual("mcp_off", McpTokenState.Of(write, user, new McpOptions { Enabled = false }, now).Why);
        var read = new McpTokenEntity { Kind = McpTokenKind.Read, Hash = "h", OnSince = now.AddDays(-10), OnStamp = "s1" };
        Assert.AreEqual("expired", McpTokenState.Of(read, user, options, now).Why);
        Assert.AreEqual("on", McpTokenState.Of(read, user with { LastSeenAt = now.AddDays(-6) }, options, now).Why);
        Assert.AreEqual("blocked", McpTokenState.Of(new McpTokenEntity { Kind = McpTokenKind.Read, BlockedAt = now }, user, options, now).Why);
        Assert.AreEqual("off", McpTokenState.Of(new McpTokenEntity { Kind = McpTokenKind.Read }, user, options, now).Why);
    }

    [TestMethod]
    public void TheCapsAreTheCeiling()
    {
        var admin = McpRights.For(new UserRights(true, new Dictionary<Area, Level>()), McpTokenKind.Write);
        Assert.IsFalse(admin.IsAdmin);
        CollectionAssert.AreEquivalent(new[] { Area.Notes, Area.Ideas, Area.Leads, Area.Projects, Area.Maps }, admin.Levels.Keys.ToList());
        Assert.IsFalse(admin.Has(Area.Configuration, Level.Manage));
        Assert.IsEmpty(McpRights.For(new UserRights(true, new Dictionary<Area, Level>()), McpTokenKind.Read).Levels, "the read token creates nothing");
        var member = McpRights.For(new UserRights(false, new Dictionary<Area, Level> { [Area.Notes] = Level.Create, [Area.Mcp] = Level.Manage }),
            McpTokenKind.Write);
        CollectionAssert.AreEquivalent(new[] { Area.Notes }, member.Levels.Keys.ToList(), "never more than the user, never MCP itself");
    }

    [TestMethod]
    public async Task AnAdminSeesTheTokensAndSwitchesOneOffWithAReason()
    {
        var user = await MemberAsync(app, "admin-off");
        var token = (await TurnOnAsync(app, user, "read")).Value!;
        await using (var client = await ClientAsync(app, token))
        {
            await OkAsync(client, "list_config");
        }
        var admin = app.Client(TestUsers.Admin);
        var all = await (await admin.GetAsync("/api/v1/mcp/tokens")).ReadAsync<List<McpAdminTokenItem>>();
        var row = all.Single(t => t.UserId == user && t.Token.Kind == McpTokenKind.Read);
        Assert.IsTrue(row.Token.On);
        Assert.IsNotNull(row.Token.LastUsedAt);
        using (var tooShort = await admin.PostAsJsonAsync($"/api/v1/mcp/tokens/{row.Id}/off", new { reason = "x" }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, tooShort.StatusCode);
        }
        using (var off = await admin.PostAsJsonAsync($"/api/v1/mcp/tokens/{row.Id}/off", new { reason = "Left the team" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode, await off.Content.ReadAsStringAsync());
        }
        Assert.AreEqual("off", await RefusalAsync(app, token));
        var account = await AccountAsync(app, user);
        Assert.AreEqual((McpTokenEnds.Admin, "Left the team"), (account.Read.EndedWhy, account.Read.EndedNote));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(TestUsers.Member).GetAsync("/api/v1/mcp/tokens")).StatusCode, "MCP: View");

        var status = await (await admin.GetAsync("/api/v1/mcp/status")).ReadAsync<McpStatus>();
        Assert.IsGreaterThanOrEqualTo(1, status.Calls7d);
        Assert.IsTrue(status.Tools7d.Any(t => t.Name == "list_config"));
        Assert.AreEqual(7, status.Days.Count);

        var catalog = await (await app.Client(TestUsers.Viewer).GetAsync("/api/v1/mcp/catalog")).ReadAsync<McpCatalog>();
        Assert.IsTrue(catalog.Tools.Any(t => t.Name == "search_items" && t.ReadToken));
        Assert.IsTrue(catalog.Tools.Any(t => t.Name == "create_note" && !t.ReadToken && t.Right == "Notes: Create"));
        Assert.IsTrue(catalog.Ceiling.Any(c => c.Area == Area.Configuration && c.Write is null));
    }

    /// <summary>Last, alone: revoking every token would end the other tests' tokens.</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task RevokingAllStopsEveryValue()
    {
        var user = await MemberAsync(app, "revoke");
        var token = (await TurnOnAsync(app, user, "read")).Value!;
        var admin = app.Client(TestUsers.Admin);
        using (var revoke = await admin.PostAsJsonAsync("/api/v1/mcp/tokens/revoke-all", new { reason = "A laptop was stolen" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode, await revoke.Content.ReadAsStringAsync());
            Assert.IsGreaterThanOrEqualTo(1, (await revoke.ReadAsync<McpRevokeAllResult>()).Revoked);
        }
        Assert.AreEqual("unknown", await RefusalAsync(app, token), "the value is gone, not just off");
        var account = await AccountAsync(app, user);
        Assert.AreEqual((false, false, "A laptop was stolen"), (account.Read.On, account.Read.HasValue, account.Read.EndedNote));
        Assert.IsNotNull((await TurnOnAsync(app, user, "read")).Value, "switching it on makes a new value");
        await using var db = app.NewDb();
        var revoked = await db.AuditLog.Where(l => l.Event == AuditEvents.McpTokensRevoked).ToListAsync();
        Assert.IsTrue(revoked.Any(l => AuditLog.ReadDetail(l.Detail).Summary?.Contains("A laptop was stolen", StringComparison.Ordinal) == true));
    }
}

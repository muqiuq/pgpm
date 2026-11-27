using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PGPM.Audit;
using PGPM.Auth.Rights;

namespace PGPM.Tests.Hosting;

/// <summary>
/// No endpoint slips in unprotected (PNMTD's endpoint audit, docs/03 §3.5): the anonymous endpoints are exactly the
/// allowlist, every v1 endpoint names its right, answers each caller as the matrix says, and every write names its audit
/// event and records a refusal. A new endpoint fails these tests until it is added below (docs/05 §4).
/// </summary>
[TestClass]
public sealed class EndpointAuditTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>Static files (the UI's shell) are served before routing and are anonymous too: they hold no data.</summary>
    private static readonly string[] AnonymousAllowlist =
    [
        "GET /healthz",
        "GET /api/v1/info",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/login/totp",
        "GET /api/v1/auth/me",
        "GET /api/v1/auth/options",
        "GET /api/v1/auth/sso/microsoft",
        "GET /{**path:nonfile}",
        "HEAD /{**path:nonfile}",
    ];

    /// <summary>The anonymous endpoints the matrix leaves out: they answer signed-out callers by design.</summary>
    private static readonly string[] OutsideTheMatrix =
    [
        "GET /api/v1/info", "POST /api/v1/auth/login", "POST /api/v1/auth/login/totp", "GET /api/v1/auth/options", "GET /api/v1/auth/sso/microsoft",
    ];

    /// <summary>
    /// What each v1 endpoint needs (docs/03 §4.5, §6.2): a level in an area ("AuditLog:View"), either of two
    /// ("Notes:Create|Ideas:Create"), the admin flag ("admin"), or only a session ("signed-in": one's own account).
    /// </summary>
    private static readonly Dictionary<string, string> Matrix = new()
    {
        ["GET /api/v1/auth/me"] = "signed-in",
        ["POST /api/v1/auth/logout"] = "signed-in",
        ["POST /api/v1/auth/totp/daily"] = "signed-in",
        ["GET /api/v1/account/access"] = "signed-in",
        ["POST /api/v1/account/password"] = "signed-in",
        ["POST /api/v1/account/sessions/revoke"] = "signed-in",
        ["POST /api/v1/account/totp/setup"] = "signed-in",
        ["POST /api/v1/account/totp/confirm"] = "signed-in",
        ["POST /api/v1/account/totp/disable"] = "signed-in",
        ["GET /api/v1/rights"] = "signed-in",
        ["GET /api/v1/users"] = "admin",
        ["POST /api/v1/users"] = "admin",
        ["GET /api/v1/users/{id:guid}"] = "admin",
        ["PUT /api/v1/users/{id:guid}"] = "admin",
        ["DELETE /api/v1/users/{id:guid}"] = "admin",
        ["GET /api/v1/users/{id:guid}/access"] = "admin",
        ["POST /api/v1/users/{id:guid}/reset-password"] = "admin",
        ["POST /api/v1/users/{id:guid}/reset-totp"] = "admin",
        ["POST /api/v1/users/{id:guid}/unlock"] = "admin",
        ["PUT /api/v1/users/{id:guid}/microsoft-account"] = "admin",
        ["DELETE /api/v1/users/{id:guid}/microsoft-account"] = "admin",
        ["GET /api/v1/groups"] = "admin",
        ["POST /api/v1/groups"] = "admin",
        ["GET /api/v1/groups/{id:guid}"] = "admin",
        ["PUT /api/v1/groups/{id:guid}"] = "admin",
        ["DELETE /api/v1/groups/{id:guid}"] = "admin",
        ["PUT /api/v1/groups/{id:guid}/rights"] = "admin",
        ["PUT /api/v1/groups/{id:guid}/members"] = "admin",
        ["GET /api/v1/audit"] = "AuditLog:View",
        ["GET /api/v1/audit/events"] = "AuditLog:View",
    };

    private sealed record EndpointInfo(string Key, string Method, string Pattern, RouteEndpoint Endpoint);

    private static List<EndpointInfo> Endpoints()
    {
        var sources = app.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        return sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var pattern = "/" + e.RoutePattern.RawText!.TrimStart('/');
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                return methods.Select(m => new EndpointInfo($"{m} {pattern}", m, pattern, e));
            })
            .ToList();
    }

    private static List<EndpointInfo> V1() =>
        Endpoints().Where(e => e.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && !e.Pattern.Contains("{**rest}")).ToList();

    [TestMethod]
    public void AnonymousEndpointsAreExactlyTheAllowlist()
    {
        var anonymous = Endpoints()
            .Where(e => e.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        CollectionAssert.AreEqual(AnonymousAllowlist.Order(StringComparer.Ordinal).ToList(), anonymous, "Anonymous: " + string.Join(", ", anonymous));
    }

    /// <summary>The right an endpoint's attributes ask for, in the matrix's notation: the action's own, else the controller's.</summary>
    private static string RightOf(RouteEndpoint endpoint)
    {
        var rights = endpoint.Metadata.GetOrderedMetadata<RequireRightAttribute>().ToList();
        if (rights.Select(r => r.Area).Distinct().Count() > 1)
        {
            return string.Join("&", rights.Select(r => RightsCatalog.Name(r.Area, r.Level)));
        }
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).ToList();
        if (policies.LastOrDefault() is not { } last)
        {
            return "signed-in";
        }
        return last == RightPolicies.Admin ? "admin" : last[RightPolicies.Prefix.Length..];
    }

    [TestMethod]
    public void EveryV1EndpointNamesTheRightOfTheMatrix()
    {
        var v1 = V1().Where(e => Matrix.ContainsKey(e.Key)).ToList();
        Assert.IsGreaterThanOrEqualTo(Matrix.Count, v1.Count);
        var wrong = v1.Where(e => RightOf(e.Endpoint) != Matrix[e.Key]).Select(e => $"{e.Key}: {RightOf(e.Endpoint)}, the matrix says {Matrix[e.Key]}");
        Assert.AreEqual("", string.Join("\n", wrong));
        // A write needs a right, except one's own session and account.
        var selfService = v1.Where(e => e.Method != "GET" && Matrix[e.Key] == "signed-in").Select(e => e.Key).ToList();
        Assert.IsTrue(selfService.All(k => k.Contains("/api/v1/auth/", StringComparison.Ordinal) || k.Contains("/api/v1/account/", StringComparison.Ordinal)),
            "writes with no right: " + string.Join(", ", selfService));
    }

    [TestMethod]
    public void TheMatrixCoversEveryV1Endpoint()
    {
        var v1 = V1().Select(e => e.Key).Where(k => !OutsideTheMatrix.Contains(k)).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(Matrix.Keys.Order(StringComparer.Ordinal).ToList(), v1,
            "Not in the matrix: " + string.Join(", ", v1.Except(Matrix.Keys)) + "; gone: " + string.Join(", ", Matrix.Keys.Except(v1)));
    }

    /// <summary>
    /// Every endpoint × who: signed out 401; a user with every level but the one it needs 403; one with exactly that level,
    /// and an admin, through. Ids are random and bodies empty, so the allowed calls end in 404 or 400 and change nothing.
    /// </summary>
    [TestMethod]
    public async Task EveryV1EndpointAnswersAsItsRightSays()
    {
        var failures = new List<string>();
        foreach (var (key, right) in Matrix)
        {
            var cases = new List<(string Who, string? Rights, bool Anonymous, string Expected)>
            {
                ("anonymous", null, true, "401"),
                ("an admin", "admin", false, "allowed"),
            };
            if (right == "signed-in")
            {
                cases.Add(("no rights", "", false, "allowed"));
            }
            else if (right == "admin")
            {
                cases.Add(("every right but the flag", Everything(), false, "403"));
            }
            else
            {
                var options = right.Split('|');
                cases.Add(("every right but this", Everything(except: options), false, "403"));
                cases.AddRange(options.Select(o => ($"exactly {o}", (string?)o, false, "allowed")));
            }
            foreach (var (who, rights, anonymous, expected) in cases)
            {
                var status = await CallAsync(key, anonymous ? null : app.ClientWithRights(rights!));
                if (!Meets(status, expected))
                {
                    failures.Add($"{key} with {who}: {(int)status}, expected {expected}");
                }
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    /// <summary>The seeded groups answer as docs/03 §6.2 says: Members and Viewers are no admins, and Viewers have no rights.</summary>
    [TestMethod]
    public async Task TheSeededUsersGetWhatTheirGroupsGive()
    {
        var failures = new List<string>();
        foreach (var (key, right) in Matrix)
        {
            foreach (var user in new[] { TestUsers.Admin, TestUsers.Member, TestUsers.Viewer })
            {
                var allowed = user == TestUsers.Admin || right == "signed-in"
                    || (right != "admin" && right.Split('|').Any(r => RightsCatalog.TryParse(r, out var a, out var l) && TestUsers.RightsOf(user).Has(a, l)));
                var status = await CallAsync(key, app.Client(user));
                if (!Meets(status, allowed ? "allowed" : "403"))
                {
                    failures.Add($"{key} as {user}: {(int)status}");
                }
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    /// <summary>Nothing slips in unlogged (docs/03 §3.3): every write endpoint names its event, or why it has none.</summary>
    [TestMethod]
    public void EveryWriteEndpointNamesItsAuditEvent()
    {
        var writes = V1().Where(e => e.Method != "GET").ToList();
        Assert.IsGreaterThan(15, writes.Count);
        var missing = writes.Where(e => e.Endpoint.Metadata.GetMetadata<AuditAttribute>() is null && e.Endpoint.Metadata.GetMetadata<NotAuditedAttribute>() is null)
            .Select(e => e.Key).ToList();
        Assert.AreEqual("", string.Join(", ", missing), "write endpoints with neither [Audit] nor [NotAudited]");
        var unknown = writes.SelectMany(e => e.Endpoint.Metadata.GetMetadata<AuditAttribute>()?.Events ?? []).Where(t => !AuditCatalog.Contains(t)).ToList();
        Assert.AreEqual("", string.Join(", ", unknown), "events that are not in the catalogue");
    }

    /// <summary>A refused write is in the audit log: its endpoint's event, outcome denied, the endpoint, who.</summary>
    [TestMethod]
    public async Task EveryAuditedEndpointWritesItsDeniedEntry()
    {
        var failures = new List<string>();
        foreach (var e in V1().Where(e => Matrix.ContainsKey(e.Key) && e.Method != "GET"))
        {
            var right = Matrix[e.Key];
            if (e.Endpoint.Metadata.GetMetadata<AuditAttribute>() is not { } audit || right == "signed-in")
            {
                continue;
            }
            var name = $"denied-{Guid.NewGuid():N}";
            var status = await CallAsync(e.Key, app.ClientWithRights(right == "admin" ? Everything() : Everything(except: right.Split('|')), name));
            await using var db = app.NewDb();
            var entry = await db.AuditLog.FromSql($"SELECT * FROM audit_log WHERE detail->>'username' = {name}").AsNoTracking().SingleOrDefaultAsync();
            var detail = entry is null ? null : AuditLog.ReadDetail(entry.Detail);
            if (status != HttpStatusCode.Forbidden || entry is null || entry.Event != audit.Events[0] || detail!.Outcome != AuditOutcome.Denied
                || detail.Request != e.Key || entry.ActorUserId != TestUsers.IdOf(TestUsers.Plain))
            {
                failures.Add($"{e.Key}: {(int)status}, entry {entry?.Event ?? "none"} {detail?.Outcome} {detail?.Request}");
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    /// <summary>Every area at its highest level, but those named, which get none ("AuditLog:View" withholds AuditLog).</summary>
    private static string Everything(params string[] except)
    {
        var withheld = except.Select(e => e.Split(':')[0]).ToHashSet();
        return string.Join(",", RightsCatalog.Areas.Where(a => !withheld.Contains(a.Area.ToString()))
            .Select(a => RightsCatalog.Name(a.Area, a.Levels[^1].Level)));
    }

    private static async Task<HttpStatusCode> CallAsync(string key, HttpClient? client)
    {
        var method = key[..key.IndexOf(' ')];
        var path = System.Text.RegularExpressions.Regex.Replace(key[(key.IndexOf(' ') + 1)..], @"\{[^}]+\}", _ => Guid.NewGuid().ToString());
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await (client ?? app.Client()).SendAsync(request);
        return response.StatusCode;
    }

    private static bool Meets(HttpStatusCode status, string expected) => expected switch
    {
        "401" => status == HttpStatusCode.Unauthorized,
        "403" => status == HttpStatusCode.Forbidden,
        _ => status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && (int)status < 500,
    };
}

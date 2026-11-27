using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Data;

namespace PGPM.Tests;

/// <summary>
/// The app in memory, on the run's PostgreSQL database (TestDefaults), in Development, with the UI build's stand-in as
/// wwwroot. <c>settings</c> are configuration values for this app; <c>services</c> replaces services for the test.
/// <para>
/// Two ways to be signed in (PNMTD): <see cref="TestSignIn"/> by headers (<see cref="Client(string?)"/>,
/// <see cref="ClientWithRights"/>) – the cookie scheme forwards a request that carries them – and the real cookie session
/// (<see cref="Browser"/>). The login rate limit is out of the way unless a test sets it.
/// </para>
/// </summary>
public sealed class TestApp(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting(WebHostDefaults.WebRootKey, TestDefaults.WebRoot);
        builder.UseSetting("Auth:LoginRateLimit:PermitLimit", "100000");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestSignIn>(TestSignIn.SchemeName, null);
            // Authenticate, challenge and forbid go to the test scheme for a request with its header; signing in and out stay
            // the cookie scheme's own (a forward to itself is none).
            s.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
            {
                o.ForwardDefaultSelector = c => c.Request.Headers.ContainsKey(TestSignIn.UserHeader) || c.Request.Headers.ContainsKey(TestSignIn.UserIdHeader)
                ? TestSignIn.SchemeName : null;
                o.ForwardSignIn = CookieAuthenticationDefaults.AuthenticationScheme;
                o.ForwardSignOut = CookieAuthenticationDefaults.AuthenticationScheme;
            });
        });
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }

    /// <summary>A client signed in as a test user (<see cref="TestUsers"/>' names), or signed out. Never follows redirects.</summary>
    public HttpClient Client(string? user = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(TestSignIn.UserHeader, user);
        }
        return client;
    }

    /// <summary>A client signed in as any user of the database (one a test created), with exactly these rights.</summary>
    public HttpClient ClientFor(Guid userId, string rights = "")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestSignIn.UserIdHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestSignIn.RightsHeader, rights);
        return client;
    }

    /// <summary>A client signed in as the plain test user with exactly these rights ("Projects:Create,Maps:Create", "admin", "").</summary>
    public HttpClient ClientWithRights(string rights, string? name = null)
    {
        var client = Client(TestUsers.Plain);
        client.DefaultRequestHeaders.Add(TestSignIn.RightsHeader, rights);
        client.DefaultRequestHeaders.Add(TestSignIn.NameHeader, name ?? $"rights-{Guid.NewGuid():N}");
        return client;
    }

    public PgpmDbContext NewDb() => Services.GetRequiredService<IDbContextFactory<PgpmDbContext>>().CreateDbContext();

    /// <summary>The audit entries by or about a user, oldest first, as "event:summary".</summary>
    public async Task<List<string>> AuditAsync(Guid userId)
    {
        await using var db = NewDb();
        var entries = await db.AuditLog.AsNoTracking().Where(l => l.ActorUserId == userId || l.SubjectId == userId)
            .OrderBy(l => l.At).ThenBy(l => l.Id).ToListAsync();
        return entries.Select(e => $"{e.Event}:{AuditLog.ReadDetail(e.Detail).Summary}").ToList();
    }
}

/// <summary>
/// The seeded test users, once per run (TestDefaults), with <see cref="Password"/>: an admin, a member of Members, a member
/// of Viewers, and one without groups (<see cref="TestApp.ClientWithRights"/>'s).
/// </summary>
public static class TestUsers
{
    public const string Password = "test-password-1";

    public const string Admin = "test-admin";
    public const string Member = "test-member";
    public const string Viewer = "test-viewer";
    public const string Plain = "test-plain";

    public static readonly IReadOnlyDictionary<string, Guid> Ids = new Dictionary<string, Guid>
    {
        [Admin] = new("01970000-0000-7000-8000-00000000a001"),
        [Member] = new("01970000-0000-7000-8000-00000000a002"),
        [Viewer] = new("01970000-0000-7000-8000-00000000a003"),
        [Plain] = new("01970000-0000-7000-8000-00000000a004"),
    };

    public static Guid IdOf(string user) => Ids[user];

    /// <summary>What the test sign-in gives each one without an explicit rights header: their groups' rights.</summary>
    public static UserRights RightsOf(string user) => user switch
    {
        Admin => new UserRights(true, new Dictionary<Area, Level>()),
        Member => new UserRights(false, new Dictionary<Area, Level>
        {
            [Area.Notes] = Level.Create, [Area.Ideas] = Level.Create, [Area.Leads] = Level.Create, [Area.Projects] = Level.Create,
            [Area.Maps] = Level.Create, [Area.Mcp] = Level.Use,
        }),
        _ => UserRights.None,
    };

    /// <summary>For services outside an app: Data Protection keys in memory, for the run.</summary>
    internal static readonly TotpSecrets TotpSecrets = new(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

    internal static void Seed(PgpmDbContext db)
    {
        var hash = Lib.Security.PasswordHasher.Hash(Password);
        foreach (var (name, id) in Ids)
        {
            var user = new UserEntity
            {
                Id = id,
                Username = name,
                NormalizedUsername = name,
                DisplayName = $"Test {name[5..]}",
                PasswordHash = hash,
                IsAdmin = name == Admin,
                SecurityStamp = UserAccounts.NewStamp(),
                CreatedAt = Lib.Helper.Clock.UtcNow,
            };
            if (name == Member)
            {
                user.Groups.Add(new UserGroupEntity { UserId = id, GroupId = GroupEntity.MembersId });
            }
            if (name == Viewer)
            {
                user.Groups.Add(new UserGroupEntity { UserId = id, GroupId = GroupEntity.ViewersId });
            }
            db.Users.Add(user);
            Persons.UserPersons.CreateManual(db, user);
        }
        db.SaveChanges();
    }

    /// <summary>"Projects:Create,Maps:Create", "admin", or "" (none).</summary>
    public static UserRights Parse(string value)
    {
        if (value == "admin")
        {
            return new UserRights(true, new Dictionary<Area, Level>());
        }
        var levels = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => RightsCatalog.TryParse(v.Trim(), out var a, out var l) ? (a, l) : throw new ArgumentException($"No such right: {v}"));
        return new UserRights(false, UserRights.Combine(levels));
    }
}

/// <summary>
/// Signs a request in as the test user its <see cref="UserHeader"/> names (PNMTD's TestSignIn), with that user's rights or
/// those of <see cref="RightsHeader"/>; without the header the cookie scheme handles the request itself.
/// </summary>
public sealed class TestSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>One of <see cref="TestUsers"/>' names: signs in as that seeded user.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Exactly these rights instead ("Projects:Create,Maps:Create", or "admin", or "" for none).</summary>
    public const string RightsHeader = "X-Test-Rights";

    /// <summary>Another name for the user (the audit entries name it, the rate limits count per name).</summary>
    public const string NameHeader = "X-Test-Name";

    /// <summary>Any user of the database by id (with <see cref="RightsHeader"/>'s rights, none without): the sync tests' own users.</summary>
    public const string UserIdHeader = "X-Test-User-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue(UserIdHeader, out var byId) && Guid.TryParse(byId, out var userId))
        {
            var own = Request.Headers.TryGetValue(RightsHeader, out var ownRights) ? TestUsers.Parse(ownRights.ToString()) : UserRights.None;
            var ownIdentity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, $"user-{userId:N}"),
                .. own.Claims()], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(ownIdentity), SchemeName)));
        }
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrEmpty(user) || !TestUsers.Ids.TryGetValue(user!, out var id))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var rights = Request.Headers.TryGetValue(RightsHeader, out var explicitRights) ? TestUsers.Parse(explicitRights.ToString()) : TestUsers.RightsOf(user!);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()),
                new Claim(ClaimTypes.Name, Request.Headers.TryGetValue(NameHeader, out var name) ? name.ToString() : user!), .. rights.Claims()],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// A client that keeps cookies like a browser and sends the XSRF-TOKEN cookie back in X-XSRF-TOKEN, as the UI does
/// (PNMTD's Browser). The real cookie session.
/// </summary>
public sealed class Browser(TestApp app)
{
    private readonly HttpClient client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    /// <summary>The raw Set-Cookie headers of the last response.</summary>
    public List<string> LastSetCookies { get; } = [];

    public bool SendCsrfToken { get; set; } = true;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ApiJson.Options);
        }
        if (Cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }
        if (SendCsrfToken && Cookies.TryGetValue("XSRF-TOKEN", out var token))
        {
            request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(token));
        }
        var response = await client.SendAsync(request);
        LastSetCookies.Clear();
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in setCookies)
            {
                LastSetCookies.Add(header);
                var pair = header.Split(';')[0];
                var name = pair[..pair.IndexOf('=')];
                var value = pair[(pair.IndexOf('=') + 1)..];
                var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                if (expired || value.Length == 0)
                {
                    Cookies.Remove(name);
                }
                else
                {
                    Cookies[name] = value;
                }
            }
        }
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body ?? new { });

    public async Task<Api.V1.Contracts.LoginResponse> LoginAsync(string username, string password)
    {
        using var response = await PostAsync("/api/v1/auth/login", new { username, password });
        return await response.ReadAsync<Api.V1.Contracts.LoginResponse>();
    }

    /// <summary>The status of a call, the response disposed.</summary>
    public async Task<HttpStatusCode> StatusAsync(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendAsync(method, url, body);
        return response.StatusCode;
    }
}

/// <summary>v1's JSON: camelCase, enums as strings.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
        return JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty body");
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Options);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object body) =>
        client.PutAsJsonAsync(url, body, Options);

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response)
    {
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    public static async Task<string?> CodeAsync(this HttpResponseMessage response) =>
        (await response.ProblemAsync()).GetProperty("code").GetString();
}

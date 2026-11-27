using System.Net;
using System.Text;
using System.Text.Json;

namespace PGPM.Contract.Tests;

/// <summary>
/// One browser's worth of a running PGPM (PGPM_CONTRACT_URL): its cookies, the CSRF header on unsafe calls (the
/// XSRF-TOKEN cookie echoed as X-XSRF-TOKEN, docs/03 §4.3), JSON in and out.
/// </summary>
internal sealed class ApiSession : IDisposable
{
    public static readonly string? BaseUrl = Environment.GetEnvironmentVariable("PGPM_CONTRACT_URL");

    private readonly HttpClientHandler handler;

    public ApiSession()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            Assert.Inconclusive("PGPM_CONTRACT_URL is not set: no running container to test (tools/contract.sh starts one).");
        }
        handler = new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect = false };
        Http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl!), Timeout = TimeSpan.FromSeconds(30) };
    }

    public CookieContainer Cookies { get; } = new();

    public HttpClient Http { get; }

    public Uri BaseAddress => Http.BaseAddress!;

    /// <summary>The page's origin, as a browser sends it with the live channel's upgrade.</summary>
    public string Origin => BaseAddress.GetLeftPart(UriPartial.Authority);

    public Cookie? Cookie(string name) => Cookies.GetCookies(BaseAddress)[name];

    /// <summary>A call answered with <paramref name="expected"/>; its JSON body (an undefined element when there is none).</summary>
    public async Task<JsonElement> CallAsync(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(expected, response.StatusCode, $"{method} {path}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        if (method != HttpMethod.Get && Cookie("XSRF-TOKEN") is { } token)
        {
            request.Headers.Add("X-XSRF-TOKEN", token.Value);
        }
        return await Http.SendAsync(request);
    }

    /// <summary>The local sign-in: its answer (<c>next</c>, <c>me</c>).</summary>
    public Task<JsonElement> SignInAsync(string username, string password) =>
        CallAsync(HttpMethod.Post, "/api/v1/auth/login", new { username, password });

    /// <summary>The forced change of a first (or temporary) password; the session goes on with the new one.</summary>
    public Task ChangePasswordAsync(string current, string next) =>
        CallAsync(HttpMethod.Post, "/api/v1/account/password", new { currentPassword = current, newPassword = next }, HttpStatusCode.NoContent);

    public void Dispose()
    {
        Http.Dispose();
        handler.Dispose();
    }
}

/// <summary>
/// The first admin of the stack (PGPM_CONTRACT_ADMIN_PASSWORD, tools/contract.sh): on the first sign-in everything but
/// the password change answers 403 <c>must_change_password</c>; the password is changed once per run, whichever test
/// needs the admin first, and every later sign-in uses the new one.
/// </summary>
internal static class FirstAdmin
{
    public const string Username = "admin";

    private static readonly string? InitialPassword = Environment.GetEnvironmentVariable("PGPM_CONTRACT_ADMIN_PASSWORD");

    private static readonly Lazy<Task<string>> Changed = new(ChangeAsync);

    /// <summary>The admin's password now (changed from the initial one, the first time this is asked).</summary>
    public static Task<string> PasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiSession.BaseUrl) || string.IsNullOrEmpty(InitialPassword))
        {
            Assert.Inconclusive("PGPM_CONTRACT_URL or PGPM_CONTRACT_ADMIN_PASSWORD is not set (tools/contract.sh sets both).");
        }
        return Changed.Value;
    }

    /// <summary>A session of the admin, signed in with the current password.</summary>
    public static async Task<ApiSession> SignInAsync()
    {
        var password = await PasswordAsync();
        var session = new ApiSession();
        var login = await session.SignInAsync(Username, password);
        Assert.AreEqual("done", login.GetProperty("next").GetString());
        return session;
    }

    private static async Task<string> ChangeAsync()
    {
        using var session = new ApiSession();
        var login = await session.SignInAsync(Username, InitialPassword!);
        Assert.AreEqual("done", login.GetProperty("next").GetString());
        var me = login.GetProperty("me");
        Assert.IsTrue(me.GetProperty("user").GetProperty("isAdmin").GetBoolean());
        if (!me.GetProperty("flags").GetProperty("mustChangePassword").GetBoolean())
        {
            // A deployment whose admin changed the initial password already: that one is the password.
            return InitialPassword!;
        }
        var cookie = session.Cookie("pgpm.auth");
        Assert.IsNotNull(cookie);
        Assert.IsTrue(cookie.HttpOnly);
        using (var held = await session.SendAsync(HttpMethod.Get, "/api/v1/users"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, held.StatusCode);
            StringAssert.Contains(await held.Content.ReadAsStringAsync(), "must_change_password");
        }
        using (var sync = await session.SendAsync(HttpMethod.Post, "/api/v1/sync", new { clientId = Guid.CreateVersion7(), since = 0 }))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, sync.StatusCode, "the sync waits for the password change too");
        }
        var password = $"contract-{Guid.NewGuid():N}";
        await session.ChangePasswordAsync(InitialPassword!, password);
        var after = await session.CallAsync(HttpMethod.Get, "/api/v1/auth/me");
        Assert.IsFalse(after.GetProperty("flags").GetProperty("mustChangePassword").GetBoolean());
        await session.CallAsync(HttpMethod.Get, "/api/v1/users");
        return password;
    }
}

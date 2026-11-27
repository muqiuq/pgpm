using System.Net;
using System.Text.Json;

namespace PGPM.Contract.Tests;

/// <summary>A running PGPM (PGPM_CONTRACT_URL, e.g. http://localhost:7330): healthy, names itself, serves the shell.</summary>
[TestClass]
public sealed class HealthContractTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("PGPM_CONTRACT_URL");

    private static HttpClient Client()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            Assert.Inconclusive("PGPM_CONTRACT_URL is not set: no running container to test (tools/contract.sh starts one).");
        }
        return new HttpClient { BaseAddress = new Uri(BaseUrl!), Timeout = TimeSpan.FromSeconds(30) };
    }

    [TestMethod]
    public async Task TheContainerIsHealthyAndNamesItself()
    {
        using var client = Client();
        using var health = await client.GetAsync("/healthz");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual("ok", await health.Content.ReadAsStringAsync());

        using var info = await client.GetAsync("/api/v1/info");
        Assert.AreEqual(HttpStatusCode.OK, info.StatusCode);
        using var json = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        Assert.AreEqual("PGPM", json.RootElement.GetProperty("name").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(json.RootElement.GetProperty("version").GetString()));
    }

    [TestMethod]
    public async Task TheShellTheServiceWorkerAndTheManifestAreServed()
    {
        using var client = Client();
        using var shell = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, shell.StatusCode);
        StringAssert.Contains(await shell.Content.ReadAsStringAsync(), "<div id=\"app\">");

        using var worker = await client.GetAsync("/sw.js");
        Assert.AreEqual(HttpStatusCode.OK, worker.StatusCode);
        Assert.AreEqual("no-cache", worker.Headers.CacheControl?.ToString());

        using var manifest = await client.GetAsync("/manifest.webmanifest");
        Assert.AreEqual(HttpStatusCode.OK, manifest.StatusCode);
        using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        Assert.AreEqual("PGPM", json.RootElement.GetProperty("short_name").GetString());
    }
}

/// <summary>
/// The sign-in of a running PGPM (docs/05 §5): the first admin (PGPM_CONTRACT_ADMIN_PASSWORD, tools/contract.sh) signs in,
/// must change the password first – the other calls answer 403 until then – and signs out again. The password stays.
/// </summary>
[TestClass]
public sealed class SignInContractTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("PGPM_CONTRACT_URL");
    private static readonly string? AdminPassword = Environment.GetEnvironmentVariable("PGPM_CONTRACT_ADMIN_PASSWORD");

    [TestMethod]
    public async Task TheFirstAdminSignsInAndMustChangeThePasswordFirst()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrEmpty(AdminPassword))
        {
            Assert.Inconclusive("PGPM_CONTRACT_URL or PGPM_CONTRACT_ADMIN_PASSWORD is not set (tools/contract.sh sets both).");
        }
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl!), Timeout = TimeSpan.FromSeconds(30) };

        using (var options = await client.GetAsync("/api/v1/auth/options"))
        {
            Assert.AreEqual(HttpStatusCode.OK, options.StatusCode);
            using var json = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
            Assert.IsTrue(json.RootElement.GetProperty("local").GetBoolean());
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        using var login = await client.PostAsync("/api/v1/auth/login", new StringContent(
            JsonSerializer.Serialize(new { username = "admin", password = AdminPassword }), System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        using (var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("done", json.RootElement.GetProperty("next").GetString());
            var me = json.RootElement.GetProperty("me");
            Assert.IsTrue(me.GetProperty("user").GetProperty("isAdmin").GetBoolean());
            Assert.IsTrue(me.GetProperty("flags").GetProperty("mustChangePassword").GetBoolean());
        }
        var session = cookies.GetCookies(client.BaseAddress!)["pgpm.auth"];
        Assert.IsNotNull(session);
        Assert.IsTrue(session.HttpOnly);

        using (var held = await client.GetAsync("/api/v1/users"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, held.StatusCode);
            StringAssert.Contains(await held.Content.ReadAsStringAsync(), "must_change_password");
        }
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("X-XSRF-TOKEN", cookies.GetCookies(client.BaseAddress!)["XSRF-TOKEN"]!.Value);
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}

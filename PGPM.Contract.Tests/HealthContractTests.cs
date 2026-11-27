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
/// The sign-in of a running PGPM (docs/05 §5): the first admin (PGPM_CONTRACT_ADMIN_PASSWORD, tools/contract.sh) must
/// change the password first – the other calls answer 403 until then (<see cref="FirstAdmin"/>) – then signs in with the
/// new one, may administrate, and signs out again.
/// </summary>
[TestClass]
public sealed class SignInContractTests
{
    [TestMethod]
    public async Task TheFirstAdminChangesThePasswordFirstThenSignsInAndOut()
    {
        var password = await FirstAdmin.PasswordAsync();
        using var client = new ApiSession();
        using (var options = await client.Http.GetAsync("/api/v1/auth/options"))
        {
            Assert.AreEqual(HttpStatusCode.OK, options.StatusCode);
            using var json = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
            Assert.IsTrue(json.RootElement.GetProperty("local").GetBoolean());
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Http.GetAsync("/api/v1/auth/me")).StatusCode);

        var login = await client.SignInAsync(FirstAdmin.Username, password);
        Assert.AreEqual("done", login.GetProperty("next").GetString());
        var me = login.GetProperty("me");
        Assert.IsTrue(me.GetProperty("user").GetProperty("isAdmin").GetBoolean());
        Assert.IsFalse(me.GetProperty("flags").GetProperty("mustChangePassword").GetBoolean());
        var session = client.Cookie("pgpm.auth");
        Assert.IsNotNull(session);
        Assert.IsTrue(session.HttpOnly);
        await client.CallAsync(HttpMethod.Get, "/api/v1/users");

        await client.CallAsync(HttpMethod.Post, "/api/v1/auth/logout", expected: HttpStatusCode.NoContent);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Http.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}

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

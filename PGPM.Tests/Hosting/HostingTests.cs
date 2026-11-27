using System.Net;
using PGPM.Api.V1.Contracts;

namespace PGPM.Tests.Hosting;

/// <summary>
/// The walking skeleton's host (docs/03 §3.1, §11.1): health, the anonymous info, the web UI at / with its deep links and
/// cache headers, the security headers, and deny by default for the API.
/// </summary>
[TestClass]
public sealed class HostingTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task HealthzAnswersOkWhileTheDatabaseDoes()
    {
        using var response = await app.Client().GetAsync("/healthz");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task InfoNamesTheAppAndItsVersionWithoutSignIn()
    {
        using var response = await app.Client().GetAsync("/api/v1/info");
        var info = await response.ReadAsync<AppInfo>();
        Assert.AreEqual("PGPM", info.Name);
        Assert.AreEqual(ServerInfo.Version, info.Version);
        StringAssert.Matches(info.Version, new System.Text.RegularExpressions.Regex(@"^\d+\.\d+\.\d+"));
        Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
        Assert.AreEqual("default-src 'none'; frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/projects")]
    [DataRow("/maps/0195f3c2-7a10-7000-8000-000000000001")]
    [DataRow("/admin/users")]
    public async Task DeepLinksGetIndexHtmlRevalidated(string path)
    {
        using var response = await app.Client().GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(TestDefaults.IndexHtml, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString());
        var csp = Header(response, "Content-Security-Policy");
        StringAssert.StartsWith(csp, "default-src 'self'");
        StringAssert.Contains(csp, "worker-src 'self'");
        StringAssert.Contains(csp, "img-src 'self' blob:;");
        // The live channel's origin, explicitly (older Safari doesn't match ws: against 'self').
        StringAssert.Contains(csp, "connect-src 'self' ws://localhost;");
        StringAssert.Contains(csp, "frame-ancestors 'none'");
        Assert.AreEqual("DENY", Header(response, "X-Frame-Options"));
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [TestMethod]
    public async Task TheServiceWorkerAndTheManifestAreRevalidatedHashedAssetsAreImmutable()
    {
        using var worker = await app.Client().GetAsync("/sw.js");
        Assert.AreEqual(HttpStatusCode.OK, worker.StatusCode);
        Assert.AreEqual("no-cache", worker.Headers.CacheControl?.ToString());
        Assert.AreEqual("text/javascript", worker.Content.Headers.ContentType?.MediaType);

        using var manifest = await app.Client().GetAsync("/manifest.webmanifest");
        Assert.AreEqual(HttpStatusCode.OK, manifest.StatusCode);
        Assert.AreEqual("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("no-cache", manifest.Headers.CacheControl?.ToString());

        using var asset = await app.Client().GetAsync("/assets/index-3f9a.js");
        Assert.AreEqual(HttpStatusCode.OK, asset.StatusCode);
        Assert.AreEqual("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
    }

    [TestMethod]
    public async Task AMissingAssetIsAnEmpty404NotTheUi()
    {
        using var response = await app.Client().GetAsync("/assets/missing-0000.js");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Deny by default: an unknown API path is the fallback policy's 401 for a signed-out caller, as ProblemDetails.</summary>
    [TestMethod]
    [DataRow("/api/v1/nothing-here")]
    [DataRow("/api/nothing")]
    public async Task UnknownApiPathsNeverGetTheUi(string path)
    {
        using var response = await app.Client().GetAsync(path);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.AreEqual("unauthorized", problem.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task APublicUrlNamesTheWebSocketOrigin()
    {
        using var configured = new TestApp(new Dictionary<string, string?> { ["PublicUrl"] = "https://pgpm.example.com" });
        using var response = await configured.Client().GetAsync("/");
        StringAssert.Contains(Header(response, "Content-Security-Policy"), "connect-src 'self' wss://pgpm.example.com;");
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : "";
}

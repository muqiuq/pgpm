using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PGPM.Api.V1;
using PGPM.Auth;

namespace PGPM.Tests.Hosting;

/// <summary>
/// The security review's host checks (docs/06): every controller is a v1 controller (CSRF, ProblemDetails), only the login
/// steps skip CSRF, cookies are Secure and HSTS is sent once PublicUrl is https (S-03, S-04), and an upload that would eat into
/// the disk's reserve is refused (S-06).
/// </summary>
[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    public void EveryControllerHasTheV1BaseAndOnlyTheLoginStepsSkipCsrf()
    {
        var controllers = typeof(Program).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.IsGreaterThan(10, controllers.Count);
        var outside = controllers.Where(t => !t.IsSubclassOf(typeof(Api.V1ControllerBase))).Select(t => t.Name).ToList();
        Assert.AreEqual("", string.Join(", ", outside), "controllers without CSRF protection");
        var skipping = controllers.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<SkipCsrfAttribute>() is not null).Select(m => $"{t.Name}.{m.Name}"))
            .Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(new[] { $"{nameof(AuthController)}.Login", $"{nameof(AuthController)}.LoginTotp" }, skipping);
    }

    [TestMethod]
    public async Task AnHttpsPublicUrlMakesEveryCookieSecureAndSendsHsts()
    {
        using var https = new TestApp(new Dictionary<string, string?> { ["PublicUrl"] = "https://pgpm.example.com" });
        var browser = new Browser(https);
        using (var login = await browser.PostAsync("/api/v1/auth/login", new { username = TestUsers.Member, password = TestUsers.Password }))
        {
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            Assert.AreEqual("max-age=31536000", Header(login, "Strict-Transport-Security"));
        }
        // All but the antiforgery cookie token, which the framework sets only as the request came (it is nothing without the session).
        var cookies = browser.LastSetCookies.Where(c => !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
            && !c.StartsWith(AuthSetup.AntiforgeryCookieName + "=", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(cookies.Count >= 2, string.Join("\n", cookies));
        Assert.IsTrue(cookies.All(c => c.Contains("; secure", StringComparison.OrdinalIgnoreCase)), string.Join("\n", cookies));
        using (var page = await https.Client().GetAsync("/"))
        {
            Assert.AreEqual("max-age=31536000", Header(page, "Strict-Transport-Security"));
        }

        // Without PublicUrl, over plain http (the tests' server): neither.
        using var plain = new TestApp();
        var local = new Browser(plain);
        using (var login = await local.PostAsync("/api/v1/auth/login", new { username = TestUsers.Member, password = TestUsers.Password }))
        {
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            Assert.AreEqual("", Header(login, "Strict-Transport-Security"));
        }
        Assert.IsFalse(local.LastSetCookies.Any(c => c.Contains("; secure", StringComparison.OrdinalIgnoreCase)), string.Join("\n", local.LastSetCookies));
    }

    [TestMethod]
    public async Task AnUploadThatWouldEatIntoTheDisksReserveIsRefused()
    {
        // More reserve than any disk has: the first chunk is refused with 507, before it counts as an upload.
        using var full = new TestApp(new Dictionary<string, string?> { ["Files:MinFreeMb"] = "1000000000" });
        var user = await Sync.SyncKit.UserAsync(full, "diskfull");
        var client = new Sync.SyncClient(full.ClientFor(user, "Notes:Create"));
        var (note, file) = (Lib.Helper.Ids.New(), Lib.Helper.Ids.New());
        await client.SyncAllAsync(
        [
            Sync.SyncKit.Insert("items", note, Sync.SyncKit.Note("Disk")),
            Sync.SyncKit.Insert("files", file, new System.Text.Json.Nodes.JsonObject
            {
                ["itemId"] = note.ToString(), ["purpose"] = "attachment", ["fileName"] = "a.txt", ["contentType"] = "text/plain", ["sizeBytes"] = 3,
                ["sha256"] = new string('e', 64),
            }),
        ]);
        using var chunk = await client.Http.PutAsync($"/api/v1/files/{file}/chunks/0", new ByteArrayContent([1, 2, 3]));
        Assert.AreEqual((HttpStatusCode)507, chunk.StatusCode);
        Assert.AreEqual(Files.FileErrors.StorageFull, await chunk.CodeAsync());
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values) : "";
}

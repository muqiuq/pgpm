using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PGPM.Data;

namespace PGPM.Tests;

/// <summary>
/// The app in memory, on the run's PostgreSQL database (TestDefaults), in Development, with the UI build's stand-in as
/// wwwroot. <c>settings</c> are configuration values for this app; <c>services</c> replaces services for the test.
/// Phase 1 adds PNMTD's TestSignIn scheme here (a request signed in by headers: <c>ClientWithRights("Projects:Create")</c>).
/// </summary>
public sealed class TestApp(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting(WebHostDefaults.WebRootKey, TestDefaults.WebRoot);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }

    /// <summary>A client that never follows redirects (signed out until phase 1).</summary>
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public PgpmDbContext NewDb() => Services.GetRequiredService<IDbContextFactory<PgpmDbContext>>().CreateDbContext();
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

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response)
    {
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}

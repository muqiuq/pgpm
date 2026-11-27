using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PGPM.Api.V1.Contracts;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Lib.Helper;
using PGPM.Mcp;
using PGPM.Tests.Sync;

namespace PGPM.Tests.Mcp;

/// <summary>
/// What the MCP tests share (PNMTD's McpServerTests helpers): users of their own in real groups (a token's rights come from
/// the database, not from the test sign-in), tokens switched on through the account API as the MCP card does, the SDK's own
/// client over HTTP against the test server, and raw requests for what a client never sends.
/// </summary>
internal static class McpKit
{
    /// <summary>A user in Members (Notes … Projects: Create, Maps: Create, MCP: Use) and <paramref name="groups"/>.</summary>
    public static Task<Guid> MemberAsync(TestApp app, string prefix, params Guid[] groups) =>
        SyncKit.UserAsync(app, prefix, [GroupEntity.MembersId, .. groups]);

    /// <summary>A group with exactly these rights.</summary>
    public static async Task<Guid> GroupWithRightsAsync(TestApp app, string prefix, params (Area Area, Level Level)[] rights)
    {
        var group = await SyncKit.GroupAsync(app, prefix);
        await using var db = app.NewDb();
        foreach (var (area, level) in rights)
        {
            db.GroupRights.Add(new GroupRightEntity { GroupId = group, Area = area, Level = level });
        }
        await db.SaveChangesAsync();
        return group;
    }

    /// <summary>The account API as the user (the test sign-in; the rights header only answers the endpoints' own checks).</summary>
    public static HttpClient Account(TestApp app, Guid user, string rights = "Mcp:Use") => app.ClientFor(user, rights);

    /// <summary>TOTP on, as far as the token's rule goes (the tests sign in by header, never with a code).</summary>
    public static async Task SecondFactorAsync(TestApp app, Guid user)
    {
        await using var db = app.NewDb();
        await db.Users.Where(u => u.Id == user).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsTotpEnabled, true));
    }

    public static async Task<McpTokenIssued> TurnOnAsync(TestApp app, Guid user, string kind)
    {
        using var response = await Account(app, user).PostAsync($"/api/v1/account/mcp/{kind}/on", null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
        return await response.ReadAsync<McpTokenIssued>();
    }

    /// <summary>A member with a second factor and both tokens on: (user, read value, write value).</summary>
    public static async Task<(Guid User, string Read, string Write)> WriterAsync(TestApp app, string prefix, params Guid[] groups)
    {
        var user = await MemberAsync(app, prefix, groups);
        await SecondFactorAsync(app, user);
        var read = (await TurnOnAsync(app, user, "read")).Value!;
        var write = (await TurnOnAsync(app, user, "write")).Value!;
        return (user, read, write);
    }

    public static async Task<McpAccountItem> AccountAsync(TestApp app, Guid user) =>
        await (await Account(app, user).GetAsync("/api/v1/account/mcp")).ReadAsync<McpAccountItem>();

    public static async Task<McpClient> ClientAsync(TestApp app, string token)
    {
        var http = app.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, McpSetup.Path),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, http, NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    public static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    public static JsonElement Json(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement.Clone();

    public static async Task<CallToolResult> CallAsync(McpClient client, string tool, object? args = null)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args ?? new { }))!;
        return await client.CallToolAsync(tool, dictionary);
    }

    public static async Task<JsonElement> OkAsync(McpClient client, string tool, object? args = null)
    {
        var result = await CallAsync(client, tool, args);
        Assert.IsFalse(result.IsError ?? false, $"{tool}: {Text(result)}");
        return Json(result);
    }

    /// <summary>The error code of a refused call.</summary>
    public static async Task<JsonElement> ErrorAsync(McpClient client, string tool, object? args = null)
    {
        var result = await CallAsync(client, tool, args);
        Assert.IsTrue(result.IsError ?? false, $"{tool} should fail: {Text(result)}");
        return Json(result);
    }

    /// <summary>A raw tools/list (no initialize): what the token handler and the origin check answer.</summary>
    public static async Task<HttpResponseMessage> RawAsync(TestApp app, string? token, string? origin = null, HttpClient? http = null)
    {
        http ??= app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, McpSetup.Path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return await http.SendAsync(request);
    }

    public static async Task<string?> RefusalAsync(TestApp app, string? token)
    {
        using var response = await RawAsync(app, token);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            ? JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()
            : null;
    }

    /// <summary>A note the user created through the sync, as the PWA does; shared with these groups (edit).</summary>
    public static async Task<Guid> NoteAsync(TestApp app, Guid user, string title, params Guid[] groups)
    {
        var id = Ids.New();
        var batch = Ids.New();
        var client = new SyncClient(app.ClientFor(user, "Notes:Create"));
        var answer = await client.SyncAsync([SyncKit.Insert("items", id, SyncKit.Note(title), batch),
            .. groups.Select(g => SyncKit.Insert("item_shares", Ids.New(), SyncKit.Share(id, g, "edit"), batch))], pull: false);
        Assert.IsTrue(answer.Arr("results").All(r => r.Str("status") == "accepted"), answer.ToJsonString());
        return id;
    }
}

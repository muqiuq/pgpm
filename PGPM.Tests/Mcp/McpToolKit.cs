using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PGPM.Lib.Helper;
using PGPM.Tests.Sync;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Mcp;

/// <summary>
/// The content the tool tests start from, made as the PWA makes it – through the sync (items and their shares in one batch,
/// maps with a layer, field values) and the file endpoints (bytes uploaded in a chunk and committed) – and the answers' parts.
/// </summary>
internal static class McpToolKit
{
    public const string Everything = "Notes:Create,Ideas:Create,Leads:Create,Projects:Create,Maps:Create";

    /// <summary>The mutations through the user's sync, every one accepted.</summary>
    public static async Task<JsonObject> SyncAsync(TestApp app, Guid user, params JsonObject[] mutations) =>
        await SyncWithAsync(new SyncClient(app.ClientFor(user, Everything)), mutations);

    /// <summary>As an admin (sees and edits every section and item).</summary>
    public static async Task<JsonObject> AdminSyncAsync(TestApp app, params JsonObject[] mutations) =>
        await SyncWithAsync(new SyncClient(app.ClientWithRights("admin")), mutations);

    private static async Task<JsonObject> SyncWithAsync(SyncClient client, JsonObject[] mutations)
    {
        var answer = await client.SyncAsync(mutations, pull: false);
        Assert.IsTrue(answer.Arr("results").All(r => r.Str("status") == "accepted"), answer.ToJsonString());
        return answer;
    }

    /// <summary>An item of the user's, shared with these groups (edit, or the given level).</summary>
    public static async Task<Guid> ItemAsync(TestApp app, Guid user, string type, string title, Guid? status, string access = "edit", params Guid[] groups)
    {
        var id = Ids.New();
        var batch = Ids.New();
        await SyncAsync(app, user, [Insert("items", id, Content.ContentKit.Item(type, title, status), batch),
            .. groups.Select(g => Insert("item_shares", Ids.New(), Share(id, g, access), batch))]);
        return id;
    }

    /// <summary>A map of the user's with one layer, shared with these groups: (map, layer).</summary>
    public static async Task<(Guid Map, Guid Layer)> MapAsync(TestApp app, Guid user, string name, JsonObject? matrix = null, string access = "edit",
        params Guid[] groups)
    {
        var (map, layer, batch) = (Ids.New(), Ids.New(), Ids.New());
        await SyncAsync(app, user,
        [
            Insert("maps", map, new JsonObject
            {
                ["kind"] = matrix is null ? "canvas" : "matrix", ["name"] = name, ["settings"] = new JsonObject(), ["matrix"] = matrix, ["isTemplate"] = false,
            }, batch),
            .. groups.Select(g => Insert("map_shares", Ids.New(), new JsonObject { ["mapId"] = map.ToString(), ["groupId"] = g.ToString(), ["access"] = access }, batch)),
            Insert("map_layers", layer, new JsonObject
            {
                ["mapId"] = map.ToString(), ["name"] = "Base", ["sortOrder"] = "a0", ["isVisible"] = true, ["isLocked"] = false,
            }, batch),
        ]);
        return (map, layer);
    }

    public static JsonObject Element(Guid map, Guid layer, string kind, double x, double y, JsonObject? more = null)
    {
        var row = new JsonObject
        {
            ["mapId"] = map.ToString(), ["layerId"] = layer.ToString(), ["kind"] = kind, ["z"] = "a0", ["x"] = x, ["y"] = y, ["width"] = 100, ["height"] = 50,
            ["style"] = new JsonObject(),
        };
        foreach (var (key, value) in more ?? [])
        {
            row[key] = value?.DeepClone();
        }
        return row;
    }

    /// <summary>The insert of a field value (a mutation).</summary>
    public static JsonObject Value(Guid item, Guid field, JsonNode? value) => Insert("field_values", Ids.New(), new JsonObject
    {
        ["itemId"] = item.ToString(), ["fieldId"] = field.ToString(), ["value"] = value,
    });

    /// <summary>A file of the item, its bytes uploaded and committed (available): the row's id.</summary>
    public static async Task<Guid> UploadAsync(TestApp app, Guid user, Guid item, string purpose, string name, string contentType, byte[] bytes)
    {
        var id = Ids.New();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await SyncAsync(app, user, Insert("files", id, new JsonObject
        {
            ["itemId"] = item.ToString(), ["purpose"] = purpose, ["fileName"] = name, ["contentType"] = contentType, ["sizeBytes"] = bytes.Length, ["sha256"] = sha,
        }));
        var http = app.ClientFor(user, Everything);
        using (var chunk = await http.PutAsync($"/api/v1/files/{id}/chunks/0",
            new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }))
        {
            Assert.AreEqual(HttpStatusCode.OK, chunk.StatusCode, await chunk.Content.ReadAsStringAsync());
        }
        using var commit = await http.PostJsonNodeAsync($"/api/v1/files/{id}/commit", new JsonObject { ["sha256"] = sha, ["size"] = bytes.Length });
        Assert.AreEqual(HttpStatusCode.OK, commit.StatusCode, await commit.Content.ReadAsStringAsync());
        return id;
    }

    public static IEnumerable<JsonElement> Each(this JsonElement json, string name) => json.GetProperty(name).EnumerateArray();

    public static string? S(this JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>No such member, or null (the answers leave nulls out where they can).</summary>
    public static bool Missing(this JsonElement json, string name) => !json.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    public static string Code(this JsonElement error) => error.GetProperty("error").GetString()!;

    /// <summary>A field of get_item's sections by key, if the answer has it.</summary>
    public static JsonElement? FieldOf(JsonElement item, string key) =>
        item.TryGetProperty("sections", out var sections)
            ? sections.EnumerateArray().SelectMany(s => s.Each("fields")).Cast<JsonElement?>().FirstOrDefault(f => f!.Value.S("key") == key)
            : null;
}

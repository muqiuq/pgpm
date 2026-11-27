using System.Net;
using System.Text.Json;
using static PGPM.Contract.Tests.ContractKit;

namespace PGPM.Contract.Tests;

/// <summary>
/// A map's sharing round trip of a running PGPM (docs/03 §3.3, §13.7), as the phase-5 client (local/maps.ts, MapView,
/// ShareDialog) does it: Ana creates a canvas map with its layer and its share in one batch; Ben (same group) gets it with
/// <c>edit</c>, draws on it; both write the preview from what they had seen – no conflict, the later one stays; a share
/// inserted later is refused (sharing goes through the endpoint); <c>PUT /api/v1/maps/{id}/shares</c> with no groups
/// answers the shares and evicts the map from Ben's device (<c>kind: "map"</c>).
/// </summary>
[TestClass]
public sealed class MapsContractTests
{
    private static object MapPatch(string table, string id, string key, object? old, object? @new) => new
    {
        mutationId = Guid.CreateVersion7(), batch = Guid.CreateVersion7(), table, id, op = "patch", baseRev = 1,
        changes = new Dictionary<string, object?> { [key] = new { old, @new } }, row = (object?)null, clientChangedAt = DateTime.UtcNow,
    };

    private static string Status(JsonElement answer, int index = 0)
    {
        var result = answer.GetProperty("results")[index];
        return result.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : result.GetProperty("status").GetString()!;
    }

    [TestMethod]
    public async Task AMapIsSharedCoEditedAndUnsharedWithEvictions()
    {
        using var admin = await FirstAdmin.SignInAsync();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var groupId = await GroupAsync(admin, $"Contract mappers {stamp}", new() { ["Maps"] = "Create" });
        using var ana = await UserAsync(admin, $"ana-map-{stamp}", $"Ana {stamp}", groupId);
        using var ben = await UserAsync(admin, $"ben-map-{stamp}", $"Ben {stamp}", groupId);
        var anaDevice = new Device(ana);
        var benDevice = new Device(ben);
        await anaDevice.SyncAsync();
        await benDevice.SyncAsync();

        // The map, its layer and its share: one batch.
        var batch = Guid.CreateVersion7();
        var (map, layer) = (Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString());
        var created = await anaDevice.SyncAsync(
        [
            Insert(batch, "maps", map, new
            {
                kind = "canvas", name = $"Contract map {stamp}", description = (string?)null, settings = new { grid = true, snap = false, gridSize = 20 },
                matrix = (object?)null, isTemplate = false,
            }),
            Insert(batch, "map_layers", layer, new { mapId = map, name = "Base", sortOrder = "a0", isVisible = true, isLocked = false }),
            Insert(batch, "map_shares", Guid.CreateVersion7().ToString(), new { mapId = map, groupId, access = "edit" }),
        ]);
        CollectionAssert.AreEqual(new[] { "accepted", "accepted", "accepted" },
            created.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToArray(), created.ToString());
        var stored = Rows(created, "maps").Single(m => m.GetProperty("id").GetString() == map);
        Assert.AreEqual("manage", stored.GetProperty("access").GetString());
        Assert.AreEqual(ana.UserId, stored.GetProperty("ownerUserId").GetString());

        // Ben gets it (edit) and draws on it.
        var arrived = await benDevice.SyncAsync();
        Assert.AreEqual("edit", Rows(arrived, "maps").Single(m => m.GetProperty("id").GetString() == map).GetProperty("access").GetString());
        var rect = Guid.CreateVersion7().ToString();
        var drawn = await benDevice.SyncAsync(
        [
            Insert(Guid.CreateVersion7(), "map_elements", rect, new
            {
                mapId = map, layerId = layer, kind = "rect", z = "a0", x = 10, y = 20, width = 120, height = 80, style = new { fill = "pink" },
            }),
        ]);
        Assert.AreEqual("accepted", Status(drawn));
        Assert.IsTrue(Rows(await anaDevice.SyncAsync(), "map_elements").Any(e => e.GetProperty("id").GetString() == rect));

        // Both leave the map: each writes a preview over the none it had seen – the later one stays, nobody has a conflict.
        var (first, second) = (Convert.ToBase64String([0x52, 0x49, 0x46, 0x46, 1]), Convert.ToBase64String([0x52, 0x49, 0x46, 0x46, 2]));
        var anaPreview = await anaDevice.SyncAsync([MapPatch("maps", map, "preview", null, first)]);
        Assert.AreEqual("accepted", Status(anaPreview));
        var benPreview = await benDevice.SyncAsync([MapPatch("maps", map, "preview", null, second)]);
        Assert.AreEqual("accepted", Status(benPreview), benPreview.ToString());
        Assert.AreEqual(0, benPreview.GetProperty("conflicts").GetArrayLength());
        Assert.AreEqual(second, Rows(await anaDevice.SyncAsync(), "maps").Single(m => m.GetProperty("id").GetString() == map)
            .GetProperty("preview").GetString(), "base64 on the wire");

        // A share outside the map's batch is refused: later sharing goes through the endpoint.
        var late = await anaDevice.SyncAsync([Insert(Guid.CreateVersion7(), "map_shares", Guid.CreateVersion7().ToString(),
            new { mapId = map, groupId, access = "read" })]);
        Assert.AreEqual("rejected", late.GetProperty("results")[0].GetProperty("status").GetString());

        // Ben can't share (manage); Ana unshares: the answer lists no shares, Ben's device evicts the map.
        using (var refused = await ben.SendAsync(HttpMethod.Put, $"/api/v1/maps/{map}/shares", new[] { new { groupId, access = "manage" } }))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        var shares = await ana.CallAsync(HttpMethod.Put, $"/api/v1/maps/{map}/shares", Array.Empty<object>());
        Assert.AreEqual(0, shares.GetArrayLength());
        var evicted = await benDevice.SyncAsync();
        Assert.IsTrue(evicted.GetProperty("evictions").EnumerateArray()
            .Any(e => e.GetProperty("kind").GetString() == "map" && e.GetProperty("id").GetString() == map), evicted.ToString());

        // Shared again with read: the answer names the group; Ben gets the map back, read-only.
        var again = await ana.CallAsync(HttpMethod.Put, $"/api/v1/maps/{map}/shares", new[] { new { groupId, access = "read" } });
        Assert.AreEqual($"Contract mappers {stamp}", again[0].GetProperty("groupName").GetString());
        Assert.AreEqual("read", again[0].GetProperty("access").GetString());
        var back = await benDevice.SyncAsync();
        Assert.AreEqual("read", Rows(back, "maps").Single(m => m.GetProperty("id").GetString() == map).GetProperty("access").GetString());
        Assert.IsTrue(Rows(back, "map_elements").Any(e => e.GetProperty("id").GetString() == rect), "the re-stamped subtree");
    }
}

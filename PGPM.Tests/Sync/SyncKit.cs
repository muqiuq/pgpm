using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Lib.Helper;

namespace PGPM.Tests.Sync;

/// <summary>
/// What the sync tests share: users and groups of their own (the database is the run's, other classes write to it at the
/// same time), mutations as the client's outbox has them, and a client that keeps the server's rows like the PWA.
/// </summary>
internal static class SyncKit
{
    /// <summary>A new user (and its person) in <paramref name="groups"/>.</summary>
    public static async Task<Guid> UserAsync(TestApp app, string prefix, params Guid[] groups)
    {
        await using var db = app.NewDb();
        var name = $"{prefix}-{Guid.NewGuid():N}";
        var (user, _) = await new UserAccounts(db).CreateAsync(name.Length > 40 ? name[..40] : name, $"{prefix} {Guid.NewGuid().ToString()[..6]}",
            null, false, groups, null, AuditWho.Seed);
        return user.Id;
    }

    public static async Task<Guid> GroupAsync(TestApp app, string prefix)
    {
        await using var db = app.NewDb();
        var name = $"{prefix} {Guid.NewGuid():N}";
        var group = new GroupEntity { Id = Ids.New(), Name = name, NormalizedName = GroupEntity.Normalize(name) };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    public static async Task<string> DisplayNameAsync(TestApp app, Guid user)
    {
        await using var db = app.NewDb();
        return await db.Users.Where(u => u.Id == user).Select(u => u.DisplayName).SingleAsync();
    }

    public static JsonObject Insert(string table, Guid id, JsonObject row, Guid? batch = null)
    {
        row["id"] = id.ToString();
        return new JsonObject
        {
            ["mutationId"] = Ids.New().ToString(), ["batch"] = (batch ?? Ids.New()).ToString(), ["table"] = table, ["id"] = id.ToString(),
            ["op"] = "insert", ["baseRev"] = null, ["changes"] = null, ["row"] = row, ["clientChangedAt"] = Clock.UtcNow,
        };
    }

    public static JsonObject Note(string title, string? description = null) => new()
    {
        ["type"] = "note", ["title"] = title, ["description"] = description, ["pinned"] = false, ["tags"] = new JsonArray(),
    };

    public static JsonObject Share(Guid itemId, Guid groupId, string access) => new()
    {
        ["itemId"] = itemId.ToString(), ["groupId"] = groupId.ToString(), ["access"] = access,
    };

    public static JsonObject Patch(string table, Guid id, int baseRev, params (string Key, JsonNode? Old, JsonNode? New)[] changes)
    {
        var map = new JsonObject();
        foreach (var (key, old, @new) in changes)
        {
            map[key] = new JsonObject { ["old"] = old?.DeepClone(), ["new"] = @new?.DeepClone() };
        }
        return new JsonObject
        {
            ["mutationId"] = Ids.New().ToString(), ["batch"] = Ids.New().ToString(), ["table"] = table, ["id"] = id.ToString(), ["op"] = "patch",
            ["baseRev"] = baseRev, ["changes"] = map, ["row"] = null, ["clientChangedAt"] = Clock.UtcNow,
        };
    }

    public static JsonObject Delete(string table, Guid id, int? baseRev, bool force = false) => new()
    {
        ["mutationId"] = Ids.New().ToString(), ["batch"] = Ids.New().ToString(), ["table"] = table, ["id"] = id.ToString(), ["op"] = "delete",
        ["baseRev"] = baseRev, ["changes"] = null, ["row"] = null, ["clientChangedAt"] = Clock.UtcNow, ["force"] = force,
    };

    public static string? Str(this JsonNode? node, string key) => node?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static long Long(this JsonNode? node, string key) => node![key]!.GetValue<long>();

    public static int Int(this JsonNode? node, string key) => node![key]!.GetValue<int>();

    public static bool Bool(this JsonNode? node, string key) => node?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    public static JsonArray Arr(this JsonNode? node, string key) => node?[key] as JsonArray ?? [];

    public static async Task<JsonObject> JsonAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
        return JsonNode.Parse(body)!.AsObject();
    }
}

/// <summary>
/// A device as the PWA is one: its client id, cursor and access epoch, and the server's rows by table (the local stores
/// without pending changes). Applies a pull as docs/03 §11.7 says: a reset wipes, evictions first, tombstones delete,
/// rows replace when their rev is not lower.
/// </summary>
internal sealed class SyncClient(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public Guid ClientId { get; } = Ids.New();

    public long Cursor { get; set; }

    public int? AccessEpoch { get; set; }

    public Dictionary<string, Dictionary<Guid, JsonObject>> Store { get; } = new(StringComparer.Ordinal);

    public int Resets { get; private set; }

    public JsonObject? Row(string table, Guid id) => Store.TryGetValue(table, out var rows) ? rows.GetValueOrDefault(id) : null;

    public IEnumerable<JsonObject> Rows(string table) => Store.TryGetValue(table, out var rows) ? rows.Values : [];

    /// <summary>One request; the answer applied (unless <paramref name="apply"/> is false).</summary>
    public async Task<JsonObject> SyncAsync(IEnumerable<JsonObject>? mutations = null, bool pull = true, int? limit = null,
        IEnumerable<JsonObject>? resolved = null, bool apply = true)
    {
        var body = new JsonObject
        {
            ["clientId"] = ClientId.ToString(), ["appVersion"] = "test", ["since"] = Cursor, ["accessEpoch"] = AccessEpoch, ["limit"] = limit,
            ["mutations"] = new JsonArray([.. (mutations ?? []).Select(m => (JsonNode)m.DeepClone())]),
            ["resolved"] = new JsonArray([.. (resolved ?? []).Select(r => (JsonNode)r.DeepClone())]),
            ["pull"] = pull,
        };
        using var response = await Http.PostAsync("/api/v1/sync", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var answer = await response.JsonAsync();
        if (apply)
        {
            Apply(answer);
        }
        return answer;
    }

    /// <summary>Push these, then pull until nothing more comes (a reset pulls from 0).</summary>
    public async Task<JsonObject> SyncAllAsync(IEnumerable<JsonObject>? mutations = null, int? limit = null, IEnumerable<JsonObject>? resolved = null)
    {
        var first = await SyncAsync(mutations, limit: limit, resolved: resolved);
        var answer = first;
        for (var i = 0; i < 1000 && (answer.Bool("more") || answer.Bool("reset")); i++)
        {
            answer = await SyncAsync(limit: limit);
        }
        return first;
    }

    public void Apply(JsonObject answer)
    {
        AccessEpoch = answer.Int("accessEpoch");
        if (answer.Bool("reset"))
        {
            Store.Clear();
            Cursor = 0;
            Resets++;
            return;
        }
        ApplyRows(answer["changes"]!.AsObject(), answer.Arr("evictions"));
        Cursor = answer.Long("cursor");
    }

    public void ApplyRows(JsonObject changes, JsonArray evictions)
    {
        foreach (var eviction in evictions)
        {
            var id = Guid.Parse(eviction.Str("id")!);
            Store.GetValueOrDefault("items")?.Remove(id);
            foreach (var (_, rows) in Store)
            {
                foreach (var child in rows.Values.Where(r => r.Str("itemId") == id.ToString()).ToList())
                {
                    rows.Remove(Guid.Parse(child.Str("id")!));
                }
            }
        }
        foreach (var (table, rows) in changes)
        {
            var store = Store.TryGetValue(table, out var s) ? s : Store[table] = [];
            foreach (var row in rows!.AsArray())
            {
                var id = Guid.Parse(row.Str("id")!);
                if (row.Bool("deleted"))
                {
                    store.Remove(id);
                }
                else if (!store.TryGetValue(id, out var local) || row.Int("rev") >= local.Int("rev"))
                {
                    store[id] = row!.DeepClone().AsObject();
                }
            }
        }
    }
}

/// <summary>A live channel's client end (docs/03 §11.4): messages and the close status, read in the background.</summary>
internal sealed class LiveSocket : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly Channel<JsonObject> messages = Channel.CreateUnbounded<JsonObject>();
    private readonly Task reading;

    private LiveSocket(WebSocket socket)
    {
        this.socket = socket;
        reading = ReadAsync();
    }

    public WebSocketCloseStatus? CloseStatus => socket.CloseStatus;

    public bool IsClosed { get; private set; }

    public static async Task<LiveSocket> ConnectAsync(TestApp app, Guid userId, string rights = "", string? origin = "http://localhost")
    {
        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            if (origin is not null)
            {
                r.Headers.Origin = origin;
            }
            r.Headers[TestSignIn.UserIdHeader] = userId.ToString();
            r.Headers[TestSignIn.RightsHeader] = rights;
        };
        return new LiveSocket(await client.ConnectAsync(new Uri("ws://localhost/api/v1/sync/live"), CancellationToken.None));
    }

    /// <summary>A hello with the client's cursor and epoch; the answer is <c>welcome</c> or <c>reset</c>.</summary>
    public async Task<JsonObject> HelloAsync(SyncClient client)
    {
        await SendAsync(new JsonObject
        {
            ["type"] = "hello", ["clientId"] = client.ClientId.ToString(), ["since"] = client.Cursor, ["accessEpoch"] = client.AccessEpoch,
            ["appVersion"] = "test",
        });
        return await NextAsync(TimeSpan.FromSeconds(10)) ?? throw new AssertFailedException("No answer to hello.");
    }

    public Task SendAsync(JsonObject message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Acks a <c>changes</c> message; a socket the server is closing (a shutdown) is not an error here.</summary>
    public async Task AckAsync(JsonObject changes)
    {
        try
        {
            await SendAsync(new JsonObject { ["type"] = "ack", ["cursor"] = changes.Long("cursor") });
        }
        catch (Exception e) when (e is ObjectDisposedException or WebSocketException)
        {
            // The server closed the socket while the message was read: nothing left to ack.
        }
    }

    /// <summary>The next message, or null after <paramref name="timeout"/> (or when the channel closed and nothing is left).</summary>
    public async Task<JsonObject?> NextAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await messages.Reader.ReadAsync(cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Messages until one matches (acking every <c>changes</c> on the way when <paramref name="ack"/>); null on timeout.</summary>
    public async Task<JsonObject?> WaitForAsync(Func<JsonObject, bool> match, TimeSpan timeout, bool ack = true)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var message = await NextAsync(until - DateTime.UtcNow);
            if (message is null)
            {
                return null;
            }
            if (match(message))
            {
                return message;
            }
            if (ack && message.Str("type") == "changes")
            {
                await AckAsync(message);
            }
        }
        return null;
    }

    public async Task<WebSocketCloseStatus?> ClosedAsync(TimeSpan timeout)
    {
        await Task.WhenAny(reading, Task.Delay(timeout));
        return IsClosed ? socket.CloseStatus : null;
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    messages.Writer.TryWrite(JsonNode.Parse(message.ToArray())!.AsObject());
                    message.SetLength(0);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // The server went away.
        }
        IsClosed = true;
        messages.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            await Task.WhenAny(reading, Task.Delay(2000));
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Gone already.
        }
        socket.Dispose();
    }
}

internal static class HttpJson
{
    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, JsonNode body) =>
        client.PutAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> PostJsonNodeAsync(this HttpClient client, string url, JsonNode body) =>
        client.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> DeleteJsonAsync(this HttpClient client, string url, JsonNode body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });

    public static Task<HttpResponseMessage> PostEmptyAsync(this HttpClient client, string url) => client.PostAsJsonAsync(url, new { });
}

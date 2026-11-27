using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PGPM.Contract.Tests;

/// <summary>
/// What the contract tests share: users the admin creates (signed in, the temporary password changed), their devices (client
/// id, cursor, access epoch – as the local database keeps them), outbox mutations as the client sends them, the live
/// channel's messages, and ProblemDetails codes.
/// </summary>
internal static class ContractKit
{
    /// <summary>A user created by the admin in the group, signed in, the temporary password changed.</summary>
    internal static async Task<User> UserAsync(ApiSession admin, string username, string displayName, string groupId)
    {
        var created = await admin.CallAsync(HttpMethod.Post, "/api/v1/users", new { username, displayName, groupIds = new[] { groupId } },
            HttpStatusCode.Created);
        var temporary = created.GetProperty("temporaryPassword").GetProperty("password").GetString()!;
        var user = new User(created.GetProperty("user").GetProperty("id").GetString()!);
        var login = await user.SignInAsync(username, temporary);
        Assert.IsTrue(login.GetProperty("me").GetProperty("flags").GetProperty("mustChangePassword").GetBoolean());
        await user.ChangePasswordAsync(temporary, $"contract-{Guid.NewGuid():N}");
        return user;
    }

    internal static IEnumerable<JsonElement> Rows(JsonElement response, string table) =>
        response.GetProperty("changes").TryGetProperty(table, out var rows) ? rows.EnumerateArray() : [];

    internal static object Insert(Guid batch, string table, string id, object row) => new
    {
        mutationId = Guid.CreateVersion7(), batch, table, id, op = "insert", baseRev = (int?)null, changes = (object?)null, row,
        clientChangedAt = DateTime.UtcNow,
    };

    internal static object Patch(string id, int baseRev, string key, object? old, object? @new) => new
    {
        mutationId = Guid.CreateVersion7(), batch = Guid.CreateVersion7(), table = "items", id, op = "patch", baseRev,
        changes = new Dictionary<string, object?> { [key] = new { old, @new } }, row = (object?)null, clientChangedAt = DateTime.UtcNow,
    };

    internal static Task SendAsync(ClientWebSocket socket, object message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next message of the type (others – the server's ping, editing notices – are answered or skipped); 15 s at most.</summary>
    internal static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.AreNotEqual(WebSocketMessageType.Close, result.MessageType, $"closed {socket.CloseStatus} {socket.CloseStatusDescription}");
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            var json = JsonDocument.Parse(message.ToArray()).RootElement.Clone();
            var got = json.GetProperty("type").GetString();
            if (got == type)
            {
                return json;
            }
            if (got == "ping")
            {
                await SendAsync(socket, new { type = "pong" });
            }
        }
    }

    internal sealed class User(string userId) : IDisposable
    {
        private readonly ApiSession session = new();

        public string UserId { get; } = userId;

        public string Origin => session.Origin;

        public Uri LiveUrl => new(session.BaseAddress.ToString().Replace("http", "ws", StringComparison.Ordinal).TrimEnd('/') + "/api/v1/sync/live");

        public Task<JsonElement> SignInAsync(string username, string password) => session.SignInAsync(username, password);

        public Task ChangePasswordAsync(string current, string next) => session.ChangePasswordAsync(current, next);

        public Task<JsonElement> CallAsync(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
            session.CallAsync(method, path, body, expected);

        /// <summary>A WebSocket with the session's cookies and the given Origin (a browser sends its page's).</summary>
        public ClientWebSocket Socket(string origin)
        {
            var socket = new ClientWebSocket();
            socket.Options.Cookies = session.Cookies;
            socket.Options.SetRequestHeader("Origin", origin);
            socket.Options.CollectHttpResponseDetails = true;
            return socket;
        }

        /// <summary>A call answered with any status (the caller checks it): raw bytes for a file chunk, nothing else.</summary>
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) => session.SendAsync(method, path, body);

        public Task<HttpResponseMessage> SendBytesAsync(HttpMethod method, string path, byte[] bytes) => session.SendBytesAsync(method, path, bytes);

        public void Dispose() => session.Dispose();
    }

    /// <summary>A device of a user: its client id, cursor and access epoch, as the local database keeps them.</summary>
    internal sealed class Device(User user)
    {
        public Guid ClientId { get; } = Guid.CreateVersion7();

        public long Cursor { get; set; }

        public int Epoch { get; private set; }

        public async Task<JsonElement> SyncAsync(object[]? mutations = null, bool pull = true, object[]? resolved = null)
        {
            var response = await user.CallAsync(HttpMethod.Post, "/api/v1/sync", new
            {
                clientId = ClientId, appVersion = "contract", since = Cursor, accessEpoch = Epoch, limit = 500,
                mutations = mutations ?? [], resolved = resolved ?? [], pull, pending = 0, conflicts = 0,
            });
            Assert.IsTrue(response.GetProperty("head").GetInt64() >= response.GetProperty("cursor").GetInt64());
            Epoch = response.GetProperty("accessEpoch").GetInt32();
            if (pull)
            {
                Cursor = response.GetProperty("cursor").GetInt64();
            }
            return response;
        }
    }

    /// <summary>The ProblemDetails <c>code</c> of an error answer.</summary>
    internal static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>The answer's JSON (a clone, the response may be disposed).</summary>
    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>A group of the admin's with these rights (a fresh name).</summary>
    internal static async Task<string> GroupAsync(ApiSession admin, string name, Dictionary<string, string> rights) =>
        (await admin.CallAsync(HttpMethod.Post, "/api/v1/groups", new { name, rights }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
}

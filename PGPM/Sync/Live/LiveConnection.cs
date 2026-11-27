using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PGPM.Auth;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sharing;

namespace PGPM.Sync.Live;

/// <summary>
/// One live WebSocket (docs/03 §11.4, §11.6). Everything it does happens on one loop, in the order the events come:
/// the client's messages (<c>hello</c>, <c>ack</c>, <c>ping</c>/<c>pong</c>), the change feed, session checks, editing
/// notices, a tick for heartbeats and timeouts, the shutdown. Flow control: at most one unacknowledged <c>changes</c>
/// batch; the next pull waits for its <c>ack</c> (30 s, else the connection closes and the client polls).
/// </summary>
internal sealed class LiveConnection(LiveSyncHub hub, WebSocket socket, LiveSession session)
{
    public const int MaxMessageBytes = 64 * 1024;

    internal sealed record Send(JsonObject Message);

    private sealed record Received(JsonObject Message);

    private sealed record Closed;

    internal static readonly object Feed = new();
    internal static readonly object Revalidate = new();
    internal static readonly object Shutdown = new();
    internal static readonly object ResetDevice = new();
    private static readonly object Tick = new();

    private readonly Channel<object> events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTime opened = Clock.UtcNow;

    private Viewer viewer = session.Viewer;
    private SessionCache.Entry entry = session.Entry;
    private int epoch = session.Entry.AccessEpoch;
    private bool live;
    private bool closed;
    private Guid? clientId;
    private volatile Device? device;
    private long cursor;
    private long deviceHorizon;
    private bool awaitingAck;
    private long sentCursor;
    private DateTime sentAt;
    private bool more;
    private bool feedPending;
    private int missedPongs;
    private DateTime lastPing = Clock.UtcNow;
    private DateTime lastCheck = Clock.UtcNow;

    public Guid UserId => session.Viewer.UserId;

    public bool IsAdmin => viewer.IsAdmin;

    /// <summary>The device, once its <c>hello</c> named it (read by the hub from other threads).</summary>
    public Guid? ClientId => device?.Id;

    private sealed record Device(Guid Id);

    public Task Completion => completion.Task;

    public void Post(object e) => events.Writer.TryWrite(e);

    public async Task RunAsync(CancellationToken aborted)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var receiving = ReceiveAsync(stop.Token);
        var ticking = TickAsync(stop.Token);
        try
        {
            await foreach (var e in events.Reader.ReadAllAsync(stop.Token))
            {
                if (!await HandleAsync(e, stop.Token) || closed)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // The client went away.
        }
        catch (Exception e)
        {
            hub.Logger.LogError(e, "Live connection of {User} failed", UserId);
            await CloseAsync(WebSocketCloseStatus.InternalServerError, "error", CancellationToken.None);
        }
        finally
        {
            // After our close frame, the client's answer ends the receive loop; a pending receive cancelled would abort the
            // socket before the client read the close code.
            if (closed)
            {
                await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            }
            await stop.CancelAsync();
            await Task.WhenAll(Quietly(receiving), Quietly(ticking));
            completion.TrySetResult();
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Ended with the connection.
        }
    }

    /// <summary>The device's row says it is no longer live.</summary>
    public async Task EndAsync()
    {
        if (clientId is not { } id)
        {
            return;
        }
        try
        {
            await using var db = await hub.Dbs.CreateDbContextAsync();
            await db.SyncClients.Where(c => c.Id == id && c.UserId == UserId).ExecuteUpdateAsync(s => s.SetProperty(c => c.LiveConnected, false));
        }
        catch (Exception e)
        {
            hub.Logger.LogWarning(e, "The sync client {Client} could not be marked offline", id);
        }
    }

    private async Task<bool> HandleAsync(object e, CancellationToken ct)
    {
        switch (e)
        {
            case Received { Message: var message }:
                return await OnMessageAsync(message, ct);
            case Closed:
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                }
                return false;
            case Send { Message: var message }:
                if (live)
                {
                    await SendAsync(message, ct);
                }
                return true;
            case var _ when ReferenceEquals(e, Feed):
                if (!live)
                {
                    return true;
                }
                if (awaitingAck)
                {
                    feedPending = true;
                    return true;
                }
                // A burst of commits is one pull: wait a moment, then take the feed events that came meanwhile.
                await Task.Delay(25, ct);
                while (events.Reader.TryPeek(out var next) && ReferenceEquals(next, Feed))
                {
                    events.Reader.TryRead(out _);
                }
                return await PullAsync(ct);
            case var _ when ReferenceEquals(e, ResetDevice):
                return await ResetAsync(ct);
            case var _ when ReferenceEquals(e, Revalidate):
                return await CheckSessionAsync(ct);
            case var _ when ReferenceEquals(e, Shutdown):
                await SendAsync(new JsonObject { ["type"] = "app", ["version"] = ServerInfo.Version }, ct);
                await CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server restarting", ct);
                return false;
            case var _ when ReferenceEquals(e, Tick):
                return await OnTickAsync(ct);
            default:
                return true;
        }
    }

    private async Task<bool> OnMessageAsync(JsonObject message, CancellationToken ct)
    {
        switch ((message["type"] as JsonValue)?.GetValueKind() == JsonValueKind.String ? message["type"]!.GetValue<string>() : null)
        {
            case "hello":
                return await HelloAsync(message, ct);
            case "ack":
                if (awaitingAck && Number(message["cursor"]) is { } acked && acked >= sentCursor)
                {
                    awaitingAck = false;
                    cursor = Math.Max(cursor, sentCursor);
                    if (more || feedPending)
                    {
                        feedPending = false;
                        return await PullAsync(ct);
                    }
                }
                return true;
            case "ping":
                await SendAsync(new JsonObject { ["type"] = "pong" }, ct);
                return true;
            case "pong":
                missedPongs = 0;
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// <c>hello</c>: the client's cursor and access epoch. Another epoch, a cursor below the tombstone horizon (or above the
    /// head), or a device whose row is gone: <c>reset</c>. Otherwise <c>welcome</c>, the open editing notices, and the
    /// changes after the cursor.
    /// </summary>
    private async Task<bool> HelloAsync(JsonObject message, CancellationToken ct)
    {
        if (live)
        {
            return true;
        }
        var since = Number(message["since"]) ?? 0;
        var helloEpoch = Number(message["accessEpoch"]);
        clientId = message["clientId"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && Guid.TryParse(v.GetValue<string>(), out var id)
            ? id : null;
        var appVersion = message["appVersion"] is JsonValue a && a.GetValueKind() == JsonValueKind.String ? a.GetValue<string>() : null;
        long head;
        await using (var db = await hub.Dbs.CreateDbContextAsync(ct))
        {
            var current = await db.Users.AsNoTracking().Where(u => u.Id == UserId).Select(u => (int?)u.AccessEpoch).FirstOrDefaultAsync(ct);
            if (current is null)
            {
                return await EndSessionAsync("revoked", ct);
            }
            epoch = current.Value;
            var client = clientId is { } c ? await db.SyncClients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c, ct) : null;
            head = await SyncCounter.HeadAsync(db, ct);
            var horizon = await SyncCounter.HorizonAsync(db, ct);
            if (since < 0 || (since > 0 && (helloEpoch != epoch || since > head || client is null || client.UserId != UserId
                || (since < horizon && horizon > client.HorizonSeq))))
            {
                return await ResetAsync(ct);
            }
            deviceHorizon = since == 0 ? horizon : client!.HorizonSeq;
            if (clientId is { } device)
            {
                await SyncService.TouchClientAsync(db, device, UserId, session.UserAgent, appVersion, since, 0, 0, live: true,
                    since == 0 ? horizon : null, ct);
            }
        }
        live = true;
        device = clientId is { } named ? new Device(named) : null;
        cursor = since;
        await SendAsync(new JsonObject { ["type"] = "welcome", ["head"] = head }, ct);
        await SendEditingAsync(ct);
        return await PullAsync(ct);
    }

    private async Task SendEditingAsync(CancellationToken ct)
    {
        var active = hub.Editing.Active.Where(e => e.UserId != UserId).ToList();
        if (active.Count == 0)
        {
            return;
        }
        await using var db = await hub.Dbs.CreateDbContextAsync(ct);
        var access = await new AccessService(db).ItemAccessAsync(viewer, active.Select(e => e.ItemId).ToList(), ct);
        foreach (var entry in active.Where(e => access.GetValueOrDefault(e.ItemId) >= ItemAccess.Read))
        {
            await SendAsync(LiveSyncHub.Message(entry, started: true), ct);
        }
    }

    /// <summary>The next page after the cursor, as <c>changes</c>; nothing when the user may see nothing new.</summary>
    private async Task<bool> PullAsync(CancellationToken ct)
    {
        if (!live || awaitingAck)
        {
            return true;
        }
        PullPage page;
        await using (var db = await hub.Dbs.CreateDbContextAsync(ct))
        {
            page = await SyncReader.PullAsync(db, viewer, cursor, Math.Clamp(hub.Options.PageLimit, 1, 1000), deviceHorizon, ct);
        }
        if (page.Reset)
        {
            return await ResetAsync(ct);
        }
        more = page.More;
        if (page.IsEmpty)
        {
            cursor = Math.Max(cursor, page.Cursor);
            return true;
        }
        await SendAsync(new JsonObject
        {
            ["type"] = "changes",
            ["changes"] = new JsonObject(page.Changes.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)new JsonArray([.. t.Value])))),
            ["evictions"] = new JsonArray([.. page.Evictions.Select(e => (JsonNode)new JsonObject { ["kind"] = e.Kind, ["id"] = e.Id })]),
            ["cursor"] = page.Cursor,
            ["more"] = page.More,
        }, ct);
        awaitingAck = true;
        sentCursor = page.Cursor;
        sentAt = Clock.UtcNow;
        return true;
    }

    private async Task<bool> OnTickAsync(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var options = hub.Options.Live;
        if (!live && now - opened > TimeSpan.FromSeconds(options.HelloTimeoutSeconds))
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "hello expected", ct);
            return false;
        }
        if (awaitingAck && now - sentAt > TimeSpan.FromSeconds(options.AckTimeoutSeconds))
        {
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "ack timeout", ct);
            return false;
        }
        if (now - lastPing >= TimeSpan.FromSeconds(options.HeartbeatSeconds))
        {
            if (missedPongs >= 2)
            {
                await CloseAsync(WebSocketCloseStatus.NormalClosure, "heartbeat", ct);
                return false;
            }
            missedPongs++;
            lastPing = now;
            await SendAsync(new JsonObject { ["type"] = "ping" }, ct);
        }
        if (now - lastCheck >= TimeSpan.FromSeconds(options.RevalidateSeconds))
        {
            return await CheckSessionAsync(ct);
        }
        return true;
    }

    /// <summary>
    /// The session again (every minute, and when <see cref="SessionCache.Forget"/> names the user): gone, deactivated or
    /// signed out everywhere → <c>session/revoked</c> and 4401; the sign-in's 30 days over → <c>session/expired</c> and
    /// 4401; another access epoch → <c>reset</c> and 4409; other rights or groups → <c>session/rights_changed</c>.
    /// </summary>
    private async Task<bool> CheckSessionAsync(CancellationToken ct)
    {
        lastCheck = Clock.UtcNow;
        SessionCache.Entry? now;
        await using (var db = await hub.Dbs.CreateDbContextAsync(ct))
        {
            now = await hub.Sessions.GetAsync(UserId, () => SessionCache.LoadAsync(db, UserId, ct));
        }
        if (now is null || !now.IsActive || now.Stamp != (session.Stamp ?? session.Entry.Stamp))
        {
            return await EndSessionAsync("revoked", ct);
        }
        if (session.SignedIn is { } signedIn && SessionRules.LoginEnds(signedIn, hub.Auth.CurrentValue) is { } ends && ends <= Clock.UtcNow)
        {
            return await EndSessionAsync("expired", ct);
        }
        if (now.AccessEpoch != epoch)
        {
            return await ResetAsync(ct);
        }
        if (now.Rights.IsAdmin != entry.Rights.IsAdmin || !now.Rights.Levels.OrderBy(l => l.Key).SequenceEqual(entry.Rights.Levels.OrderBy(l => l.Key))
            || !now.GroupIds.Order().SequenceEqual(entry.GroupIds.Order()))
        {
            entry = now;
            // The rights too: a map template's access follows Configuration: Manage (S-11, docs/06).
            viewer = viewer with { GroupIds = now.GroupIds, Rights = now.Rights };
            await SendAsync(new JsonObject { ["type"] = "session", ["state"] = "rights_changed" }, ct);
        }
        return true;
    }

    private async Task<bool> EndSessionAsync(string state, CancellationToken ct)
    {
        await SendAsync(new JsonObject { ["type"] = "session", ["state"] = state }, ct);
        await CloseAsync(LiveCloseCodes.SessionInvalid, $"session {state}", ct);
        return false;
    }

    private async Task<bool> ResetAsync(CancellationToken ct)
    {
        await SendAsync(new JsonObject { ["type"] = "reset" }, ct);
        await CloseAsync(LiveCloseCodes.ResetRequired, "reset required", ct);
        return false;
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (closed || socket.State != WebSocketState.Open)
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(WireJson.Options));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
    }

    public Task CloseAsync(int code, string reason, CancellationToken ct) => CloseAsync((WebSocketCloseStatus)code, reason, ct);

    private async Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        if (closed)
        {
            return;
        }
        closed = true;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Gone already.
        }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", ct);
                    break;
                }
                if (!result.EndOfMessage)
                {
                    continue;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    try
                    {
                        if (JsonNode.Parse(message.ToArray()) is JsonObject json)
                        {
                            Post(new Received(json));
                        }
                    }
                    catch (JsonException)
                    {
                        // Not JSON: ignored.
                    }
                }
                message.SetLength(0);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // Gone.
        }
        Post(new Closed());
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(ct))
        {
            Post(Tick);
        }
    }

    private static long? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var n) ? n : null;
}

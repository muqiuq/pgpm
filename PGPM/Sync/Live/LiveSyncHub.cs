using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Auth;
using PGPM.Data;
using PGPM.Sharing;

namespace PGPM.Sync.Live;

/// <summary>The close codes of the live channel (docs/03 §11.4).</summary>
public static class LiveCloseCodes
{
    /// <summary>The session is no longer valid: sign in again.</summary>
    public const int SessionInvalid = 4401;

    /// <summary>Reserved for the origin; a wrong origin is refused with HTTP 403 before the upgrade.</summary>
    public const int Origin = 4403;

    /// <summary>The client must reset (after the <c>reset</c> message).</summary>
    public const int ResetRequired = 4409;

    /// <summary>More than Sync:Live:MaxConnectionsPerUser.</summary>
    public const int TooManyConnections = 4429;
}

/// <summary>Who a live connection is: from the handshake's session, checked again every minute.</summary>
public sealed record LiveSession(Viewer Viewer, string? Stamp, DateTime? SignedIn, SessionCache.Entry Entry, string? UserAgent);

/// <summary>
/// The live feed (docs/03 §11.6): owns the WebSocket connections, wakes them on every <see cref="ChangeFeed.Publish"/>,
/// tells a user's connections when <see cref="SessionCache.Forget"/> is called for them, spreads <c>editing</c> notices,
/// and says <c>app</c> before the server stops. Hosted, so it lives from start to shutdown.
/// </summary>
public sealed class LiveSyncHub : BackgroundService
{
    private readonly Lock gate = new();
    private readonly HashSet<LiveConnection> connections = [];
    private readonly IDbContextFactory<PgpmDbContext> dbs;
    private readonly EditingRegistry editing;
    private readonly ILogger<LiveSyncHub> logger;

    public LiveSyncHub(IDbContextFactory<PgpmDbContext> dbs, ChangeFeed feed, SessionCache sessions, EditingRegistry editing,
        IOptions<SyncOptions> options, IOptionsMonitor<AuthOptions> auth, ILogger<LiveSyncHub> logger)
    {
        this.dbs = dbs;
        this.editing = editing;
        this.logger = logger;
        Options = options.Value;
        Auth = auth;
        Sessions = sessions;
        feed.Published += _ => Each(c => c.Post(LiveConnection.Feed));
        sessions.Forgotten += user => Each(c => c.Post(LiveConnection.Revalidate), user);
        editing.Changed += (entry, started) => _ = AnnounceAsync(entry, started);
    }

    internal SyncOptions Options { get; }

    internal IOptionsMonitor<AuthOptions> Auth { get; }

    internal SessionCache Sessions { get; }

    internal IDbContextFactory<PgpmDbContext> Dbs => dbs;

    internal EditingRegistry Editing => editing;

    internal ILogger Logger => logger;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return connections.Count;
            }
        }
    }

    /// <summary>The users with a live connection now.</summary>
    public int LiveUsers()
    {
        lock (gate)
        {
            return connections.Select(c => c.UserId).Distinct().Count();
        }
    }

    /// <summary>The devices (<c>sync_clients.id</c>) whose live channel is open now (after their <c>hello</c>).</summary>
    public IReadOnlySet<Guid> LiveClients()
    {
        lock (gate)
        {
            return connections.Select(c => c.ClientId).OfType<Guid>().ToHashSet();
        }
    }

    /// <summary>
    /// A device's row was deleted (Administration → System): its live connections get <c>reset</c> and close 4409, so it
    /// starts over at once. Answers how many were told.
    /// </summary>
    public int ResetClient(Guid clientId)
    {
        List<LiveConnection> matching;
        lock (gate)
        {
            matching = [.. connections.Where(c => c.ClientId == clientId)];
        }
        matching.ForEach(c => c.Post(LiveConnection.ResetDevice));
        return matching.Count;
    }

    /// <summary>Serves one accepted WebSocket until it closes.</summary>
    public async Task RunAsync(WebSocket socket, LiveSession session, CancellationToken aborted)
    {
        var connection = new LiveConnection(this, socket, session);
        bool admitted;
        lock (gate)
        {
            admitted = connections.Count(c => c.UserId == session.Viewer.UserId) < Options.Live.MaxConnectionsPerUser;
            if (admitted)
            {
                connections.Add(connection);
            }
        }
        if (!admitted)
        {
            await connection.CloseAsync(LiveCloseCodes.TooManyConnections, "too many connections", aborted);
            return;
        }
        try
        {
            await connection.RunAsync(aborted);
        }
        finally
        {
            lock (gate)
            {
                connections.Remove(connection);
            }
            await connection.EndAsync();
        }
    }

    private void Each(Action<LiveConnection> action, Guid? user = null)
    {
        List<LiveConnection> all;
        lock (gate)
        {
            all = [.. connections.Where(c => user is null || c.UserId == user)];
        }
        all.ForEach(action);
    }

    /// <summary><c>editing</c> / <c>editing_end</c> to the live connections of the other users who can read the item.</summary>
    private async Task AnnounceAsync(EditingEntry entry, bool started)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync();
            var readers = await new AccessService(db).UsersWithItemAccessAsync(entry.ItemId, CancellationToken.None);
            var message = Message(entry, started);
            Each(c =>
            {
                if (c.UserId != entry.UserId && (readers.Contains(c.UserId) || c.IsAdmin))
                {
                    c.Post(new LiveConnection.Send(message));
                }
            });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The editing notice for item {Item} was not sent", entry.ItemId);
        }
    }

    internal static JsonObject Message(EditingEntry entry, bool started)
    {
        var message = new JsonObject
        {
            ["type"] = started ? "editing" : "editing_end",
            ["itemId"] = entry.ItemId,
            ["target"] = entry.Target,
            ["userId"] = entry.UserId,
            ["user"] = entry.UserName,
        };
        if (started)
        {
            message["since"] = entry.Since;
        }
        return message;
    }

    /// <summary>Expires editing entries without a heartbeat.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                editing.Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>A deploy: every client hears the new version is coming (<c>app</c>), then the connections close.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        List<LiveConnection> all;
        lock (gate)
        {
            all = [.. connections];
        }
        foreach (var connection in all)
        {
            connection.Post(LiveConnection.Shutdown);
        }
        await Task.WhenAny(Task.WhenAll(all.Select(c => c.Completion)), Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        await base.StopAsync(cancellationToken);
    }
}

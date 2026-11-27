using System.Data;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;

namespace PGPM.Sync;

/// <summary>
/// One pull page (docs/03 §11.6): the rows of every table after the cursor, evictions, and where the cursor may move;
/// <see cref="Horizon"/> is the snapshot's tombstone horizon (a device pulling from 0 remembers it).
/// </summary>
public sealed record PullPage(long Head, long Cursor, bool More, bool Reset, Dictionary<string, List<JsonObject>> Changes, List<SyncEviction> Evictions,
    long Horizon)
{
    public bool IsEmpty => Changes.Count == 0 && Evictions.Count == 0;

    public static PullPage ResetAt(long head, long horizon) => new(head, 0, false, true, [], [], horizon);
}

/// <summary>
/// The pull (docs/03 §11.6, PLM's SyncService.PullAsync): one REPEATABLE READ snapshot, so the counter and the rows agree;
/// each registered table paged on its own by seq, filtered by what the user may see, plus the user's evictions. The cursor
/// moves to the head when nothing came back full, else to the lowest of the full tables' highest seqs (PLM's
/// <c>nextCursor</c>): a table that filled its page may have more rows below the others' last ones. A cursor above the
/// head resets, and one below the tombstone horizon if the horizon rose since the device last pulled from 0
/// (<paramref name="deviceHorizon"/>: <see cref="SyncClientEntity.HorizonSeq"/>).
/// </summary>
/// <para>
/// Before the tables, one statement reads the head, the horizon and every table's highest seq (an index lookup each, the
/// seq indexes are unique): a table without a row above the cursor is not queried at all. A live feed's pull after a
/// commit so reads the one or two tables that changed instead of all of them (docs/07 §1).
/// </para>
public static class SyncReader
{
    /// <summary>What the pulls did since the start (docs/07, the load test): pulls, tables read, tables skipped.</summary>
    public static readonly PullCounters Counters = new();

    /// <summary>Off: every table is read (the load test's baseline).</summary>
    internal static bool SkipUnchangedTables { get; set; } = true;

    private static string? headsSql;

    /// <summary>The head, the horizon, the highest seq of every table (in registry order) and of the evictions.</summary>
    private static string HeadsSql(PgpmDbContext db) => headsSql ??= string.Join(" UNION ALL ",
        new[] { "SELECT -2 AS i, (SELECT value FROM sync_counter WHERE id = 1) AS seq", "SELECT -1, (SELECT tombstone_horizon_seq FROM sync_state WHERE id = 1)" }
            .Concat(SyncTables.All.Select((t, i) => $"SELECT {i}, (SELECT max(seq) FROM {t.SqlTable(db)})"))
            .Append($"SELECT {SyncTables.All.Count}, (SELECT max(seq) FROM sync_evictions)"));

    private sealed record TableHead(int I, long? Seq);

    public static async Task<PullPage> PullAsync(PgpmDbContext db, Viewer viewer, long since, int limit, long deviceHorizon, CancellationToken ct,
        bool? skipUnchanged = null)
    {
        var skip = skipUnchanged ?? SkipUnchangedTables;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var heads = (await db.Database.SqlQueryRaw<TableHead>(HeadsSql(db)).ToListAsync(ct)).ToDictionary(h => h.I, h => h.Seq ?? 0);
        var head = heads[-2];
        var horizon = heads[-1];
        Counters.Pull();
        if (since > 0 && (since > head || (since < horizon && horizon > deviceHorizon)))
        {
            await transaction.CommitAsync(ct);
            return PullPage.ResetAt(head, horizon);
        }
        var scope = new PullScope(db, viewer, new AccessService(db));
        var changes = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var full = new List<long>();
        for (var i = 0; i < SyncTables.All.Count; i++)
        {
            var table = SyncTables.All[i];
            if (skip && heads[i] <= since)
            {
                Counters.Skip();
                continue;
            }
            Counters.Read();
            var rows = await table.PageAsync(scope, since, limit, ct);
            if (rows.Count > 0)
            {
                changes[table.Name] = rows.Select(r => r.Row).ToList();
            }
            if (rows.Count >= limit)
            {
                full.Add(rows[^1].Seq);
            }
        }
        var evictions = skip && heads[SyncTables.All.Count] <= since ? []
            : await db.SyncEvictions.AsNoTracking().Where(e => e.UserId == viewer.UserId && e.Seq > since).OrderBy(e => e.Seq).Take(limit)
                .ToListAsync(ct);
        if (evictions.Count >= limit)
        {
            full.Add(evictions[^1].Seq);
        }
        await transaction.CommitAsync(ct);
        var more = full.Count > 0;
        return new PullPage(head, more ? Math.Max(since, full.Min()) : head, more, false, changes,
            evictions.Select(e => new SyncEviction(e.RootKind.ToString().ToLowerInvariant(), e.RootId)).ToList(), horizon);
    }
}

/// <summary>The pulls' counters (thread-safe), for the load test and diagnostics.</summary>
public sealed class PullCounters
{
    private long pulls;
    private long reads;
    private long skipped;

    public void Pull() => Interlocked.Increment(ref pulls);

    public void Read() => Interlocked.Increment(ref reads);

    public void Skip() => Interlocked.Increment(ref skipped);

    public PullCounts Snapshot() => new(Interlocked.Read(ref pulls), Interlocked.Read(ref reads), Interlocked.Read(ref skipped));
}

/// <summary>Pulls, table reads and skipped tables; the difference of two snapshots is what happened between them.</summary>
public readonly record struct PullCounts(long Pulls, long TableReads, long Skipped)
{
    public static PullCounts operator -(PullCounts a, PullCounts b) => new(a.Pulls - b.Pulls, a.TableReads - b.TableReads, a.Skipped - b.Skipped);
}

/// <summary>Sync:* (docs/03 §14).</summary>
public sealed class SyncOptions
{
    public int PageLimit { get; set; } = 500;

    public int MaxMutations { get; set; } = 200;

    public long MaxBodyBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Tombstones of <c>other</c> delete batches (not the Trash) are pruned after this many days.</summary>
    public int TombstoneDays { get; set; } = 90;

    public LiveOptions Live { get; set; } = new();
}

/// <summary>Sync:Live:* (docs/03 §14).</summary>
public sealed class LiveOptions
{
    public bool Enabled { get; set; } = true;

    public int MaxConnectionsPerUser { get; set; } = 10;

    public int AckTimeoutSeconds { get; set; } = 30;

    public int HeartbeatSeconds { get; set; } = 25;

    /// <summary>How often a connection checks its session (and on every <see cref="Auth.SessionCache.Forget"/>).</summary>
    public int RevalidateSeconds { get; set; } = 60;

    /// <summary>A connection that sends no hello within this time is closed.</summary>
    public int HelloTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// <c>POST /api/v1/sync</c> (docs/03 §11.4, §11.5): the push in one locked transaction, the conflicts the user decided,
/// the reset rules, the device's row, then a pull page.
/// </summary>
public sealed class SyncService(PgpmDbContext db, SyncWriter writer, IOptions<SyncOptions> options)
{
    public async Task<SyncResponse> SyncAsync(Viewer viewer, SyncRequest request, HttpContext http, CancellationToken ct)
    {
        var settings = options.Value;
        var mutations = request.Mutations ?? [];
        var resolved = request.Resolved ?? [];
        new ApiValidator()
            .Check(request.ClientId is { } c && c != Guid.Empty, "clientId", "The device's id.")
            .Check(request.Since >= 0, "since", "0 or a cursor.")
            .Check(mutations.Count <= settings.MaxMutations, "mutations", $"At most {settings.MaxMutations} per request.")
            .Check(resolved.Count <= 1000, "resolved", "At most 1000 per request.")
            .Check(request.Limit is null or (>= 1 and <= 1000), "limit", "1 to 1000.")
            .ThrowIfInvalid();
        var clientId = request.ClientId!.Value;
        var results = new List<SyncMutationResult>();
        IReadOnlyList<SyncConflict> conflicts = [];
        if (mutations.Count + resolved.Count > 0)
        {
            await using var write = await writer.BeginAsync(viewer, ChangeSource.Ui, http.TraceIdentifier, ct);
            foreach (var mutation in mutations)
            {
                results.Add(await write.ApplyAsync(mutation, clientId, ct));
            }
            foreach (var resolution in resolved)
            {
                await write.ResolveAsync(resolution, ct);
            }
            await write.CommitAsync(ct);
            conflicts = write.Conflicts;
        }

        var epoch = await db.Users.AsNoTracking().Where(u => u.Id == viewer.UserId).Select(u => (int?)u.AccessEpoch).SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthorized();
        var client = await db.SyncClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        // A client that has synced before must still have its row (an admin deleting it resets the device) and the epoch.
        var reset = request.Since > 0 && (request.AccessEpoch != epoch || client is null || client.UserId != viewer.UserId);
        var page = reset || !request.Pull ? null
            : await SyncReader.PullAsync(db, viewer, request.Since, request.Limit ?? Math.Clamp(settings.PageLimit, 1, 1000), client?.HorizonSeq ?? 0, ct);
        reset |= page?.Reset == true;
        var head = page?.Head ?? await SyncCounter.HeadAsync(db, ct);
        var cursor = reset ? 0 : page?.Cursor ?? request.Since;
        // A pull from 0 starts the device's data afresh: purges before it can't concern it.
        var horizon = request.Since == 0 && page is { Reset: false } ? page.Horizon : (long?)null;
        await TouchClientAsync(db, clientId, viewer.UserId, http.Request.Headers.UserAgent.ToString(), request.AppVersion, cursor,
            request.Pending ?? 0, request.Conflicts ?? 0, live: null, horizon, ct);
        return new SyncResponse(head, cursor, page is { More: true, Reset: false }, reset, epoch, results, conflicts,
            reset ? [] : page?.Changes ?? [], reset ? [] : page?.Evictions ?? []);
    }

    /// <summary>
    /// The device's row (docs/02 §11.4), created or taken over by this user. <paramref name="live"/> null keeps the live flag
    /// (and sets the counts), <paramref name="horizon"/> null keeps the horizon the device started from.
    /// </summary>
    public static Task TouchClientAsync(PgpmDbContext db, Guid clientId, Guid userId, string? userAgent, string? appVersion, long lastSeq,
        int pending, int conflicts, bool? live, long? horizon, CancellationToken ct)
    {
        var agent = string.IsNullOrEmpty(userAgent) ? null : userAgent.Length > 256 ? userAgent[..256] : userAgent;
        var version = appVersion is { Length: > 40 } ? appVersion[..40] : appVersion;
        var now = Clock.UtcNow;
        var connected = live ?? false;
        var keepLive = live is null;
        var start = horizon ?? 0;
        var keepHorizon = horizon is null;
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO sync_clients (id, user_id, user_agent, app_version, last_seen_at, last_seq, pending_count, conflict_count, live_connected,
                horizon_seq)
            VALUES ({clientId}, {userId}, {agent}, {version}, {now}, {lastSeq}, {pending}, {conflicts}, {connected}, {start})
            ON CONFLICT (id) DO UPDATE SET user_id = EXCLUDED.user_id, user_agent = COALESCE(EXCLUDED.user_agent, sync_clients.user_agent),
                app_version = COALESCE(EXCLUDED.app_version, sync_clients.app_version), last_seen_at = EXCLUDED.last_seen_at,
                last_seq = EXCLUDED.last_seq,
                pending_count = CASE WHEN {keepLive} THEN EXCLUDED.pending_count ELSE sync_clients.pending_count END,
                conflict_count = CASE WHEN {keepLive} THEN EXCLUDED.conflict_count ELSE sync_clients.conflict_count END,
                live_connected = CASE WHEN {keepLive} THEN sync_clients.live_connected ELSE EXCLUDED.live_connected END,
                horizon_seq = CASE WHEN {keepHorizon} AND sync_clients.user_id = EXCLUDED.user_id THEN sync_clients.horizon_seq
                    ELSE EXCLUDED.horizon_seq END
            """, ct);
    }
}

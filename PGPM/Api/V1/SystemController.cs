using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Files;
using PGPM.Lib.Helper;
using PGPM.Mcp;
using PGPM.Persons.DirectorySync;
using PGPM.Sync;
using PGPM.Sync.Live;

namespace PGPM.Api.V1;

/// <summary>
/// Administration → System (docs/01 §3, docs/03 §9): the instance, the database, the files' disk, the sync's head and
/// horizon, every device with its cursor and what it reported, the live connections, the background tasks' health and the
/// directory and MCP at a glance. <i>System: View</i>, which also lets an admin make a device start over
/// (<c>DELETE /clients/{id}</c>: its row goes, the device resets at its next sync, a live one at once).
/// </summary>
[Route("api/v1/system")]
[RequireRight(Area.System, Level.View)]
public sealed class SystemController(PgpmDbContext db, LiveSyncHub hub, FileStore files, DirectorySyncService directory,
    IOptions<SyncOptions> sync, IOptionsMonitor<McpOptions> mcp, IConfiguration configuration, IWebHostEnvironment environment) : V1ControllerBase
{
    /// <summary>The newest devices first, at most this many (an instance with more lists the most recent ones).</summary>
    public const int MaxClients = 500;

    /// <summary>The background tasks PGPM runs (docs/03 §9) and how often; a <c>task.*</c> key of another is listed too.</summary>
    private static readonly (string Key, string Name, int? Minutes)[] KnownTasks =
    [
        (TombstonePruner.HealthKey, "Tombstone pruner", 60),
        (LogPruner.HealthKey, "Log pruner", 24 * 60),
        (FileCleanup.HealthKey, "File cleanup", 60),
        (FileCheck.HealthKey, "File check", 60),
        (McpTokenExpiryTask.HealthKey, "MCP token expiry", 5),
        (DirectorySyncService.HealthKey, "Directory sync", null),
    ];

    [HttpGet]
    public async Task<SystemStatusItem> Status(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var background = configuration.GetValue("BackgroundTasks:Enabled", true);
        var instance = new SystemInstanceItem(ServerInfo.Name, ServerInfo.Version, started, (long)Math.Max(0, (now - started).TotalSeconds),
            environment.EnvironmentName, configuration["PublicUrl"], background);

        var size = (await db.Database.SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"").ToListAsync(ct)).Single();
        var version = (await db.Database.SqlQuery<string>($"SELECT current_setting('server_version') AS \"Value\"").ToListAsync(ct)).Single();

        var stored = await db.Database.SqlQuery<StoredBytes>($"""
            SELECT COALESCE(sum(size_bytes), 0)::bigint AS bytes, count(*)::int AS files
            FROM (SELECT DISTINCT ON (storage_path) storage_path, size_bytes FROM files WHERE storage_path IS NOT NULL) AS s
            """).SingleAsync(ct);
        var uploads = await db.FileUploads.CountAsync(ct);
        var options = files.Options;
        var storage = new SystemFilesItem(stored.Bytes, stored.Files, uploads, files.FreeBytes(), options.MinFreeMb * 1024 * 1024, files.Problem());

        var head = await SyncCounter.HeadAsync(db, ct);
        var horizon = await SyncCounter.HorizonAsync(db, ct);
        var live = hub.LiveClients();
        var rows = await db.SyncClients.AsNoTracking().OrderByDescending(c => c.LastSeenAt).Take(MaxClients)
            .GroupJoin(db.Users.AsNoTracking(), c => c.UserId, u => u.Id, (c, u) => new { Client = c, Users = u })
            .SelectMany(x => x.Users.DefaultIfEmpty(), (x, u) => new { x.Client, Name = u == null ? null : u.DisplayName })
            .ToListAsync(ct);
        var clients = rows.OrderByDescending(r => r.Client.LastSeenAt).Select(r => new SystemClientItem(r.Client.Id, r.Client.UserId, r.Name,
            r.Client.AppVersion, r.Client.UserAgent, r.Client.LastSeenAt, r.Client.LastSeq, Math.Max(0, head - r.Client.LastSeq), r.Client.PendingCount,
            r.Client.ConflictCount, live.Contains(r.Client.Id))).ToList();
        var counts = await db.SyncClients.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Pending = g.Sum(c => c.PendingCount), Conflicts = g.Sum(c => c.ConflictCount) })
            .FirstOrDefaultAsync(ct);
        var s = sync.Value;
        var syncItem = new SystemSyncItem(head, horizon, counts?.Count ?? 0, hub.Count, hub.LiveUsers(), counts?.Pending ?? 0, counts?.Conflicts ?? 0,
            s.Live.Enabled, s.Live.MaxConnectionsPerUser);

        var tasks = await TasksAsync(background, started, now, ct);

        var credentials = directory.Credentials();
        var state = credentials is null ? null
            : await db.DirectorySyncState.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == credentials.TenantId, ct);
        var directoryItem = new SystemDirectoryItem(directory.Options.Enabled, credentials is not null, state?.LastStatus, state?.LastFinishedAt,
            state?.LastError);

        var day = now.AddDays(-1);
        var calls = await db.McpLog.AsNoTracking().Where(l => l.Method == "tools/call" && l.At >= day)
            .GroupBy(l => l.Outcome == McpOutcomes.Ok).Select(g => new { Ok = g.Key, Count = g.Count() }).ToListAsync(ct);
        var tokensOn = await db.McpTokens.AsNoTracking().CountAsync(t => t.OnSince != null && t.Hash != null && t.BlockedAt == null, ct);
        var mcpItem = new SystemMcpItem(mcp.CurrentValue.Enabled, tokensOn, calls.Sum(c => c.Count), calls.Where(c => !c.Ok).Sum(c => c.Count));

        return new SystemStatusItem(instance, new SystemDatabaseItem(size, version), storage, syncItem, clients, tasks, directoryItem, mcpItem);
    }

    /// <summary>
    /// Makes a device start over (docs/03 §11.6 "sync_clients row deleted by an admin"): its row goes, so its next sync is a
    /// reset – its outbox is pushed first, its local data wiped and pulled anew; an open live channel is told at once
    /// (<c>reset</c>, close 4409). 404 for a device the server doesn't know.
    /// </summary>
    [HttpDelete("clients/{id:guid}")]
    [Audit(AuditEvents.SystemClientReset)]
    public async Task<IActionResult> ResetClient(Guid id, CancellationToken ct)
    {
        var client = await db.SyncClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Sync client");
        var user = await db.Users.AsNoTracking().Where(u => u.Id == client.UserId).Select(u => new { u.Id, u.Username }).FirstOrDefaultAsync(ct);
        await db.SyncClients.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        var connections = hub.ResetClient(id);
        if (user is not null)
        {
            AuditLog.Target(AuditTarget.User(user.Id, user.Username));
        }
        AuditLog.Summary($"Device {id} ({client.AppVersion ?? "unknown version"}, last seen {client.LastSeenAt:yyyy-MM-dd HH:mm} UTC)"
            + (connections > 0 ? ", live channel reset" : ""));
        return NoContent();
    }

    private async Task<List<SystemTaskItem>> TasksAsync(bool background, DateTime started, DateTime now, CancellationToken ct)
    {
        var rows = await db.KeyValues.AsNoTracking().Where(k => k.Key.StartsWith("task.")).ToListAsync(ct);
        var byKey = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var directoryOn = directory.Options.Enabled && directory.Credentials() is not null;
        var list = new List<SystemTaskItem>();
        foreach (var (key, name, minutes) in KnownTasks)
        {
            if (key == DirectorySyncService.HealthKey && !directoryOn && !byKey.ContainsKey(key))
            {
                continue;
            }
            var interval = key == DirectorySyncService.HealthKey ? Math.Max(1, directory.Options.IntervalHours) * 60 : minutes;
            list.Add(Task(key, name, byKey.GetValueOrDefault(key), interval));
        }
        list.AddRange(rows.Where(r => KnownTasks.All(t => t.Key != r.Key)).OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => Task(r.Key, r.Key["task.".Length..].Replace('_', ' '), r, null)));
        return list;

        SystemTaskItem Task(string key, string name, KeyValueEntity? row, int? interval)
        {
            // Overdue by more than an interval (plus the minute the tasks wait after a start), with the background tasks on.
            var due = interval is { } m ? TimeSpan.FromMinutes(2 * m + 2) : (TimeSpan?)null;
            var since = row?.UpdatedAt ?? started;
            var stale = background && due is { } d && now - since > d;
            return new SystemTaskItem(key, name, row?.Value, row?.UpdatedAt, interval, stale);
        }
    }

    private sealed record StoredBytes(long Bytes, int Files);
}

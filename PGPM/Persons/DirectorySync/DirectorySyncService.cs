using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Audit;
using PGPM.Auth.Microsoft;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Persons.DirectorySync;

/// <summary>The directory sync's settings (docs/03 §14): <c>Directory:Enabled</c> (off), <c>IntervalHours</c> (6), <c>FullSyncDays</c> (7).</summary>
public sealed class DirectoryOptions
{
    public const string Section = "Directory";

    public bool Enabled { get; set; }

    public int IntervalHours { get; set; } = 6;

    public int FullSyncDays { get; set; } = 7;
}

/// <summary>What a run did.</summary>
public sealed record DirectoryRunResult(bool Ok, string Mode, int Created, int Updated, int Deactivated, int Seen, int Linked, string? Error);

/// <summary>
/// The persons directory from Microsoft Graph (docs/03 §8): the whole tenant, members and guests, upserted by (tenant,
/// object id) – the SSO's identity key. Incremental runs continue from the stored delta link; a full run (the first, the
/// weekly one after <c>Directory:FullSyncDays</c>, after a 410, or asked for) reads everything and makes every person it
/// didn't see inactive. Removed or disabled accounts become inactive, never deleted. A person whose user exists (same
/// tenant and object id) is linked to it. Each page is one <see cref="SyncWriter"/> unit with the source <c>directory</c>:
/// only persons that changed get a new seq, and they reach every connected client live. The result goes into
/// <c>directory_sync_state</c> and the task health (<c>key_values</c>); a failure into the audit log too.
/// </summary>
public sealed class DirectorySyncService(IDbContextFactory<PgpmDbContext> dbs, GraphDirectoryClient graph, IOptionsMonitor<DirectoryOptions> options,
    IOptionsMonitor<EntraOptions> entra, ILogger<DirectorySyncService> logger)
{
    public const string HealthKey = "task.directory_sync";

    /// <summary>One run at a time in this process, whoever starts it.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Who writes: PGPM itself, no user (<c>updated_by</c> stays empty), seeing everything.</summary>
    private static readonly Viewer System = new(Guid.Empty, true, [], UserRights.None);

    public DirectoryOptions Options => options.CurrentValue;

    /// <summary>The app registration from <c>Entra:*</c>; null while the tenant, the client or the secret is missing.</summary>
    public DirectoryCredentials? Credentials()
    {
        var e = entra.CurrentValue;
        return e.Tenant is { } tenant && !string.IsNullOrWhiteSpace(e.ClientId) && !string.IsNullOrWhiteSpace(e.ClientSecret)
            ? new DirectoryCredentials(tenant, e.ClientId.Trim(), e.ClientSecret)
            : null;
    }

    /// <summary>Due: switched on, configured, and the last run started <c>IntervalHours</c> ago (or never).</summary>
    public async Task<bool> IsDueAsync(CancellationToken ct)
    {
        if (!Options.Enabled || Credentials() is not { } credentials)
        {
            return false;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var started = await db.DirectorySyncState.AsNoTracking().Where(s => s.TenantId == credentials.TenantId).Select(s => s.LastStartedAt)
            .FirstOrDefaultAsync(ct);
        return started is null || started.Value.AddHours(Math.Max(1, Options.IntervalHours)) <= Clock.UtcNow;
    }

    /// <summary>A run with the configured registration; <paramref name="full"/> null: full when one is due.</summary>
    public Task<DirectoryRunResult> RunAsync(bool? full, CancellationToken ct) =>
        Credentials() is { } credentials
            ? RunAsync(credentials, full, ct)
            : throw new InvalidOperationException("The directory sync needs Entra:TenantId, Entra:ClientId and Entra:ClientSecret.");

    public async Task<DirectoryRunResult> RunAsync(DirectoryCredentials credentials, bool? forceFull, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await RunLockedAsync(credentials, forceFull, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class Counts
    {
        public int Created;
        public int Updated;
        public int Deactivated;
        public int Linked;
        public readonly HashSet<Guid> Seen = [];
    }

    private async Task<DirectoryRunResult> RunLockedAsync(DirectoryCredentials credentials, bool? forceFull, CancellationToken ct)
    {
        var tenant = credentials.TenantId;
        var started = Clock.UtcNow;
        string? deltaLink;
        bool full;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            var state = await db.DirectorySyncState.SingleOrDefaultAsync(s => s.TenantId == tenant, ct);
            if (state is null)
            {
                state = new DirectorySyncStateEntity { TenantId = tenant };
                db.DirectorySyncState.Add(state);
            }
            full = forceFull ?? state.LastFullAt is not { } lastFull || lastFull.AddDays(Math.Max(1, Options.FullSyncDays)) <= started;
            full = full || state.DeltaLink is null;
            deltaLink = state.DeltaLink;
            state.LastStartedAt = started;
            state.LastStatus = DirectoryRunStatus.Running;
            state.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        var counts = new Counts();
        var correlation = $"directory:{Ids.New()}";
        try
        {
            GraphDirectoryClient.ReadResult read;
            try
            {
                read = await graph.ReadAsync(credentials, full ? null : deltaLink, page => ApplyPageAsync(tenant, page, counts, correlation, ct), ct);
            }
            catch (DeltaExpiredException) when (!full)
            {
                logger.LogInformation("The directory's delta link expired: a full run instead");
                full = true;
                counts.Seen.Clear();
                read = await graph.ReadAsync(credentials, null, page => ApplyPageAsync(tenant, page, counts, correlation, ct), ct);
            }
            if (full)
            {
                counts.Deactivated += await DeactivateUnseenAsync(tenant, counts.Seen, correlation, ct);
            }
            var result = new DirectoryRunResult(true, full ? "full" : "delta", counts.Created, counts.Updated, counts.Deactivated, counts.Seen.Count,
                counts.Linked, null);
            await FinishAsync(tenant, result, read.DeltaLink, started, ct);
            logger.LogInformation("Directory sync ({Mode}): {Created} created, {Updated} updated, {Deactivated} deactivated of {Seen}, {Pages} pages, {Retries} waits",
                result.Mode, result.Created, result.Updated, result.Deactivated, result.Seen, read.Pages, read.Retries);
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = e is GraphException or DeltaExpiredException or InvalidOperationException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            logger.LogWarning(e, "The directory sync failed");
            var result = new DirectoryRunResult(false, full ? "full" : "delta", counts.Created, counts.Updated, counts.Deactivated, counts.Seen.Count,
                counts.Linked, GraphTokenCache.Cut(error, 1000));
            await FinishAsync(tenant, result, null, started, CancellationToken.None);
            return result;
        }
    }

    /// <summary>The state, the health entry, and for a failure the audit entry.</summary>
    private async Task FinishAsync(Guid tenant, DirectoryRunResult result, string? deltaLink, DateTime started, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var state = await db.DirectorySyncState.SingleAsync(s => s.TenantId == tenant, ct);
        var now = Clock.UtcNow;
        state.LastFinishedAt = now;
        state.LastStatus = result.Ok ? DirectoryRunStatus.Ok : DirectoryRunStatus.Failed;
        state.LastError = result.Error;
        state.LastMode = result.Mode;
        state.CreatedCount = result.Created;
        state.UpdatedCount = result.Updated;
        state.DeactivatedCount = result.Deactivated;
        state.SeenCount = result.Seen;
        state.LinkedCount = result.Linked;
        if (result.Ok)
        {
            state.DeltaLink = deltaLink;
            state.LastSucceededAt = now;
            if (result.Mode == "full")
            {
                state.LastFullAt = started;
            }
        }
        else
        {
            db.AuditLog.Add(AuditLog.Entry(AuditEvents.DirectorySyncFailed, AuditWho.DirectoryJob,
                $"The {result.Mode} run failed after {result.Seen} accounts: {result.Error}", outcome: AuditOutcome.Failed));
        }
        await db.SaveChangesAsync(ct);
        var summary = result.Ok
            ? $"{result.Mode}: {result.Created} created, {result.Updated} updated, {result.Deactivated} deactivated of {result.Seen}"
            : $"failed: {result.Error}";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO key_values (key, value, updated_at) VALUES ({HealthKey}, {GraphTokenCache.Cut(summary, 1000)}, {now})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """, ct);
    }

    /// <summary>One page in one write unit: upserts, deactivations, links. Unchanged persons stay as they are (no seq).</summary>
    private async Task ApplyPageAsync(Guid tenant, IReadOnlyList<GraphUser> page, Counts counts, string correlation, CancellationToken ct)
    {
        if (page.Count == 0)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var write = await new SyncWriter(db).BeginAsync(System, ChangeSource.Directory, correlation, ct);
        using var _ = ChangeContext.Use(new ChangeContext(null, ChangeSource.Directory, correlation));
        var oids = page.Select(u => u.Id).Distinct().ToList();
        var persons = await db.Persons.Where(p => p.EntraTenantId == tenant && p.EntraObjectId != null && oids.Contains(p.EntraObjectId.Value))
            .ToDictionaryAsync(p => p.EntraObjectId!.Value, ct);
        var users = await db.Users.AsNoTracking().Where(u => u.MicrosoftTenantId == tenant && u.MicrosoftObjectId != null && oids.Contains(u.MicrosoftObjectId.Value))
            .Select(u => new { u.Id, Oid = u.MicrosoftObjectId!.Value, u.Username }).ToDictionaryAsync(u => u.Oid, ct);
        var userIds = users.Values.Select(u => (Guid?)u.Id).ToList();
        var ownPersons = await db.Persons.Where(p => userIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId!.Value, ct);
        var now = Clock.UtcNow;
        var links = new List<(PersonEntity Person, Guid UserId, string Username, PersonEntity? Previous)>();
        foreach (var account in page.DistinctBy(u => u.Id))
        {
            counts.Seen.Add(account.Id);
            persons.TryGetValue(account.Id, out var person);
            var user = users.GetValueOrDefault(account.Id);
            if (account.Removed)
            {
                if (person is { IsActive: true })
                {
                    person.IsActive = false;
                    person.DirectorySyncedAt = now;
                    counts.Deactivated++;
                }
                continue;
            }
            var created = false;
            if (person is null)
            {
                if (user is not null && ownPersons.TryGetValue(user.Id, out var own) && own.Source == PersonSource.Manual)
                {
                    // The user's manual person becomes the directory's, keeping what it is assigned to (as at a Microsoft sign-in).
                    person = own;
                    person.Source = PersonSource.Microsoft;
                    person.EntraTenantId = tenant;
                    person.EntraObjectId = account.Id;
                }
                else
                {
                    person = new PersonEntity
                    {
                        Id = Ids.New(), Source = PersonSource.Microsoft, EntraTenantId = tenant, EntraObjectId = account.Id, DisplayName = "", IsActive = true,
                    };
                    db.Persons.Add(person);
                    created = true;
                }
                persons[account.Id] = person;
            }
            var wasActive = person.IsActive;
            Apply(person, account.Fields);
            if (user is not null && person.UserId != user.Id)
            {
                var previous = ownPersons.GetValueOrDefault(user.Id);
                links.Add((person, user.Id, user.Username, previous is not null && previous != person ? previous : null));
            }
            db.ChangeTracker.DetectChanges();
            if (created)
            {
                counts.Created++;
            }
            else if (db.Entry(person).State == EntityState.Modified)
            {
                if (wasActive && !person.IsActive)
                {
                    counts.Deactivated++;
                }
                else
                {
                    counts.Updated++;
                }
            }
            if (created || db.Entry(person).State == EntityState.Modified)
            {
                person.DirectorySyncedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var (person, userId, username, previous) in links)
        {
            // Every user has one person: a manual person the user had (and a Microsoft person of another account) lets go first.
            if (previous is not null)
            {
                previous.UserId = null;
                await db.SaveChangesAsync(ct);
            }
            person.UserId = userId;
            person.DirectorySyncedAt = now;
            counts.Linked++;
            AuditLog.Record(db, AuditEvents.PersonLinked, $"{person.DisplayName} is {username}'s person (from the directory)", AuditWho.DirectoryJob,
                AuditTarget.Person(person.Id, person.DisplayName));
            await db.SaveChangesAsync(ct);
        }
        await write.CommitAsync(ct);
    }

    /// <summary>A full run: every active Microsoft person of the tenant it didn't see is inactive now.</summary>
    private async Task<int> DeactivateUnseenAsync(Guid tenant, HashSet<Guid> seen, string correlation, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var write = await new SyncWriter(db).BeginAsync(System, ChangeSource.Directory, correlation, ct);
        using var _ = ChangeContext.Use(new ChangeContext(null, ChangeSource.Directory, correlation));
        var active = await db.Persons.Where(p => p.EntraTenantId == tenant && p.Source == PersonSource.Microsoft && p.IsActive && !p.Deleted)
            .ToListAsync(ct);
        var now = Clock.UtcNow;
        var gone = active.Where(p => p.EntraObjectId is not { } oid || !seen.Contains(oid)).ToList();
        foreach (var person in gone)
        {
            person.IsActive = false;
            person.DirectorySyncedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return gone.Count;
    }

    /// <summary>
    /// The properties a page carries onto the person (docs/03 §8.1): a missing key leaves the value; the e-mail lower-cased;
    /// the name falls back to the UPN; the first business phone; <c>accountEnabled = false</c> → inactive.
    /// </summary>
    internal static void Apply(PersonEntity person, JsonObject fields)
    {
        string? Text(string key, int max = 200) => fields[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t
            ? t.Length <= max ? t : t[..max]
            : null;

        if (fields.ContainsKey("givenName"))
        {
            person.GivenName = Text("givenName");
        }
        if (fields.ContainsKey("surname"))
        {
            person.Surname = Text("surname");
        }
        if (fields.ContainsKey("mail"))
        {
            person.Email = UserPersons.Email(Text("mail", 254));
        }
        if (fields.ContainsKey("userPrincipalName"))
        {
            person.UserPrincipalName = Text("userPrincipalName");
        }
        if (fields.ContainsKey("jobTitle"))
        {
            person.JobTitle = Text("jobTitle");
        }
        if (fields.ContainsKey("department"))
        {
            person.Department = Text("department");
        }
        if (fields.ContainsKey("companyName"))
        {
            person.CompanyName = Text("companyName");
        }
        if (fields.ContainsKey("mobilePhone"))
        {
            person.MobilePhone = Text("mobilePhone");
        }
        if (fields.ContainsKey("businessPhones"))
        {
            var first = (fields["businessPhones"] as JsonArray)?.Select(p => p is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p));
            person.Phone = first is null ? null : first.Length <= 200 ? first : first[..200];
        }
        if (fields.ContainsKey("userType"))
        {
            person.UserType = Text("userType") switch
            {
                { } t when t.Equals("Member", StringComparison.OrdinalIgnoreCase) => "Member",
                { } t when t.Equals("Guest", StringComparison.OrdinalIgnoreCase) => "Guest",
                _ => null,
            };
        }
        if (fields.ContainsKey("displayName") || person.DisplayName.Length == 0)
        {
            var name = Text("displayName") ?? person.UserPrincipalName ?? (person.DisplayName.Length > 0 ? person.DisplayName : null) ?? person.Email
                ?? person.EntraObjectId?.ToString() ?? "?";
            person.DisplayName = name;
        }
        if (fields["accountEnabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on))
        {
            person.IsActive = on;
        }
    }
}

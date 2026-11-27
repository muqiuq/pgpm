using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PGPM.Api.V1.Contracts;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;

namespace PGPM.Sync;

/// <summary>
/// Opens write units on synced rows (docs/03 §11.5, PLM's SyncService.PushAsync): the sync push, the sharing and owner
/// endpoints, restore and purge, later MCP tools and the directory job all write through a <see cref="SyncWrite"/>.
/// </summary>
public sealed class SyncWriter(PgpmDbContext db)
{
    /// <summary>
    /// One transaction holding the counter's lock (taken first, before anything is read), for <paramref name="viewer"/>'s
    /// changes from <paramref name="source"/>. 503 busy after ten seconds.
    /// </summary>
    public async Task<SyncWrite> BeginAsync(Viewer viewer, ChangeSource source, string? correlationId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await SyncCounter.LockAsync(db, ct);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
        return new SyncWrite(db, transaction, viewer, source, correlationId);
    }
}

/// <summary>
/// One write transaction on synced rows: mutations each in a savepoint (a refused one never aborts the batch), their
/// results kept for replays (<c>sync_mutations</c>), field conflicts collected for the response, change-log entries,
/// delete batches. <see cref="CommitAsync"/> commits; the change feed is published by <see cref="SyncCommitInterceptor"/>.
/// </summary>
public sealed class SyncWrite : IAsyncDisposable
{
    private const string Savepoint = "pgpm_mutation";

    internal static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IDbContextTransaction transaction;
    private readonly List<SyncConflict> conflicts = [];
    private readonly string? correlationId;
    private readonly Dictionary<Guid, string?> names = [];

    internal SyncWrite(PgpmDbContext db, IDbContextTransaction transaction, Viewer viewer, ChangeSource source, string? correlationId)
    {
        Db = db;
        this.transaction = transaction;
        Viewer = viewer;
        Source = source;
        this.correlationId = correlationId;
        Access = new AccessService(db);
    }

    public PgpmDbContext Db { get; }

    public Viewer Viewer { get; }

    public ChangeSource Source { get; }

    public AccessService Access { get; }

    /// <summary>The batch of the mutation being applied (initial shares belong to an item of the same batch).</summary>
    public Guid? CurrentBatch { get; private set; }

    public IReadOnlyList<SyncConflict> Conflicts => conflicts;

    /// <summary>Who changes rows now: the user, the source, the request, and (in a push) the mutation's client time.</summary>
    public ChangeContext Context(DateTime? clientChangedAt = null) =>
        new(Viewer.UserId, Source, correlationId, clientChangedAt is { } at ? Clock.AsUtc(at) : null);

    /// <summary>
    /// One mutation (docs/03 §11.5 step 2): a known mutation id answers with the stored result; otherwise the table applies
    /// it inside a savepoint. A <see cref="SyncRejection"/> or an integrity error rolls the mutation back and is its result.
    /// Lock timeouts and other errors end the request.
    /// </summary>
    public async Task<SyncMutationResult> ApplyAsync(SyncMutationIn m, Guid? clientId, CancellationToken ct)
    {
        using var _ = ChangeContext.Use(Context(m.ClientChangedAt));
        CurrentBatch = m.Batch;
        var stored = await Db.SyncMutations.AsNoTracking().FirstOrDefaultAsync(x => x.MutationId == m.MutationId, ct);
        if (stored is not null)
        {
            if (stored.UserId != Viewer.UserId)
            {
                return new SyncRejection(SyncCodes.DuplicateId, "This mutation id is taken.").ToResult(m);
            }
            var replay = JsonSerializer.Deserialize<StoredResult>(stored.Result, StoredJson)!;
            conflicts.AddRange(replay.Conflicts ?? []);
            return replay.Result;
        }
        if (m.MutationId == Guid.Empty)
        {
            return new SyncRejection(SyncCodes.ValidationFailed, "A mutation has a mutationId.").ToResult(m);
        }
        var before = conflicts.Count;
        await transaction.CreateSavepointAsync(Savepoint, ct);
        SyncMutationResult result;
        try
        {
            var table = SyncTables.Find(m.Table) ?? throw new SyncRejection(SyncCodes.NotSupported, $"No synced table {m.Table}.");
            result = m.Op switch
            {
                SyncOp.Insert => await table.InsertAsync(this, m, ct),
                SyncOp.Patch => await table.PatchAsync(this, m, ct),
                SyncOp.Delete => await table.DeleteAsync(this, m, ct),
                _ => throw new SyncRejection(SyncCodes.NotSupported, $"No operation {m.Op}."),
            };
        }
        catch (SyncRejection rejection)
        {
            await RollBackAsync(before, ct);
            result = rejection.ToResult(m);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: var state } && state.StartsWith("23", StringComparison.Ordinal))
        {
            // The database's own checks (a constraint the validators missed): the mutation is refused, the batch goes on.
            await RollBackAsync(before, ct);
            result = new SyncRejection(state == PostgresErrorCodes.UniqueViolation ? SyncCodes.DuplicateId : SyncCodes.ValidationFailed,
                $"The database refused the row ({((PostgresException)e.InnerException).ConstraintName ?? state}).").ToResult(m);
        }
        Db.ChangeTracker.Clear();
        Db.SyncMutations.Add(new SyncMutationEntity
        {
            MutationId = m.MutationId,
            UserId = Viewer.UserId,
            ClientId = clientId,
            BatchId = m.Batch,
            TableName = m.Table.Length > 100 ? m.Table[..100] : m.Table,
            RowId = m.Id,
            Op = m.Op.ToString().ToLowerInvariant(),
            Status = result.Status.ToString().ToLowerInvariant(),
            Result = JsonSerializer.Serialize(new StoredResult(result, conflicts.Skip(before).ToList()), StoredJson),
            ProcessedAt = Clock.UtcNow,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        await transaction.ReleaseSavepointAsync(Savepoint, ct);
        return result;
    }

    private async Task RollBackAsync(int conflictsBefore, CancellationToken ct)
    {
        await transaction.RollbackToSavepointAsync(Savepoint, ct);
        Db.ChangeTracker.Clear();
        conflicts.RemoveRange(conflictsBefore, conflicts.Count - conflictsBefore);
    }

    /// <summary>
    /// A conflict the user decided (docs/03 §11.8, request <c>resolved[]</c>): a <c>conflict_resolved</c> entry, for a row
    /// they may see; <c>field: null</c> for the delete kinds (the whole row). Anything else is ignored: it is a record,
    /// not a change.
    /// </summary>
    public async Task ResolveAsync(SyncResolution resolution, CancellationToken ct)
    {
        var field = resolution.Field;
        if (SyncTables.Find(resolution.Table ?? "") is not { } table || resolution.Id is not { } id
            || field is { Length: 0 or > 200 }
            || resolution.Resolution is not ("mine" or "theirs" or "edited")
            || await table.FindVisibleAsync(this, id, ct) is not { } row)
        {
            return;
        }
        using var _ = ChangeContext.Use(Context());
        Log(table, id, table.ItemIdOf(row), ChangeActions.ConflictResolved, new JsonObject { ["field"] = field, ["resolution"] = resolution.Resolution });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
    }

    public void AddConflict(SyncConflict conflict) => conflicts.Add(conflict);

    /// <summary>A change-log entry the interceptor can't infer (share, conflict, conflict_resolved), saved with the next save.</summary>
    public void Log(SyncTable table, Guid entityId, Guid? itemId, string action, JsonNode changes)
    {
        var who = ChangeContext.Current;
        Db.ChangeLog.Add(new ChangeLogEntity
        {
            At = Clock.UtcNow,
            UserId = who.UserId ?? Viewer.UserId,
            Source = who.Source,
            Entity = table.Name,
            EntityId = entityId,
            ItemId = itemId,
            Action = action,
            Changes = changes.ToJsonString(WireJson.Options),
            ClientChangedAt = who.ClientChangedAt,
            CorrelationId = who.CorrelationId ?? correlationId,
        });
    }

    /// <summary>A new delete batch (docs/02 §11.3), added to the context.</summary>
    public DeleteBatchEntity NewBatch(DeleteRootKind kind, string table, Guid rootId)
    {
        var batch = new DeleteBatchEntity
        {
            Id = Ids.New(), RootKind = kind, RootTable = table, RootId = rootId, DeletedAt = Clock.UtcNow, DeletedBy = Viewer.UserId,
        };
        Db.DeleteBatches.Add(batch);
        return batch;
    }

    /// <summary>The item was inserted by this user in <paramref name="batch"/> (this request or an earlier one).</summary>
    public async Task<bool> InsertedInBatchAsync(Guid? batch, string table, Guid rowId, CancellationToken ct) =>
        batch is { } b && await Db.SyncMutations.AnyAsync(x => x.UserId == Viewer.UserId && x.BatchId == b && x.TableName == table && x.RowId == rowId
            && x.Op == "insert" && x.Status == "accepted", ct);

    /// <summary>
    /// Who last changed a field, and when (the conflict's <c>by</c>/<c>at</c>): the newest change-log entry naming it, else
    /// the row's last change.
    /// </summary>
    public async Task<(string? By, DateTime? At)> LastChangeAsync(SyncTable table, ISyncedEntity row, string key, CancellationToken ct)
    {
        var field = key.Split('.')[0];
        var pattern = new JsonArray(new JsonObject { ["field"] = field }).ToJsonString();
        var entry = await Db.ChangeLog.FromSql($"""
            SELECT * FROM change_log WHERE entity = {table.Name} AND entity_id = {row.Id} AND action IN ('create', 'update')
            AND changes @> CAST({pattern} AS jsonb)
            """).AsNoTracking().OrderByDescending(e => e.Id).Select(e => new { e.UserId, e.At }).FirstOrDefaultAsync(ct);
        var userId = entry is null ? row.UpdatedBy : entry.UserId;
        return (await NameOfAsync(userId, ct), entry?.At ?? row.UpdatedAt);
    }

    public async Task<string?> NameOfAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is not { } id)
        {
            return null;
        }
        if (!names.TryGetValue(id, out var name))
        {
            name = await Db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            names[id] = name;
        }
        return name;
    }

    public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

    public ValueTask DisposeAsync() => transaction.DisposeAsync();

    /// <summary>What <c>sync_mutations.result</c> holds: the answer and its field conflicts, replayed on a retry.</summary>
    internal sealed record StoredResult(SyncMutationResult Result, List<SyncConflict>? Conflicts);
}

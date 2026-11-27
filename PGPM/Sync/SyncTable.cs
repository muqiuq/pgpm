using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Sharing;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Sync;

/// <summary>The rejection codes of a mutation (docs/03 §11.4 "wire format").</summary>
public static class SyncCodes
{
    public const string MissingAccess = "missing_access";
    public const string MissingRight = "missing_right";
    public const string ParentMissing = "parent_missing";
    public const string ValidationFailed = "validation_failed";
    public const string NotPatchable = "not_patchable";
    public const string DuplicateId = "duplicate_id";
    public const string NotSupported = "not_supported";

    /// <summary>Conflict kinds: a field changed by someone else, a patch of a deleted row, a delete of a changed row.</summary>
    public const string Field = "field";
    public const string DeletedByOther = "deleted_by_other";
    public const string ChangedSinceDelete = "changed_since_delete";
}

/// <summary>A mutation the server refuses (<c>rejected</c>): rolled back to its savepoint, the batch goes on.</summary>
public sealed class SyncRejection(string code, string message, JsonObject? current = null, IDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public JsonObject? Current { get; } = current;

    public IDictionary<string, string[]>? Errors { get; } = errors;

    public SyncMutationResult ToResult(SyncMutationIn m) => new()
    {
        MutationId = m.MutationId,
        Status = SyncStatus.Rejected,
        Id = m.Id,
        Code = Code,
        Message = Message,
        Errors = Errors,
        Current = Current,
    };
}

/// <summary>
/// A row's parent (docs/02 §11.5): visibility follows it, a delete of the parent tombstones the row in the same batch when
/// <see cref="DeletedWithParent"/>, a restore skips the row while its parent is gone, a re-stamp of the parent's subtree
/// includes it, a purge removes it.
/// </summary>
public sealed record ParentRef(string Table, string ForeignKey, bool DeletedWithParent);

/// <summary>What a pull reads with: the snapshot's context, the user, their access.</summary>
public sealed record PullScope(PgpmDbContext Db, Viewer Viewer, AccessService Access);

/// <summary>
/// One synced table (docs/03 §11.5, §11.6): its wire name, what a user may see, what a client may insert, patch and delete,
/// and how. <see cref="SyncTables.All"/> lists them; adding a table is one descriptor (a subclass of
/// <see cref="SyncTable{T}"/>), its entity and its migration.
/// </summary>
public abstract class SyncTable(string name, Type entityType)
{
    protected static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The table's name, which is its wire name: <c>items</c>, <c>item_shares</c>.</summary>
    public string Name { get; } = name;

    public Type EntityType { get; } = entityType;

    /// <summary>[S, ro]: written by the server only (admin endpoints, the directory job).</summary>
    public virtual bool ReadOnly => false;

    /// <summary>Changes go to <c>change_log</c> (<see cref="ChangeLogInterceptor"/>).</summary>
    public virtual bool History => true;

    /// <summary>The kind of delete batch a client's delete of a row opens: items and maps are restorable from the Trash.</summary>
    public virtual DeleteRootKind DeleteRoot => DeleteRootKind.Other;

    /// <summary>Wire keys that are never delivered.</summary>
    public virtual IReadOnlySet<string> Hidden => None;

    /// <summary>Wire keys a client's insert can't set: the server does (number, owner, …).</summary>
    public virtual IReadOnlySet<string> ServerOwned => None;

    /// <summary>The patch whitelist: wire keys, and <c>key.*</c> for the members of a jsonb column.</summary>
    public virtual IReadOnlySet<string> Patchable => None;

    public virtual IReadOnlyList<ParentRef> Parents => [];

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<WireColumn>> ColumnCache = new();

    public IReadOnlyList<WireColumn> Columns(PgpmDbContext db) =>
        ColumnCache.GetOrAdd(EntityType, t => WireColumn.Of(db.Model.FindEntityType(t) ?? throw new InvalidOperationException($"{t.Name} is not mapped.")));

    public WireColumn? Column(PgpmDbContext db, string key) => Columns(db).FirstOrDefault(c => c.Key == key);

    /// <summary>The table and column names of the database, for the bulk statements (re-stamp, purge).</summary>
    public string SqlTable(PgpmDbContext db) => db.Model.FindEntityType(EntityType)!.GetTableName()!;

    /// <summary>The root item of a row (History, editing), if it belongs to one.</summary>
    public virtual Guid? ItemIdOf(ISyncedEntity row) => null;

    /// <summary>The row as the wire has it, without computed columns: a tombstone is <c>{id, seq, rev, deleted: true}</c>.</summary>
    public JsonObject ToWire(PgpmDbContext db, ISyncedEntity row, bool withBatch = false)
    {
        if (row.Deleted)
        {
            var tombstone = new JsonObject { ["id"] = row.Id, ["seq"] = row.Seq, ["rev"] = row.Rev, ["deleted"] = true };
            if (withBatch)
            {
                tombstone["deleteBatchId"] = row.DeleteBatchId;
            }
            return tombstone;
        }
        var json = new JsonObject();
        foreach (var column in Columns(db))
        {
            if (!Hidden.Contains(column.Key))
            {
                json[column.Key] = WireJson.ToNode(column.Get(row), column);
            }
        }
        return json;
    }

    /// <summary>A page of the rows the user may see with a seq above <paramref name="since"/>, in seq order, as the wire has them.</summary>
    public abstract Task<List<(long Seq, JsonObject Row)>> PageAsync(PullScope scope, long since, int limit, CancellationToken ct);

    public abstract Task<SyncMutationResult> InsertAsync(SyncWrite w, SyncMutationIn m, CancellationToken ct);

    public abstract Task<SyncMutationResult> PatchAsync(SyncWrite w, SyncMutationIn m, CancellationToken ct);

    public abstract Task<SyncMutationResult> DeleteAsync(SyncWrite w, SyncMutationIn m, CancellationToken ct);

    /// <summary>The row, tracked, if the user may see it (tombstones included).</summary>
    public abstract Task<ISyncedEntity?> FindVisibleAsync(SyncWrite w, Guid id, CancellationToken ct);

    /// <summary>The row, tracked, whoever may see it.</summary>
    public abstract Task<ISyncedEntity?> FindAsync(PgpmDbContext db, Guid id, CancellationToken ct);

    /// <summary>The rows (tracked) whose <paramref name="parent"/> foreign key is <paramref name="parentId"/>; live ones only if asked.</summary>
    public abstract Task<List<ISyncedEntity>> ChildrenAsync(PgpmDbContext db, ParentRef parent, Guid parentId, bool liveOnly, CancellationToken ct);

    /// <summary>The rows (tracked) deleted in a batch.</summary>
    public abstract Task<List<ISyncedEntity>> InBatchAsync(PgpmDbContext db, Guid batchId, CancellationToken ct);

    /// <summary>Another live row with the same unique pair (a restore skips the row then).</summary>
    public abstract Task<bool> HasLiveDuplicateAsync(PgpmDbContext db, ISyncedEntity row, CancellationToken ct);

    /// <summary>The user may delete (and so restore) the row: throws <see cref="SyncRejection"/> otherwise.</summary>
    public abstract Task AuthorizeDeleteAsync(SyncWrite w, ISyncedEntity row, CancellationToken ct);

    /// <summary>The row with the computed columns for the user: the <c>current</c> of a result.</summary>
    public abstract Task<JsonObject> CurrentAsync(SyncWrite w, ISyncedEntity row, CancellationToken ct);

    /// <summary>
    /// Tombstones the row in the batch, and the rows below it that go with it (<see cref="ParentRef.DeletedWithParent"/>),
    /// recursively. Saved by the caller.
    /// </summary>
    public async Task MarkDeletedAsync(SyncWrite w, ISyncedEntity row, Guid batchId, CancellationToken ct)
    {
        row.Deleted = true;
        row.DeleteBatchId = batchId;
        foreach (var (child, parent) in SyncTables.ChildrenOf(Name))
        {
            if (!parent.DeletedWithParent)
            {
                continue;
            }
            foreach (var row2 in await child.ChildrenAsync(w.Db, parent, row.Id, liveOnly: true, ct))
            {
                await child.MarkDeletedAsync(w, row2, batchId, ct);
            }
        }
    }

    /// <summary>A parent of the row is missing or deleted: a restore leaves the row in the trash.</summary>
    public async Task<bool> ParentGoneAsync(PgpmDbContext db, ISyncedEntity row, CancellationToken ct)
    {
        foreach (var parent in Parents)
        {
            if (Column(db, parent.ForeignKey)?.Get(row) is not Guid parentId)
            {
                continue;
            }
            var table = SyncTables.Get(parent.Table);
            if (await table.FindAsync(db, parentId, ct) is not { Deleted: false })
            {
                return true;
            }
        }
        return false;
    }

    protected static SyncRejection Invalid(string field, string message, JsonObject? current = null) =>
        new(SyncCodes.ValidationFailed, "One or more fields are invalid.", current, new Dictionary<string, string[]> { [field] = [message] });

    protected static SyncRejection Invalid(IDictionary<string, string[]> errors, JsonObject? current = null) =>
        new(SyncCodes.ValidationFailed, "One or more fields are invalid.", current, errors);
}

/// <summary>How an insert of a row whose unique pair exists already is merged (docs/03 §11.5).</summary>
public enum DuplicateMerge
{
    /// <summary><c>accepted</c> with <c>mergedInto</c> the existing row; the client rewrites its references.</summary>
    MergedInto,

    /// <summary>A patch of the existing row's <see cref="SyncTable{T}.MergeKeys"/> with <c>old</c> = null (field values: may conflict).</summary>
    PatchFromNull,

    /// <summary>A patch with <c>old</c> = the existing value: the later insert wins (personal settings).</summary>
    PatchOverwrite,
}

/// <summary>
/// The generic part of a synced table: pull pages, insert, patch (per field, conflicting keys not applied), delete
/// (batches, cascades, <c>force</c>), with hooks for the table's rules: who may see it (<see cref="Visible"/>), may
/// insert/patch/delete, what the server sets, what is valid, which pair is unique.
/// </summary>
public abstract class SyncTable<T>(string name) : SyncTable(name, typeof(T)) where T : class, ISyncedEntity
{
    /// <summary>A new empty row for an insert.</summary>
    protected abstract T Create();

    /// <summary>The rows the user may see, tombstones included.</summary>
    public abstract IQueryable<T> Visible(IQueryable<T> rows, Viewer viewer, AccessService access);

    protected virtual Guid? ItemOf(T row) => null;

    public sealed override Guid? ItemIdOf(ISyncedEntity row) => ItemOf((T)row);

    /// <summary>Adds the computed columns (<c>access</c>) to delivered rows; tombstones have none.</summary>
    protected virtual Task AddComputedAsync(PullScope scope, IReadOnlyList<(T Row, JsonObject Wire)> rows, CancellationToken ct) => Task.CompletedTask;

    /// <summary>May the user insert it? Throws a <see cref="SyncRejection"/> otherwise. Default: no client inserts.</summary>
    protected virtual Task AuthorizeInsertAsync(SyncWrite w, T row, SyncMutationIn m, CancellationToken ct) =>
        throw new SyncRejection(SyncCodes.NotSupported, $"Rows of {Name} are not created by clients.");

    /// <summary>The server-owned values of a new row (number, owner, …).</summary>
    protected virtual Task PrepareInsertAsync(SyncWrite w, T row, CancellationToken ct) => Task.CompletedTask;

    protected virtual Task AfterInsertAsync(SyncWrite w, T row, CancellationToken ct) => Task.CompletedTask;

    /// <summary>May the user patch these keys of the row? Throws otherwise (with the row as <c>current</c>).</summary>
    protected virtual Task AuthorizePatchAsync(SyncWrite w, T row, IReadOnlyCollection<string> keys, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Server-owned consequences of applied keys (a status change sets <c>status_changed_at</c>).</summary>
    protected virtual Task AfterPatchAsync(SyncWrite w, T row, IReadOnlyCollection<string> applied, CancellationToken ct) => Task.CompletedTask;

    protected virtual Task AuthorizeDeleteAsync(SyncWrite w, T row, CancellationToken ct) =>
        throw new SyncRejection(SyncCodes.NotSupported, $"Rows of {Name} are not deleted by clients.");

    public sealed override Task AuthorizeDeleteAsync(SyncWrite w, ISyncedEntity row, CancellationToken ct) => AuthorizeDeleteAsync(w, (T)row, ct);

    /// <summary>The row's checks after an insert or patch, by wire key.</summary>
    protected virtual void Validate(T row, ApiValidator v)
    {
    }

    /// <summary>A key of the whitelist; <c>key.*</c> admits the members of a jsonb column. Per row where the row decides.</summary>
    protected virtual bool IsPatchable(T row, string key, PgpmDbContext db)
    {
        if (Patchable.Contains(key))
        {
            return true;
        }
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && Patchable.Contains(key[..dot] + ".*") && Column(db, key[..dot]) is { IsJson: true }
            && key.Split('.').All(p => p.Length > 0 && p != "__proto__");
    }

    /// <summary>The live row with the same unique pair, if the table has one.</summary>
    protected virtual Expression<Func<T, bool>>? SamePair(T row) => null;

    protected virtual DuplicateMerge Merge => DuplicateMerge.MergedInto;

    /// <summary>The keys a duplicate insert patches into the existing row (<see cref="DuplicateMerge.PatchFromNull"/>, <see cref="DuplicateMerge.PatchOverwrite"/>).</summary>
    protected virtual IReadOnlyList<string> MergeKeys => [];

    protected DbSet<T> Set(PgpmDbContext db) => db.Set<T>();

    // --- reading ------------------------------------------------------------------------------------------------------

    public override async Task<List<(long Seq, JsonObject Row)>> PageAsync(PullScope scope, long since, int limit, CancellationToken ct)
    {
        var rows = await Visible(Set(scope.Db).IgnoreQueryFilters().AsNoTracking(), scope.Viewer, scope.Access)
            .Where(r => r.Seq > since).OrderBy(r => r.Seq).Take(limit).ToListAsync(ct);
        var wire = rows.Select(r => (Row: r, Wire: ToWire(scope.Db, r))).ToList();
        await AddComputedAsync(scope, wire.Where(r => !r.Row.Deleted).ToList(), ct);
        return wire.Select(r => (r.Row.Seq, r.Wire)).ToList();
    }

    public override async Task<ISyncedEntity?> FindVisibleAsync(SyncWrite w, Guid id, CancellationToken ct) =>
        await Visible(Set(w.Db).IgnoreQueryFilters(), w.Viewer, w.Access).FirstOrDefaultAsync(r => r.Id == id, ct);

    public override async Task<ISyncedEntity?> FindAsync(PgpmDbContext db, Guid id, CancellationToken ct) =>
        await Set(db).IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct);

    public override async Task<List<ISyncedEntity>> ChildrenAsync(PgpmDbContext db, ParentRef parent, Guid parentId, bool liveOnly, CancellationToken ct)
    {
        var rows = Set(db).IgnoreQueryFilters().Where(ForeignKeyIs(Column(db, parent.ForeignKey)!.PropertyName, parentId));
        if (liveOnly)
        {
            rows = rows.Where(r => !r.Deleted);
        }
        return [.. await rows.ToListAsync(ct)];
    }

    public override async Task<List<ISyncedEntity>> InBatchAsync(PgpmDbContext db, Guid batchId, CancellationToken ct) =>
        [.. await Set(db).IgnoreQueryFilters().Where(r => r.DeleteBatchId == batchId && r.Deleted).OrderBy(r => r.Seq).ToListAsync(ct)];

    public override async Task<bool> HasLiveDuplicateAsync(PgpmDbContext db, ISyncedEntity row, CancellationToken ct) =>
        SamePair((T)row) is { } pair && await Set(db).IgnoreQueryFilters().Where(r => r.Id != row.Id && !r.Deleted).AnyAsync(pair, ct);

    public override async Task<JsonObject> CurrentAsync(SyncWrite w, ISyncedEntity row, CancellationToken ct)
    {
        var wire = ToWire(w.Db, row, withBatch: true);
        if (!row.Deleted)
        {
            await AddComputedAsync(new PullScope(w.Db, w.Viewer, w.Access), [((T)row, wire)], ct);
        }
        return wire;
    }

    /// <summary><c>row => row.&lt;property&gt; == id</c>, for a foreign key of either nullability.</summary>
    private static Expression<Func<T, bool>> ForeignKeyIs(string property, Guid id)
    {
        var row = Expression.Parameter(typeof(T), "r");
        var member = Expression.Property(row, property);
        var value = member.Type == typeof(Guid) ? (Expression)Expression.Constant(id) : Expression.Constant((Guid?)id, typeof(Guid?));
        return Expression.Lambda<Func<T, bool>>(Expression.Equal(member, value), row);
    }

    // --- insert -------------------------------------------------------------------------------------------------------

    public override async Task<SyncMutationResult> InsertAsync(SyncWrite w, SyncMutationIn m, CancellationToken ct)
    {
        if (ReadOnly)
        {
            throw new SyncRejection(SyncCodes.NotSupported, $"{Name} is read-only.");
        }
        if (m.Row is not { } input)
        {
            throw Invalid("row", "An insert carries its row.");
        }
        if (!Ids.IsVersion7(m.Id))
        {
            throw Invalid("id", "A new row's id is a UUID v7.");
        }
        if (input.TryGetPropertyValue("id", out var idNode) && idNode is not null
            && (idNode.GetValueKind() != JsonValueKind.String || !Guid.TryParse(idNode.GetValue<string>(), out var rowId) || rowId != m.Id))
        {
            throw Invalid("id", "The row's id is the mutation's.");
        }
        if (await Set(w.Db).IgnoreQueryFilters().AnyAsync(r => r.Id == m.Id, ct))
        {
            throw new SyncRejection(SyncCodes.DuplicateId, $"A row of {Name} with this id exists already.");
        }
        var row = Create();
        row.Id = m.Id;
        var v = new ApiValidator();
        foreach (var column in Columns(w.Db))
        {
            if (column.Key == "id" || SyncedEntity.WireColumns.Contains(column.Key) || ServerOwned.Contains(column.Key) || Hidden.Contains(column.Key)
                || !input.TryGetPropertyValue(column.Key, out var node))
            {
                continue;
            }
            if (Read(node, column, out var value, out var error))
            {
                column.Set(row, value);
            }
            else
            {
                v.Add(column.Key, error);
            }
        }
        ThrowIfInvalid(v, null);
        await AuthorizeInsertAsync(w, row, m, ct);
        await PrepareInsertAsync(w, row, ct);
        Validate(row, v);
        ThrowIfInvalid(v, null);
        if (SamePair(row) is { } pair && await Set(w.Db).IgnoreQueryFilters().Where(r => !r.Deleted).FirstOrDefaultAsync(pair, ct) is { } existing)
        {
            return await MergeAsync(w, m, row, existing, ct);
        }
        Set(w.Db).Add(row);
        await AfterInsertAsync(w, row, ct);
        await w.Db.SaveChangesAsync(ct);
        return Accepted(m, row);
    }

    private async Task<SyncMutationResult> MergeAsync(SyncWrite w, SyncMutationIn m, T row, T existing, CancellationToken ct)
    {
        if (Merge == DuplicateMerge.MergedInto)
        {
            return Accepted(m, existing) with { Id = m.Id, MergedInto = existing.Id };
        }
        var changes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var key in MergeKeys)
        {
            var column = Column(w.Db, key)!;
            changes[key] = new JsonObject
            {
                ["old"] = Merge == DuplicateMerge.PatchOverwrite ? WireJson.ToNode(column.Get(existing), column) : null,
                ["new"] = WireJson.ToNode(column.Get(row), column),
            };
        }
        var patched = await ApplyChangesAsync(w, m, existing, changes, ct);
        return patched with { Id = m.Id, MergedInto = existing.Id };
    }

    // --- patch --------------------------------------------------------------------------------------------------------

    public override async Task<SyncMutationResult> PatchAsync(SyncWrite w, SyncMutationIn m, CancellationToken ct)
    {
        if (ReadOnly)
        {
            throw new SyncRejection(SyncCodes.NotPatchable, $"{Name} is read-only.");
        }
        if (m.Changes is not { Count: > 0 } changes)
        {
            throw Invalid("changes", "A patch changes at least one key.");
        }
        var row = (T?)await FindVisibleAsync(w, m.Id, ct)
            ?? throw new SyncRejection(SyncCodes.MissingAccess, "The row doesn't exist or you may not see it.");
        if (row.Deleted)
        {
            return Conflict(m, row, SyncCodes.DeletedByOther, await CurrentAsync(w, row, ct));
        }
        return await ApplyChangesAsync(w, m, row, changes, ct);
    }

    /// <summary>
    /// Per key (docs/03 §11.5): current = old → applied; current = new → nothing to do; otherwise a conflict – the key is
    /// not applied and reported with their value and who changed it when. Keys not in the whitelist refuse the mutation.
    /// </summary>
    private async Task<SyncMutationResult> ApplyChangesAsync(SyncWrite w, SyncMutationIn m, T row, IReadOnlyDictionary<string, JsonObject> changes,
        CancellationToken ct)
    {
        var refused = changes.Keys.Where(k => !IsPatchable(row, k, w.Db)).ToList();
        if (refused.Count > 0)
        {
            throw new SyncRejection(SyncCodes.NotPatchable, $"Not patchable in {Name}: {string.Join(", ", refused)}.", await CurrentAsync(w, row, ct));
        }
        await AuthorizePatchAsync(w, row, changes.Keys.ToList(), ct);
        var applied = new List<string>();
        var conflicts = new List<(string Key, JsonNode? Theirs)>();
        var v = new ApiValidator();
        foreach (var (key, change) in changes)
        {
            var (column, path) = Resolve(w.Db, key);
            // A member without "new" is removed; a column without it is set to null.
            var hasNew = change.TryGetPropertyValue("new", out var newNode) || path is null;
            change.TryGetPropertyValue("old", out var oldNode);
            var current = ValueAt(row, column, path);
            var wanted = path is null ? WireJson.Normalize(newNode, column) : newNode;
            if (hasNew ? WireJson.Same(current, wanted) : current is null)
            {
                continue;
            }
            var seen = path is null ? WireJson.Normalize(oldNode, column) : oldNode;
            if (!WireJson.Same(current, seen))
            {
                conflicts.Add((key, current));
                continue;
            }
            if (path is null)
            {
                if (Read(newNode, column, out var value, out var error))
                {
                    column.Set(row, value);
                    applied.Add(key);
                }
                else
                {
                    v.Add(key, error);
                }
            }
            else
            {
                column.Set(row, SetAt(column.Get(row) as string, path, newNode?.DeepClone(), remove: !hasNew));
                applied.Add(key);
            }
        }
        if (applied.Count > 0)
        {
            Validate(row, v);
        }
        if (v.HasErrors)
        {
            ThrowIfInvalid(v, await CurrentAsync(w, row, ct));
        }
        if (applied.Count > 0)
        {
            await AfterPatchAsync(w, row, applied, ct);
            await w.Db.SaveChangesAsync(ct);
        }
        foreach (var (key, theirs) in conflicts)
        {
            var (by, at) = await w.LastChangeAsync(this, row, key, ct);
            w.AddConflict(new SyncConflict(m.MutationId, Name, row.Id, key, theirs?.DeepClone(), by, at, row.Rev));
            var yours = changes[key].TryGetPropertyValue("new", out var mine) ? mine?.DeepClone() : null;
            w.Log(this, row.Id, ItemOf(row), ChangeActions.Conflict,
                new JsonObject { ["field"] = key, ["yours"] = yours, ["theirs"] = theirs?.DeepClone(), ["by"] = by });
        }
        if (conflicts.Count > 0)
        {
            await w.Db.SaveChangesAsync(ct);
        }
        var result = Accepted(m, row);
        if (conflicts.Count == 0)
        {
            return result;
        }
        var fields = conflicts.Select(c => c.Key).ToList();
        return applied.Count > 0
            ? result with { Status = SyncStatus.Partial, ConflictFields = fields }
            : result with { Status = SyncStatus.Conflict, Kind = SyncCodes.Field, ConflictFields = fields, Current = await CurrentAsync(w, row, ct) };
    }

    private (WireColumn Column, string[]? Path) Resolve(PgpmDbContext db, string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (Column(db, key)!, null) : (Column(db, key[..dot])!, key[(dot + 1)..].Split('.'));
    }

    private static JsonNode? ValueAt(T row, WireColumn column, string[]? path)
    {
        var node = WireJson.ToNode(column.Get(row), column);
        foreach (var part in path ?? [])
        {
            node = node is JsonObject o && o.TryGetPropertyValue(part, out var child) ? child : null;
        }
        return node;
    }

    /// <summary>A jsonb column's text with a member set (objects created on the way) or removed.</summary>
    private static string SetAt(string? json, string[] path, JsonNode? value, bool remove)
    {
        var root = (json is null ? null : JsonNode.Parse(json)) as JsonObject ?? new JsonObject();
        var target = root;
        foreach (var part in path[..^1])
        {
            if (target[part] is not JsonObject next)
            {
                next = new JsonObject();
                target[part] = next;
            }
            target = next;
        }
        if (remove)
        {
            target.Remove(path[^1]);
        }
        else
        {
            target[path[^1]] = value;
        }
        return root.ToJsonString(WireJson.Options);
    }

    // --- delete -------------------------------------------------------------------------------------------------------

    public override async Task<SyncMutationResult> DeleteAsync(SyncWrite w, SyncMutationIn m, CancellationToken ct)
    {
        if (ReadOnly)
        {
            throw new SyncRejection(SyncCodes.NotSupported, $"{Name} is read-only.");
        }
        var row = (T?)await FindVisibleAsync(w, m.Id, ct)
            ?? throw new SyncRejection(SyncCodes.MissingAccess, "The row doesn't exist or you may not see it.");
        if (row.Deleted)
        {
            return Accepted(m, row);
        }
        await AuthorizeDeleteAsync(w, row, ct);
        if (m.BaseRev is { } baseRev && baseRev != row.Rev && m.Force != true)
        {
            return Conflict(m, row, SyncCodes.ChangedSinceDelete, await CurrentAsync(w, row, ct));
        }
        var batch = w.NewBatch(DeleteRoot, Name, row.Id);
        await MarkDeletedAsync(w, row, batch.Id, ct);
        await w.Db.SaveChangesAsync(ct);
        return Accepted(m, row);
    }

    // --- helpers ------------------------------------------------------------------------------------------------------

    private static bool Read(JsonNode? node, WireColumn column, out object? value, out string error)
    {
        try
        {
            value = WireJson.FromNode(node, column);
            error = "";
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            value = null;
            error = e is FormatException ? e.Message : $"Not a valid {Describe(column)}.";
            return false;
        }
    }

    private static string Describe(WireColumn column)
    {
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        return type.IsEnum ? $"value ({string.Join(", ", Enum.GetNames(type).Select(n => n.ToLowerInvariant()))})"
            : type == typeof(Guid) ? "uuid"
            : type == typeof(DateOnly) ? "date (yyyy-MM-dd)"
            : type == typeof(DateTime) ? "timestamp"
            : type == typeof(bool) ? "boolean"
            : type == typeof(string) ? "text"
            : type == typeof(List<string>) ? "list of texts"
            : "number";
    }

    private static void ThrowIfInvalid(ApiValidator v, JsonObject? current)
    {
        try
        {
            v.ThrowIfInvalid();
        }
        catch (ApiException e)
        {
            throw Invalid(e.Errors!, current);
        }
    }

    protected static SyncMutationResult Accepted(SyncMutationIn m, T row) =>
        new() { MutationId = m.MutationId, Status = SyncStatus.Accepted, Id = row.Id, Rev = row.Rev, Seq = row.Seq };

    private static SyncMutationResult Conflict(SyncMutationIn m, T row, string kind, JsonObject current) =>
        new() { MutationId = m.MutationId, Status = SyncStatus.Conflict, Id = row.Id, Rev = row.Rev, Seq = row.Seq, Kind = kind, Current = current };

    /// <summary>The access of a row's item for the user: rejection <c>missing_access</c> below <paramref name="needed"/>.</summary>
    protected async Task RequireItemAccessAsync(SyncWrite w, T row, Guid itemId, Items.ItemAccess needed, CancellationToken ct)
    {
        var access = await w.Access.ItemAccessAsync(w.Viewer, itemId, ct);
        if (access < needed)
        {
            throw new SyncRejection(SyncCodes.MissingAccess, $"You need {needed.ToString().ToLowerInvariant()} access to the item.",
                await CurrentAsync(w, row, ct));
        }
    }
}

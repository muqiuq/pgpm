namespace PGPM.Sync;

/// <summary>The single row of the global change counter (docs/02 §11.1), locked by every write of synced rows.</summary>
public sealed class SyncCounterEntity
{
    public int Id { get; set; }

    public long Value { get; set; }
}

/// <summary>The single row of the sync state (docs/02 §11.4): cursors below the horizon reset.</summary>
public sealed class SyncStateEntity
{
    public int Id { get; set; }

    /// <summary>The highest seq of a row that was physically deleted (a purge, a pruned tombstone or eviction).</summary>
    public long TombstoneHorizonSeq { get; set; }
}

/// <summary>What a delete batch's root is (docs/02 §11.3): items and maps are restorable from the Trash.</summary>
public enum DeleteRootKind { Item, Map, Other }

/// <summary>Rows deleted in one operation (docs/02 §11.3): the restore unit.</summary>
public sealed class DeleteBatchEntity
{
    public Guid Id { get; set; }

    public DeleteRootKind RootKind { get; set; }

    /// <summary>The root row's table (wire name, <c>items</c>): restore checks the right to delete it.</summary>
    public string RootTable { get; set; } = "";

    public Guid RootId { get; set; }

    public DateTime DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public DateTime? RestoredAt { get; set; }

    public Guid? RestoredBy { get; set; }

    public DateTime? PurgedAt { get; set; }

    public Guid? PurgedBy { get; set; }
}

/// <summary>
/// The idempotency log of the push (docs/02 §11.2): a known <c>mutationId</c> is answered with the stored result. Also says
/// which rows a batch inserted (initial shares: only for an item inserted in the same batch).
/// </summary>
public sealed class SyncMutationEntity
{
    public Guid MutationId { get; set; }

    public Guid UserId { get; set; }

    public Guid? ClientId { get; set; }

    public Guid? BatchId { get; set; }

    public string TableName { get; set; } = "";

    public Guid RowId { get; set; }

    public string Op { get; set; } = "";

    public string Status { get; set; } = "";

    /// <summary>The stored answer (the result and its field conflicts), jsonb.</summary>
    public string Result { get; set; } = "{}";

    public DateTime ProcessedAt { get; set; }
}

/// <summary>A device's sync state (docs/02 §11.4). Deleting the row makes the client reset.</summary>
public sealed class SyncClientEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string? UserAgent { get; set; }

    public string? AppVersion { get; set; }

    public DateTime LastSeenAt { get; set; }

    public long LastSeq { get; set; }

    public int PendingCount { get; set; }

    public int ConflictCount { get; set; }

    public bool LiveConnected { get; set; }

    /// <summary>
    /// The tombstone horizon when the device last pulled from 0: a horizon raised since then, above its cursor, means rows
    /// it may hold were deleted for good – a reset. A device paging through its first sync is never reset by an older purge.
    /// </summary>
    public long HorizonSeq { get; set; }
}

/// <summary>A user lost access to an item or map (docs/02 §11.4): their clients delete the root and everything below it.</summary>
public sealed class SyncEvictionEntity
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public long Seq { get; set; }

    public DeleteRootKind RootKind { get; set; }

    public Guid RootId { get; set; }

    public DateTime At { get; set; }
}

/// <summary>The actions of <c>change_log</c> (docs/02 §12), as stored.</summary>
public static class ChangeActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Restore = "restore";
    public const string Share = "share";
    public const string Conflict = "conflict";
    public const string ConflictResolved = "conflict_resolved";

    public static readonly string[] All = [Create, Update, Delete, Restore, Share, Conflict, ConflictResolved];
}

/// <summary>One change of a synced row, for the History (docs/02 §12 <c>change_log</c>).</summary>
public sealed class ChangeLogEntity
{
    public long Id { get; set; }

    public DateTime At { get; set; }

    public Guid? UserId { get; set; }

    public ChangeSource Source { get; set; }

    /// <summary>The table (wire name).</summary>
    public string Entity { get; set; } = "";

    public Guid EntityId { get; set; }

    /// <summary>The root item, for the History tab.</summary>
    public Guid? ItemId { get; set; }

    public Guid? MapId { get; set; }

    public string Action { get; set; } = "";

    /// <summary>jsonb: <c>[{"field","old","new"}]</c>; conflict <c>{"field","yours","theirs","by"}</c>; conflict_resolved <c>{"field","resolution"}</c>.</summary>
    public string Changes { get; set; } = "[]";

    public DateTime? ClientChangedAt { get; set; }

    public string? CorrelationId { get; set; }
}

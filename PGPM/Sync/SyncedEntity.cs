using PGPM.Audit;

namespace PGPM.Sync;

/// <summary>
/// A row synced to the PWA (docs/02 §1.1): every table marked [S] carries these columns. <see cref="SyncStampInterceptor"/>
/// assigns <see cref="Seq"/>, <see cref="Rev"/> and the <c>updated_*</c> columns on every write through EF – sync pushes,
/// admin endpoints, restore, the directory job – so no code path changes a synced row without clients seeing it. A test
/// asserts that every type implementing it is registered in <see cref="SyncTables"/>.
/// </summary>
public interface ISyncedEntity
{
    Guid Id { get; set; }

    /// <summary>The global change sequence number (<c>sync_counter</c>): the pull cursor.</summary>
    long Seq { get; set; }

    /// <summary>Starts at 1, +1 on every accepted change; a re-stamp keeps it.</summary>
    int Rev { get; set; }

    /// <summary>A tombstone.</summary>
    bool Deleted { get; set; }

    /// <summary>The delete batch it was deleted in (the restore unit).</summary>
    Guid? DeleteBatchId { get; set; }

    DateTime CreatedAt { get; set; }

    Guid? CreatedBy { get; set; }

    DateTime UpdatedAt { get; set; }

    Guid? UpdatedBy { get; set; }
}

/// <summary>The synced-row columns once, for entities that have no other base class. Not mapped itself (no TPH).</summary>
public abstract class SyncedEntity : ISyncedEntity
{
    public Guid Id { get; set; }

    [AuditIgnore]
    public long Seq { get; set; }

    [AuditIgnore]
    public int Rev { get; set; }

    [AuditIgnore]
    public bool Deleted { get; set; }

    [AuditIgnore]
    public Guid? DeleteBatchId { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [AuditIgnore]
    public Guid? CreatedBy { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }

    [AuditIgnore]
    public Guid? UpdatedBy { get; set; }

    /// <summary>The wire names of the synced-row columns: never written by a client.</summary>
    public static readonly IReadOnlySet<string> WireColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "seq", "rev", "deleted", "deleteBatchId", "createdAt", "createdBy", "updatedAt", "updatedBy",
    };
}

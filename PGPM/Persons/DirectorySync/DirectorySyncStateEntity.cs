namespace PGPM.Persons.DirectorySync;

/// <summary>A directory run's state (docs/02 §5 <c>directory_sync_state.last_status</c>).</summary>
public static class DirectoryRunStatus
{
    public const string Running = "running";
    public const string Ok = "ok";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Running, Ok, Failed];
}

/// <summary>
/// The directory sync of one tenant (docs/02 §5 <c>directory_sync_state</c>): the delta link the next incremental run
/// continues from, and the last run's result for <i>Administration → Microsoft integration</i>. PGPM adds the mode and
/// counts of the last run, when the last full run was (the weekly one), and when a run last succeeded (docs/03 §15).
/// </summary>
public sealed class DirectorySyncStateEntity
{
    public Guid TenantId { get; set; }

    /// <summary>Graph's <c>@odata.deltaLink</c> of the last complete run; null: the next run is a full one.</summary>
    public string? DeltaLink { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastFinishedAt { get; set; }

    /// <summary>One of <see cref="DirectoryRunStatus"/>.</summary>
    public string? LastStatus { get; set; }

    public string? LastError { get; set; }

    /// <summary><c>full</c> or <c>delta</c>.</summary>
    public string? LastMode { get; set; }

    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    public int DeactivatedCount { get; set; }

    /// <summary>Accounts Graph returned in the last run (changed or not).</summary>
    public int SeenCount { get; set; }

    /// <summary>Persons linked to their user in the last run.</summary>
    public int LinkedCount { get; set; }

    /// <summary>The last full run that completed: the weekly one is due <c>Directory:FullSyncDays</c> after it.</summary>
    public DateTime? LastFullAt { get; set; }

    public DateTime? LastSucceededAt { get; set; }
}

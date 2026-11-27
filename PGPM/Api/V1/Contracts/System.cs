namespace PGPM.Api.V1.Contracts;

/// <summary>The instance: its version, when it started, its environment.</summary>
public sealed record SystemInstanceItem(string Name, string Version, DateTime StartedAt, long UptimeSeconds, string Environment, string? PublicUrl,
    bool BackgroundTasks);

/// <summary>The database: its size on disk and the PostgreSQL version.</summary>
public sealed record SystemDatabaseItem(long SizeBytes, string? ServerVersion);

/// <summary>
/// The attachments' folder: the stored bytes (each stored file once, as the <c>files</c> rows name it, Trash included), the
/// disk's free space and the reserve uploads keep (<c>Files:MinFreeMb</c>), and what is wrong with the folder, if anything.
/// </summary>
public sealed record SystemFilesItem(long StoredBytes, int StoredFiles, int PendingUploads, long? FreeBytes, long MinFreeBytes, string? Problem);

/// <summary>The sync: the head and tombstone horizon (docs/02 §11), the devices known and live, their pending changes and conflicts.</summary>
public sealed record SystemSyncItem(long Head, long TombstoneHorizon, int Clients, int LiveConnections, int LiveUsers, int Pending, int Conflicts,
    bool LiveEnabled, int MaxConnectionsPerUser);

/// <summary>
/// One device (<c>sync_clients</c>): whose, which app, when last seen, how far it is (<see cref="Cursor"/>, <see cref="Behind"/>
/// the head), what it reported pending and in conflict, whether its live channel is open now.
/// </summary>
public sealed record SystemClientItem(Guid Id, Guid UserId, string? UserName, string? AppVersion, string? UserAgent, DateTime LastSeenAt, long Cursor,
    long Behind, int Pending, int Conflicts, bool Live);

/// <summary>
/// A background task's health (<c>key_values</c> <c>task.*</c>, docs/03 §9): its last successful run and summary;
/// <see cref="Stale"/> when it is overdue by more than its interval (or has never run that long after the start).
/// </summary>
public sealed record SystemTaskItem(string Key, string Name, string? Summary, DateTime? LastRunAt, int? IntervalMinutes, bool Stale);

/// <summary>The directory sync at a glance (details: Administration → Microsoft integration).</summary>
public sealed record SystemDirectoryItem(bool Enabled, bool Configured, string? LastStatus, DateTime? LastFinishedAt, string? LastError);

/// <summary>The MCP server at a glance (details: Administration → MCP): tokens switched on, calls of the last 24 hours.</summary>
public sealed record SystemMcpItem(bool Enabled, int TokensOn, int Calls24h, int Errors24h);

/// <summary><c>GET /api/v1/system</c> (<i>System: View</i>): everything Administration → System shows.</summary>
public sealed record SystemStatusItem(SystemInstanceItem Instance, SystemDatabaseItem Database, SystemFilesItem Files, SystemSyncItem Sync,
    IReadOnlyList<SystemClientItem> Clients, IReadOnlyList<SystemTaskItem> Tasks, SystemDirectoryItem Directory, SystemMcpItem Mcp);

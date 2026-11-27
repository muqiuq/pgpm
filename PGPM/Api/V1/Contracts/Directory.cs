namespace PGPM.Api.V1.Contracts;

/// <summary>The Microsoft SSO's settings as Administration → Microsoft integration shows them: read-only, never the secret.</summary>
public sealed record SsoSettingsItem(
    bool Enabled,
    string? TenantId,
    string? ClientId,
    bool ClientSecretSet,
    string? RedirectUri,
    bool AutoProvision,
    Ref? DefaultGroup,
    IReadOnlyList<string> AppRoles);

/// <summary>The directory sync's settings and whether it can run (the app registration complete).</summary>
public sealed record DirectorySettingsItem(bool Enabled, bool Configured, int IntervalHours, int FullSyncDays, bool Running, bool Pending,
    DateTime? NextRunAt, DateTime? NextFullRunAt);

/// <summary>
/// The last run (docs/02 §5 <c>directory_sync_state</c>). <see cref="Status"/>: <c>running</c>, <c>ok</c>, <c>failed</c>;
/// <see cref="Mode"/>: <c>full</c> or <c>delta</c>.
/// </summary>
public sealed record DirectoryRunItem(DateTime? StartedAt, DateTime? FinishedAt, string? Status, string? Mode, string? Error, int Created, int Updated,
    int Deactivated, int Seen, int Linked, DateTime? LastFullAt, DateTime? LastSucceededAt);

/// <summary>The persons of the directory by kind.</summary>
public sealed record DirectoryCountsItem(int Microsoft, int Active, int Inactive, int Guests, int Linked, int Manual);

/// <summary><c>GET /directory/status</c>: everything Administration → Microsoft integration shows.</summary>
public sealed record DirectoryStatusItem(SsoSettingsItem Sso, DirectorySettingsItem Directory, DirectoryRunItem? LastRun, DirectoryCountsItem Persons);

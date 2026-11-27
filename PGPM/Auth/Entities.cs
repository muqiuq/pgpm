using PGPM.Audit;
using PGPM.Auth.Rights;

namespace PGPM.Auth;

/// <summary>
/// A user (docs/02 §3 <c>users</c>: PNMTD's UserEntity with GGITSV's Microsoft link as columns, docs/03 §15 #2). Every
/// user has exactly one person (<see cref="Persons.PersonEntity.UserId"/>).
/// </summary>
[Audited]
public sealed class UserEntity
{
    public Guid Id { get; set; }

    public required string Username { get; set; }

    [AuditIgnore]
    public required string NormalizedUsername { get; set; }

    public required string DisplayName { get; set; }

    public string? Email { get; set; }

    /// <summary><c>pbkdf2-sha256$210000$&lt;salt&gt;$&lt;key&gt;</c>; null: an account that signs in with Microsoft only.</summary>
    [AuditSecret]
    public string? PasswordHash { get; set; }

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    public bool MustChangePassword { get; set; }

    public DateTime? TemporaryPasswordExpiresAt { get; set; }

    /// <summary>Rotated whenever the user's sessions should end: password, 2FA, admin flag, active flag.</summary>
    [AuditIgnore]
    public required string SecurityStamp { get; set; }

    /// <summary>The TOTP secret, encrypted with Data Protection (PGPM.Totp.v1).</summary>
    [AuditSecret]
    public string? TotpSecretProtected { get; set; }

    public bool IsTotpEnabled { get; set; }

    /// <summary>The time step of the last code used: replay protection.</summary>
    [AuditIgnore]
    public long? TotpLastUsedStep { get; set; }

    [AuditSecret]
    public string? PendingTotpSecretProtected { get; set; }

    [AuditIgnore]
    public DateTime? PendingTotpCreatedAt { get; set; }

    [AuditIgnore]
    public int FailedLoginCount { get; set; }

    [AuditIgnore]
    public int LockoutCount { get; set; }

    public DateTime? LockoutEnd { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [AuditIgnore]
    public DateTime? LastLoginAt { get; set; }

    /// <summary>The last UI or sync request, written at most every 5 minutes (<see cref="LastSeen"/>).</summary>
    [AuditIgnore]
    public DateTime? LastSeenAt { get; set; }

    [AuditIgnore]
    public DateTime? PasswordChangedAt { get; set; }

    /// <summary>Entra's <c>tid</c>; with <see cref="MicrosoftObjectId"/> the only key a Microsoft sign-in is matched by.</summary>
    public Guid? MicrosoftTenantId { get; set; }

    /// <summary>Entra's <c>oid</c>.</summary>
    public Guid? MicrosoftObjectId { get; set; }

    /// <summary>The UPN from Graph's <c>/me</c> at the last Microsoft sign-in.</summary>
    [AuditIgnore]
    public string? MicrosoftUserName { get; set; }

    [AuditIgnore]
    public DateTime? DirectoryReadAt { get; set; }

    /// <summary>Incremented on structural access changes: the user's clients reset their local data (docs/03 §11.5, phase 2).</summary>
    [AuditIgnore]
    public int AccessEpoch { get; set; } = 1;

    public List<UserGroupEntity> Groups { get; set; } = [];

    public bool HasMicrosoftAccount => MicrosoftObjectId is not null;
}

/// <summary>
/// A group (docs/02 §3 <c>groups</c>): rights per area, members, and an optional Entra app role that manages its
/// membership at every Microsoft sign-in. A synced row, read-only for clients (names for the share picker), delivered
/// without <see cref="NormalizedName"/> and <see cref="EntraAppRole"/>. Deleting one leaves a tombstone (a query filter hides
/// it from everything but the sync).
/// </summary>
[Audited]
public sealed class GroupEntity : Sync.SyncedEntity
{
    /// <summary>The seeded groups (docs/03 §6.2), fixed ids (UUID v7 layout).</summary>
    public static readonly Guid MembersId = new("01970000-0000-7000-8000-000000000001");

    public static readonly Guid ViewersId = new("01970000-0000-7000-8000-000000000002");

    public required string Name { get; set; }

    [AuditIgnore]
    public required string NormalizedName { get; set; }

    public string? Description { get; set; }

    /// <summary>Upper-cased; unique where set. Membership follows the token's <c>roles</c> claim (docs/03 §5.3 step 4).</summary>
    public string? EntraAppRole { get; set; }

    public List<GroupRightEntity> Rights { get; set; } = [];

    public List<UserGroupEntity> Members { get; set; } = [];

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public static string? NormalizeAppRole(string? role) => string.IsNullOrWhiteSpace(role) ? null : role.Trim().ToUpperInvariant();
}

[Audited]
public sealed class UserGroupEntity
{
    public Guid UserId { get; set; }

    public Guid GroupId { get; set; }

    public UserEntity User { get; set; } = null!;

    public GroupEntity Group { get; set; } = null!;
}

[Audited]
public sealed class GroupRightEntity
{
    public Guid GroupId { get; set; }

    public Area Area { get; set; }

    public Level Level { get; set; }

    public GroupEntity Group { get; set; } = null!;
}

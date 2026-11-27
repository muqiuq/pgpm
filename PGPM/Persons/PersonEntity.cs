using PGPM.Audit;

namespace PGPM.Persons;

/// <summary>Where a person comes from (docs/02 §5).</summary>
public enum PersonSource { Microsoft, Manual }

/// <summary>
/// A person of the directory (docs/02 §5 <c>persons</c>), minimal in phase 1: every user has one – a manual person
/// created with a local user, or the Microsoft person a Microsoft sign-in links (or creates from the claims). A synced row
/// every user receives; manual persons are patchable with Persons: Edit, a Microsoft person's directory fields never
/// (phase 6 fills them from Microsoft Graph).
/// </summary>
[Audited]
public sealed class PersonEntity : Sync.SyncedEntity
{
    public PersonSource Source { get; set; }

    public Guid? EntraTenantId { get; set; }

    public Guid? EntraObjectId { get; set; }

    public required string DisplayName { get; set; }

    public string? GivenName { get; set; }

    public string? Surname { get; set; }

    /// <summary>Lower-cased.</summary>
    public string? Email { get; set; }

    public string? UserPrincipalName { get; set; }

    /// <summary><c>Member</c> or <c>Guest</c>.</summary>
    public string? UserType { get; set; }

    public string? JobTitle { get; set; }

    public string? Department { get; set; }

    public string? CompanyName { get; set; }

    public string? Phone { get; set; }

    public string? MobilePhone { get; set; }

    public bool IsActive { get; set; } = true;

    [AuditIgnore]
    public DateTime? DirectorySyncedAt { get; set; }

    [AuditText]
    public string? Notes { get; set; }

    /// <summary>The user this person is; unique where set.</summary>
    public Guid? UserId { get; set; }
}

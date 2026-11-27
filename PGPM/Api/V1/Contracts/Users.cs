namespace PGPM.Api.V1.Contracts;

/// <summary>A user as admins see it. <see cref="LockedUntil"/> is set while a lockout lasts.</summary>
public sealed record UserItem(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    bool IsAdmin,
    IReadOnlyList<UserGroupRef> Groups,
    bool IsActive,
    bool IsTotpEnabled,
    bool HasPassword,
    bool MustChangePassword,
    DateTime? TemporaryPasswordExpiresAt,
    DateTime? LockedUntil,
    int FailedLoginCount,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    DateTime? LastSeenAt,
    DateTime? PasswordChangedAt,
    MicrosoftAccount? MicrosoftAccount,
    Guid? PersonId);

/// <summary>A user's group; <see cref="Entra"/>: the membership follows an Entra app role.</summary>
public sealed record UserGroupRef(Guid Id, string Name, bool Entra);

/// <summary>The Microsoft account a user signs in with (docs/03 §5.3): matched by tenant and object id only.</summary>
public sealed record MicrosoftAccount(Guid TenantId, Guid ObjectId, string? UserName, DateTime? DirectoryReadAt);

/// <summary><c>POST /users</c>: a local user with a temporary password, which the response shows once.</summary>
public sealed class CreateUserRequest
{
    public string? Username { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool IsAdmin { get; init; }

    public List<Guid>? GroupIds { get; init; }
}

/// <summary><c>PUT /users/{id}</c>. The username stays.</summary>
public sealed class UpdateUserRequest
{
    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    /// <summary>Unchanged when missing.</summary>
    public bool? IsAdmin { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>Unchanged when missing. Memberships Entra manages stay as they are and may not be changed.</summary>
    public List<Guid>? GroupIds { get; init; }
}

/// <summary><c>PUT /users/{id}/microsoft-account</c>: the account's object id (<c>oid</c>); the tenant defaults to Entra:TenantId.</summary>
public sealed class MicrosoftAccountRequest
{
    public Guid ObjectId { get; init; }

    public Guid? TenantId { get; init; }
}

/// <summary>A temporary password: shown once, valid until <see cref="ExpiresAt"/>, changed at the next sign-in.</summary>
public sealed record TemporaryPassword(string Password, DateTime ExpiresAt);

public sealed record CreatedUser(UserItem User, TemporaryPassword TemporaryPassword);

/// <summary><c>GET /users</c>: sort username (default), name, admin, lastLogin, created; search username, name, e-mail; a group's members.</summary>
public sealed class UserQuery : PageQuery
{
    public Guid? GroupId { get; set; }
}

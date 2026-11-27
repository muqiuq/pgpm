using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Persons;
using PGPM.Lib.Helper;
using PGPM.Lib.Security;

namespace PGPM.Auth;

/// <summary>
/// Users and their passwords, for the API and for `pgpm users …` alike (PNMTD, docs/03 §4). Every change that should end
/// other sessions rotates the security stamp; every change is in the audit log; every user has a person.
/// </summary>
public sealed partial class UserAccounts(PgpmDbContext db, SessionCache? sessions = null)
{
    /// <summary>For the CLI, which looks groups up by name.</summary>
    internal PgpmDbContext Db => db;

    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromDays(7);

    [GeneratedRegex("^[A-Za-z0-9._@-]{2,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static bool IsValidUsername(string? name) => name is not null && UsernamePattern().IsMatch(name);

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    public static string NewStamp() => Guid.NewGuid().ToString("N");

    public Task<UserEntity?> FindAsync(string username, CancellationToken ct = default)
    {
        var normalized = Normalize(username);
        return db.Users.SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
    }

    public async Task<UserEntity> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound("User");

    /// <summary>
    /// A new local user, with a manual person. Without <paramref name="password"/> one is generated: a temporary password
    /// that is shown once, expires after 7 days and must be changed at the first sign-in.
    /// </summary>
    public async Task<(UserEntity User, string? TemporaryPassword)> CreateAsync(string? username, string? displayName, string? email, bool isAdmin,
        IReadOnlyCollection<Guid>? groupIds, string? password, AuditWho who, CancellationToken ct = default)
    {
        var name = username?.Trim() ?? "";
        var v = new ApiValidator()
            .Check(IsValidUsername(name), "username", "2 to 64 letters, digits or . _ @ -.")
            .Check(displayName is null || displayName.Trim().Length <= 128, "displayName", "At most 128 characters.");
        CheckEmail(v, email);
        if (password is not null && PasswordHasher.PolicyViolation(password) is { } violation)
        {
            v.Add("password", violation);
        }
        v.ThrowIfInvalid();
        if (await FindAsync(name, ct) is not null)
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"The username {name} is taken.");
        }
        var groups = await GroupsAsync(groupIds, ct);
        if (groups.FirstOrDefault(g => g.EntraAppRole is not null) is { } managed)
        {
            throw ManagedByEntra(managed);
        }

        var now = Clock.UtcNow;
        var temporary = password is null ? SecurePasswordGenerator.Generate() : null;
        var user = new UserEntity
        {
            Id = Ids.New(),
            Username = name,
            NormalizedUsername = Normalize(name),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
            Email = CleanEmail(email),
            PasswordHash = PasswordHasher.Hash(password ?? temporary!),
            IsAdmin = isAdmin,
            IsActive = true,
            MustChangePassword = temporary is not null,
            TemporaryPasswordExpiresAt = temporary is not null ? now + TemporaryPasswordLifetime : null,
            SecurityStamp = NewStamp(),
            CreatedAt = now,
            PasswordChangedAt = now,
        };
        user.Groups.AddRange(groups.Select(g => new UserGroupEntity { UserId = user.Id, GroupId = g.Id }));
        db.Users.Add(user);
        UserPersons.CreateManual(db, user);
        Log(AuditEvents.UserCreated, user, who, $"{user.Username}: {Access(isAdmin, groups.Select(g => g.Name))}");
        await SaveAsync(ct);
        return (user, temporary);
    }

    /// <summary>
    /// Display name, e-mail, admin flag, active state and groups. A change of the flag or of the active state ends the
    /// user's sessions; groups apply from the next request, without a sign-out. <paramref name="isAdmin"/> and
    /// <paramref name="groupIds"/> null: unchanged. Memberships of groups Entra manages can't be changed here.
    /// </summary>
    public async Task<UserEntity> UpdateAsync(Guid id, string? displayName, string? email, bool? isAdmin, bool isActive,
        IReadOnlyCollection<Guid>? groupIds, AuditWho who, CancellationToken ct = default)
    {
        var user = await db.Users.Include(u => u.Groups).ThenInclude(m => m.Group).SingleOrDefaultAsync(u => u.Id == id, ct)
            ?? throw ApiException.NotFound("User");
        var v = new ApiValidator()
            .Check(!string.IsNullOrWhiteSpace(displayName) && displayName.Trim().Length <= 128, "displayName", "1 to 128 characters.");
        CheckEmail(v, email);
        v.ThrowIfInvalid();
        var groups = groupIds is null ? null : await GroupsAsync(groupIds, ct);
        await GuardLastAdminAsync(user, staysAdmin: (isAdmin ?? user.IsAdmin) && isActive, ct);
        user.DisplayName = displayName!.Trim();
        user.Email = CleanEmail(email);
        if (isAdmin is { } admin && user.IsAdmin != admin)
        {
            Log(AuditEvents.UserAdminChanged, user, who, admin ? $"{user.Username} is an admin now" : $"{user.Username} is no longer an admin");
            user.IsAdmin = admin;
            user.SecurityStamp = NewStamp();
        }
        if (user.IsActive != isActive)
        {
            Log(AuditEvents.UserUpdated, user, who, isActive ? $"{user.Username} activated" : $"{user.Username} deactivated");
            user.IsActive = isActive;
            user.SecurityStamp = NewStamp();
        }
        if (groups is not null)
        {
            var removed = user.Groups.Where(m => groups.All(g => g.Id != m.GroupId)).Select(m => m.Group).ToList();
            var added = groups.Where(g => user.Groups.All(m => m.GroupId != g.Id)).ToList();
            if (removed.Concat(added).FirstOrDefault(g => g.EntraAppRole is not null) is { } managed)
            {
                throw ManagedByEntra(managed);
            }
            if (removed.Count + added.Count > 0)
            {
                var before = user.Groups.Select(m => m.Group.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
                var after = groups.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
                Log(AuditEvents.UserGroupsChanged, user, who, $"{user.Username}: {Names(before)} → {Names(after)}");
                user.Groups.RemoveAll(m => removed.Any(g => g.Id == m.GroupId));
                user.Groups.AddRange(added.Select(g => new UserGroupEntity { UserId = user.Id, GroupId = g.Id }));
            }
        }
        await UserPersons.FollowUserAsync(db, user, ct);
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return user;
    }

    /// <summary>"admin", "groups Members, Viewers", "no groups": what a user may do, for the audit and the CLI.</summary>
    public static string Access(bool isAdmin, IEnumerable<string> groups)
    {
        var names = groups.ToList();
        return isAdmin
            ? "admin" + (names.Count > 0 ? $", groups {string.Join(", ", names)}" : "")
            : names.Count > 0 ? $"groups {string.Join(", ", names)}" : "no groups";
    }

    private static string Names(IReadOnlyCollection<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    internal static ApiException ManagedByEntra(GroupEntity group) =>
        ApiException.Conflict("membership_managed_by_entra",
            $"The members of {group.Name} follow the Entra app role {group.EntraAppRole} at every Microsoft sign-in.");

    /// <summary>The groups of a request: each must exist (400 groupIds otherwise).</summary>
    private async Task<List<GroupEntity>> GroupsAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }
        var distinct = ids.Distinct().ToList();
        var groups = await db.Groups.Where(g => distinct.Contains(g.Id)).ToListAsync(ct);
        new ApiValidator().Check(groups.Count == distinct.Count, "groupIds", "Unknown group.").ThrowIfInvalid();
        return groups;
    }

    /// <summary>Deletes a user. Their person stays (without a user): it may be assigned to items.</summary>
    public async Task DeleteAsync(Guid id, AuditWho who, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        await GuardLastAdminAsync(user, staysAdmin: false, ct);
        db.Users.Remove(user);
        Log(AuditEvents.UserDeleted, user, who, $"{user.Username} deleted");
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
    }

    /// <summary>A new password, by default a temporary one (shown once, 7 days, must be changed). Ends every session and lifts a lockout.</summary>
    public async Task<string?> ResetPasswordAsync(Guid id, string? password, AuditWho who, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        if (password is not null && PasswordHasher.PolicyViolation(password) is { } violation)
        {
            throw ApiException.Invalid("password", violation);
        }
        var temporary = password is null ? SecurePasswordGenerator.Generate() : null;
        var now = Clock.UtcNow;
        user.PasswordHash = PasswordHasher.Hash(password ?? temporary!);
        user.MustChangePassword = temporary is not null;
        user.TemporaryPasswordExpiresAt = temporary is not null ? now + TemporaryPasswordLifetime : null;
        user.PasswordChangedAt = now;
        user.SecurityStamp = NewStamp();
        Unlock(user);
        Log(AuditEvents.PasswordReset, user, who, temporary is not null ? $"{user.Username}: a temporary password" : $"{user.Username}: password set");
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return temporary;
    }

    /// <summary>
    /// "Reset 2FA", for a user who lost the authenticator: 2FA off, every session ended, a lockout lifted. The next sign-in
    /// offers the setup again. Returns whether 2FA was on.
    /// </summary>
    public async Task<bool> ResetTotpAsync(Guid id, AuditWho who, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        var wasEnabled = user.IsTotpEnabled;
        ClearTotp(user);
        user.SecurityStamp = NewStamp();
        Unlock(user);
        Log(AuditEvents.TotpReset, user, who, wasEnabled ? $"{user.Username}: 2FA off" : $"{user.Username}: 2FA was off");
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return wasEnabled;
    }

    public async Task UnlockAsync(Guid id, AuditWho who, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        Unlock(user);
        Log(AuditEvents.UserUnlocked, user, who, $"{user.Username} unlocked");
        await SaveAsync(ct);
    }

    /// <summary>The user's own change: needs the current password; ends the user's other sessions. 409 for an account without one.</summary>
    public async Task ChangePasswordAsync(Guid id, string? currentPassword, string? newPassword, AuditWho who, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        if (user.PasswordHash is null)
        {
            throw ApiException.Conflict("password_not_set", "This account signs in with Microsoft and has no password.");
        }
        var v = new ApiValidator();
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            v.Add("currentPassword", "Not your current password.");
        }
        if (PasswordHasher.PolicyViolation(newPassword) is { } violation)
        {
            v.Add("newPassword", violation);
        }
        else if (newPassword == currentPassword)
        {
            v.Add("newPassword", "Choose a password other than the current one.");
        }
        v.ThrowIfInvalid();
        user.PasswordHash = PasswordHasher.Hash(newPassword!);
        user.MustChangePassword = false;
        user.TemporaryPasswordExpiresAt = null;
        user.PasswordChangedAt = Clock.UtcNow;
        user.SecurityStamp = NewStamp();
        Log(AuditEvents.PasswordChanged, user, who, null);
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
    }

    /// <summary>"Sign out everywhere": every session but the one that asks (which gets a fresh cookie) ends.</summary>
    public async Task<UserEntity> RevokeSessionsAsync(Guid id, AuditWho who, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        user.SecurityStamp = NewStamp();
        Log(AuditEvents.SessionsRevoked, user, who, null);
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return user;
    }

    /// <summary>
    /// An admin links a Microsoft account (docs/03 §5.3 step 2): the user signs in with it from then on. The tenant defaults
    /// to Entra:TenantId. 409 duplicate_key when another user has it. A directory person of the account becomes the user's.
    /// </summary>
    public async Task<UserEntity> LinkMicrosoftAsync(Guid id, Guid objectId, Guid? tenantId, Guid? defaultTenant, AuditWho who,
        CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        var tenant = tenantId ?? defaultTenant;
        new ApiValidator()
            .Check(objectId != Guid.Empty, "objectId", "The account's object id (oid).")
            .Check(tenant is { } t && t != Guid.Empty, "tenantId", "The tenant (Entra:TenantId is not set).")
            .ThrowIfInvalid();
        if (await db.Users.AnyAsync(u => u.Id != id && u.MicrosoftTenantId == tenant && u.MicrosoftObjectId == objectId, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, "Another user is linked to this Microsoft account.");
        }
        user.MicrosoftTenantId = tenant;
        user.MicrosoftObjectId = objectId;
        Log(AuditEvents.MicrosoftLinked, user, who, $"{user.Username}: Microsoft account {tenant}/{objectId}");
        await SaveAsync(ct);
        if (await db.Persons.AnyAsync(p => p.EntraTenantId == tenant && p.EntraObjectId == objectId, ct))
        {
            await UserPersons.LinkMicrosoftAsync(db, user, tenant!.Value, objectId, null, null, null, who, ct);
            await SaveAsync(ct);
        }
        return user;
    }

    /// <summary>The Microsoft link goes; the user keeps their person (and needs a password to sign in locally).</summary>
    public async Task<UserEntity> UnlinkMicrosoftAsync(Guid id, AuditWho who, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        if (user.MicrosoftObjectId is null)
        {
            return user;
        }
        Log(AuditEvents.MicrosoftUnlinked, user, who, $"{user.Username}: Microsoft account {user.MicrosoftTenantId}/{user.MicrosoftObjectId} unlinked");
        user.MicrosoftTenantId = null;
        user.MicrosoftObjectId = null;
        user.MicrosoftUserName = null;
        user.SecurityStamp = NewStamp();
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return user;
    }

    /// <summary>
    /// A new installation: when the users table is empty after the migrations, the first admin (Auth:InitialAdmin:Username,
    /// default admin) with Auth:InitialAdmin:Password. Without it a random password is generated and logged once. Either
    /// way it must be changed at the first sign-in. Returns the generated password, if one was generated.
    /// </summary>
    public async Task<(UserEntity? User, string? GeneratedPassword)> SeedAsync(IConfiguration configuration, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return (null, null);
        }
        var username = configuration["Auth:InitialAdmin:Username"];
        var password = configuration["Auth:InitialAdmin:Password"];
        var generated = string.IsNullOrEmpty(password) ? SecurePasswordGenerator.Generate() : null;
        var (user, _) = await CreateAsync(string.IsNullOrWhiteSpace(username) ? "admin" : username, null, null, isAdmin: true, groupIds: null,
            password ?? generated, AuditWho.Seed, ct);
        // Known to whoever reads .env or the log: changed at the first sign-in, but it does not expire.
        user.MustChangePassword = true;
        await SaveAsync(ct);
        return (user, generated);
    }

    /// <summary>
    /// A change that would leave no active admin (deleting, demoting or deactivating the last one) is refused with 409
    /// last_admin: nobody could manage users any more. <paramref name="staysAdmin"/>: the user is an active admin after
    /// the change, too.
    /// </summary>
    internal async Task GuardLastAdminAsync(UserEntity user, bool staysAdmin, CancellationToken ct)
    {
        if (!user.IsAdmin || !user.IsActive || staysAdmin)
        {
            return;
        }
        if (!await db.Users.AnyAsync(u => u.Id != user.Id && u.IsAdmin && u.IsActive, ct))
        {
            throw ApiException.Conflict("last_admin", "This is the last active admin.");
        }
    }

    internal static void ClearTotp(UserEntity user)
    {
        user.TotpSecretProtected = null;
        user.IsTotpEnabled = false;
        user.TotpLastUsedStep = null;
        user.PendingTotpSecretProtected = null;
        user.PendingTotpCreatedAt = null;
    }

    internal static void Unlock(UserEntity user)
    {
        user.FailedLoginCount = 0;
        user.LockoutCount = 0;
        user.LockoutEnd = null;
    }

    private static void CheckEmail(ApiValidator v, string? email) =>
        v.Check(string.IsNullOrWhiteSpace(email) || (email.Trim().Length <= 254 && email.Contains('@', StringComparison.Ordinal)),
            "email", "An e-mail address (at most 254 characters).");

    private static string? CleanEmail(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    /// <summary>
    /// An account's event. The actor is who did it (an admin, the CLI, a seed, Entra); during the anonymous steps of the
    /// sign-in, the person signing in. The account is the subject, so the user filter finds it.
    /// </summary>
    internal void Log(string @event, UserEntity? user, AuditWho who, string? summary, string? typedUsername = null)
    {
        var actor = who.Actor != AuditActor.User || who.UserId is not null ? who : who.As(user?.Id, user?.Username ?? typedUsername ?? "");
        AuditLog.Record(db, @event, summary, actor, user is null ? null : AuditTarget.User(user.Id, user.Username));
    }

    internal Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

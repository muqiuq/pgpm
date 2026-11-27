using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Persons;
using PGPM.Lib.Helper;

namespace PGPM.Auth.Microsoft;

/// <summary>Why a Microsoft sign-in was refused: the <c>ssoError</c> of the sign-in page.</summary>
public enum MicrosoftSignInFailure { Deactivated, AccountConflict, NoAccount }

public sealed record MicrosoftSignInResult(UserEntity? User, MicrosoftSignInFailure? Failure, bool Provisioned)
{
    public static string ErrorCode(MicrosoftSignInFailure failure) => failure switch
    {
        MicrosoftSignInFailure.Deactivated => "deactivated",
        MicrosoftSignInFailure.AccountConflict => "account-conflict",
        _ => "no-account",
    };
}

/// <summary>
/// Completes a Microsoft sign-in (GGITSV's MicrosoftSignInService on PNMTD's users, docs/03 §5.3), in one transaction:
/// <list type="number">
/// <item>the account is found by tenant and object id only – never by e-mail or name;</item>
/// <item>an unknown one is created (Entra:AutoProvision), unless its username belongs to another user: account-conflict,
///   an admin links it;</item>
/// <item>a deactivated user is refused;</item>
/// <item>the memberships of every group with an Entra app role follow the token's roles; other groups and the admin
///   flag are never touched;</item>
/// <item>a user without any group then goes into Entra:DefaultGroupId;</item>
/// <item>the user's person is the directory person of the account (or one made from the claims);</item>
/// <item>Graph's /me names (when it answered) and the sign-in time are written.</item>
/// </list>
/// The caller issues the session (<see cref="MicrosoftSignIn"/>).
/// </summary>
public sealed class MicrosoftSignInService(PgpmDbContext db, SessionCache sessions, IOptions<EntraOptions> entra, ILogger<MicrosoftSignInService> logger)
{
    public async Task<MicrosoftSignInResult> CompleteAsync(ExternalIdentity identity, AuditWho request, CancellationToken ct)
    {
        var accounts = new UserAccounts(db, sessions);
        var key = $"{identity.TenantId}/{identity.ObjectId}";
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await db.Users.Include(u => u.Groups).SingleOrDefaultAsync(u => u.MicrosoftTenantId == identity.TenantId
            && u.MicrosoftObjectId == identity.ObjectId, ct);
        var provisioned = false;
        if (user is null)
        {
            var username = UserNameFor(identity);
            if (await accounts.FindAsync(username, ct) is { } namesake)
            {
                accounts.Log(AuditEvents.LoginRefused, namesake, request.As(null, username),
                    $"Microsoft sign-in: the account {key} is not linked, and its username {username} belongs to another user; an admin links it.");
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new MicrosoftSignInResult(null, MicrosoftSignInFailure.AccountConflict, false);
            }
            if (!entra.Value.AutoProvision)
            {
                AuditLog.Record(db, AuditEvents.LoginRefused, $"Microsoft sign-in: the account {key} ({username}) has no user, and Entra:AutoProvision is off.",
                    request.As(null, username));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new MicrosoftSignInResult(null, MicrosoftSignInFailure.NoAccount, false);
            }
            user = Provision(identity, username, request);
            provisioned = true;
            await db.SaveChangesAsync(ct);
        }
        else if (!user.IsActive)
        {
            accounts.Log(AuditEvents.LoginRefused, user, request.As(user.Id, user.Username), "Microsoft sign-in: the account is deactivated.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new MicrosoftSignInResult(null, MicrosoftSignInFailure.Deactivated, false);
        }

        var entraWho = AuditWho.Entra(user.Id, user.Username, request.IpAddress);
        await SynchronizeRoleGroupsAsync(user, identity.Roles, entraWho, ct);
        await DefaultGroupAsync(user, entraWho, ct);
        await UserPersons.LinkMicrosoftAsync(db, user, identity.TenantId, identity.ObjectId, identity.DisplayName,
            identity.Email ?? identity.Directory?.Mail, identity.Directory?.UserPrincipalName ?? identity.PreferredUserName, entraWho, ct);

        var now = Clock.UtcNow;
        user.LastLoginAt = now;
        if (identity.Directory is { } directory)
        {
            user.MicrosoftUserName = Clean(directory.UserPrincipalName);
            user.DirectoryReadAt = now;
        }
        accounts.Log(AuditEvents.LoginSucceeded, user, request.As(user.Id, user.Username), provisioned ? "Microsoft, first sign-in" : "Microsoft");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        sessions.Forget(user.Id);
        return new MicrosoftSignInResult(user, null, provisioned);
    }

    /// <summary>
    /// <c>preferred_username</c> first (usually the UPN), then the e-mail address, and the object id when neither fits the
    /// username rules (a guest's <c>…#EXT#@…</c>, or longer than 64 characters).
    /// </summary>
    internal static string UserNameFor(ExternalIdentity identity) =>
        new[] { identity.PreferredUserName?.Trim(), identity.Email?.Trim(), identity.ObjectId.ToString("D") }.First(UserAccounts.IsValidUsername)!;

    private UserEntity Provision(ExternalIdentity identity, string username, AuditWho request)
    {
        var email = identity.Email ?? identity.Directory?.Mail ?? (username.Contains('@', StringComparison.Ordinal) ? username : null);
        var user = new UserEntity
        {
            Id = Ids.New(),
            Username = username,
            NormalizedUsername = UserAccounts.Normalize(username),
            DisplayName = string.IsNullOrWhiteSpace(identity.DisplayName) ? username : identity.DisplayName.Trim(),
            Email = Clean(email),
            PasswordHash = null,
            IsActive = true,
            SecurityStamp = UserAccounts.NewStamp(),
            CreatedAt = Clock.UtcNow,
            MicrosoftTenantId = identity.TenantId,
            MicrosoftObjectId = identity.ObjectId,
        };
        db.Users.Add(user);
        AuditLog.Record(db, AuditEvents.UserProvisioned, $"{username}: created at the first Microsoft sign-in of the account {identity.TenantId}/{identity.ObjectId}",
            AuditWho.Entra(user.Id, username, request.IpAddress), AuditTarget.User(user.Id, username));
        return user;
    }

    /// <summary>Every group with an Entra app role: a member exactly when the token carries the role (GGITSV).</summary>
    private async Task SynchronizeRoleGroupsAsync(UserEntity user, IReadOnlyList<string> roles, AuditWho who, CancellationToken ct)
    {
        var held = roles.Select(GroupEntity.NormalizeAppRole).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var roleGroups = await db.Groups.Where(g => g.EntraAppRole != null).ToListAsync(ct);
        foreach (var group in roleGroups)
        {
            var member = user.Groups.FirstOrDefault(m => m.GroupId == group.Id);
            var shouldBe = held.Contains(group.EntraAppRole!);
            if (shouldBe && member is null)
            {
                user.Groups.Add(new UserGroupEntity { UserId = user.Id, GroupId = group.Id });
                user.AccessEpoch++;
                AuditLog.Record(db, AuditEvents.MembershipAdded, $"{user.Username} → {group.Name}: the Microsoft sign-in carries the app role {group.EntraAppRole}",
                    who, AuditTarget.Group(group.Id, group.Name));
            }
            else if (!shouldBe && member is not null)
            {
                user.Groups.Remove(member);
                db.UserGroups.Remove(member);
                user.AccessEpoch++;
                AuditLog.Record(db, AuditEvents.MembershipRemoved,
                    $"{user.Username} ← {group.Name}: the Microsoft sign-in no longer carries the app role {group.EntraAppRole}",
                    who, AuditTarget.Group(group.Id, group.Name));
            }
        }
    }

    /// <summary>Entra:DefaultGroupId for a user who has no group after the app roles (docs/03 §5.3 step 5).</summary>
    private async Task DefaultGroupAsync(UserEntity user, AuditWho who, CancellationToken ct)
    {
        if (entra.Value.DefaultGroupId is not { } id || user.Groups.Count > 0)
        {
            return;
        }
        if (await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct) is not { } group)
        {
            logger.LogWarning("Entra:DefaultGroupId {Group} names no group: {User} stays without one.", id, user.Username);
            return;
        }
        user.Groups.Add(new UserGroupEntity { UserId = user.Id, GroupId = group.Id });
        user.AccessEpoch++;
        AuditLog.Record(db, AuditEvents.MembershipAdded, $"{user.Username} → {group.Name}: no group after the app roles, the default group (Entra:DefaultGroupId)",
            who, AuditTarget.Group(group.Id, group.Name));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

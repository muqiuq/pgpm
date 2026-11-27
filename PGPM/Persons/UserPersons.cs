using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Data;

namespace PGPM.Persons;

/// <summary>
/// Every user has exactly one person (docs/02 §3, docs/03 §6.3): a manual person created with a local user, kept in step
/// with the user's name and e-mail; for a Microsoft user the directory person with the same (tenant, object id), created
/// from the sign-in's claims when the directory sync (phase 6) hasn't brought it yet.
/// </summary>
public static class UserPersons
{
    /// <summary>A manual person for a new local user (added to <paramref name="db"/>, saved with the user).</summary>
    public static PersonEntity CreateManual(PgpmDbContext db, UserEntity user)
    {
        var person = new PersonEntity
        {
            Id = Lib.Helper.Ids.New(),
            Source = PersonSource.Manual,
            DisplayName = user.DisplayName,
            Email = Email(user.Email),
            IsActive = true,
            UserId = user.Id,
        };
        db.Persons.Add(person);
        return person;
    }

    /// <summary>A manual person follows its user's display name and e-mail; a Microsoft person is the directory's.</summary>
    public static async Task FollowUserAsync(PgpmDbContext db, UserEntity user, CancellationToken ct)
    {
        var person = await db.Persons.SingleOrDefaultAsync(p => p.UserId == user.Id, ct);
        if (person is { Source: PersonSource.Manual })
        {
            person.DisplayName = user.DisplayName;
            person.Email = Email(user.Email);
        }
        else if (person is null)
        {
            CreateManual(db, user);
        }
    }

    /// <summary>
    /// A Microsoft sign-in's person (docs/03 §5.3 step 6): the person with the account's (tenant, object id) becomes the
    /// user's – the directory's, or one made from the claims. A manual person the user had is converted into it when no
    /// directory person exists (it keeps what it is assigned to), and let go otherwise (it stays, without a user).
    /// </summary>
    public static async Task<PersonEntity> LinkMicrosoftAsync(PgpmDbContext db, UserEntity user, Guid tenantId, Guid objectId, string? displayName,
        string? email, string? userPrincipalName, AuditWho who, CancellationToken ct)
    {
        var directory = await db.Persons.SingleOrDefaultAsync(p => p.EntraTenantId == tenantId && p.EntraObjectId == objectId, ct);
        var own = await db.Persons.SingleOrDefaultAsync(p => p.UserId == user.Id, ct);
        if (directory is not null)
        {
            if (directory.UserId == user.Id)
            {
                return directory;
            }
            if (own is not null)
            {
                own.UserId = null;
            }
            if (directory.UserId is { } other && other != user.Id)
            {
                // Another user had it (a link moved): the person is this account's now.
                directory.UserId = null;
                await db.SaveChangesAsync(ct);
            }
            if (own is not null)
            {
                await db.SaveChangesAsync(ct);
            }
            directory.UserId = user.Id;
            AuditLog.Record(db, AuditEvents.PersonLinked, $"{directory.DisplayName} is {user.Username}'s person (Microsoft account {tenantId}/{objectId})",
                who, AuditTarget.Person(directory.Id, directory.DisplayName));
            return directory;
        }
        var name = string.IsNullOrWhiteSpace(displayName) ? user.DisplayName : displayName.Trim();
        if (own is not null && own.Source == PersonSource.Manual)
        {
            own.Source = PersonSource.Microsoft;
            own.EntraTenantId = tenantId;
            own.EntraObjectId = objectId;
            own.DisplayName = name;
            own.Email = Email(email) ?? own.Email;
            own.UserPrincipalName = Clean(userPrincipalName);
            AuditLog.Record(db, AuditEvents.PersonLinked, $"{own.DisplayName}: now the person of the Microsoft account {tenantId}/{objectId}",
                who, AuditTarget.Person(own.Id, own.DisplayName));
            return own;
        }
        if (own is not null)
        {
            // A Microsoft person of another account (the link moved): it stays the old account's.
            own.UserId = null;
            await db.SaveChangesAsync(ct);
        }
        var person = new PersonEntity
        {
            Id = Lib.Helper.Ids.New(),
            Source = PersonSource.Microsoft,
            EntraTenantId = tenantId,
            EntraObjectId = objectId,
            DisplayName = name,
            Email = Email(email),
            UserPrincipalName = Clean(userPrincipalName),
            IsActive = true,
            UserId = user.Id,
        };
        db.Persons.Add(person);
        AuditLog.Record(db, AuditEvents.PersonCreated, $"{person.DisplayName}, from the Microsoft sign-in of {user.Username}", who,
            AuditTarget.Person(person.Id, person.DisplayName));
        return person;
    }

    public static string? Email(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

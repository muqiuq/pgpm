using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Auth.Rights;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Persons;

/// <summary>
/// <c>persons</c> (docs/02 §5): every user receives every person. Manual persons are created, patched and deleted with
/// <i>Persons: Edit</i>; a Microsoft person's directory fields are the directory job's (only <c>notes</c> is patchable), and
/// the name and e-mail of a user's own manual person follow the user. A user's person is never deleted.
/// </summary>
public sealed class PersonsTable() : SyncTable<PersonEntity>("persons")
{
    public override IReadOnlySet<string> ServerOwned { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "source", "entraTenantId", "entraObjectId", "directorySyncedAt", "userId",
    };

    public override IReadOnlySet<string> Patchable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "displayName", "givenName", "surname", "email", "userPrincipalName", "userType", "jobTitle", "department", "companyName", "phone",
        "mobilePhone", "isActive", "notes",
    };

    protected override PersonEntity Create() => new() { DisplayName = "" };

    public override IQueryable<PersonEntity> Visible(IQueryable<PersonEntity> rows, Viewer viewer, AccessService access) => rows;

    private static void RequireRight(SyncWrite w)
    {
        if (!w.Viewer.Rights.Has(Area.Persons, Level.Edit))
        {
            throw new SyncRejection(SyncCodes.MissingRight, $"You need {RightsCatalog.Of(Area.Persons).Label}: Edit.");
        }
    }

    protected override Task AuthorizeInsertAsync(SyncWrite w, PersonEntity row, SyncMutationIn m, CancellationToken ct)
    {
        RequireRight(w);
        return Task.CompletedTask;
    }

    protected override Task PrepareInsertAsync(SyncWrite w, PersonEntity row, CancellationToken ct)
    {
        row.Source = PersonSource.Manual;
        row.Email = UserPersons.Email(row.Email);
        return Task.CompletedTask;
    }

    protected override bool IsPatchable(PersonEntity row, string key, Data.PgpmDbContext db) =>
        base.IsPatchable(row, key, db)
        && (row.Source == PersonSource.Microsoft ? key == "notes" : row.UserId is null || key is not ("displayName" or "email"));

    protected override Task AuthorizePatchAsync(SyncWrite w, PersonEntity row, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        RequireRight(w);
        return Task.CompletedTask;
    }

    protected override Task AfterPatchAsync(SyncWrite w, PersonEntity row, IReadOnlyCollection<string> applied, CancellationToken ct)
    {
        row.Email = UserPersons.Email(row.Email);
        return Task.CompletedTask;
    }

    protected override Task AuthorizeDeleteAsync(SyncWrite w, PersonEntity row, CancellationToken ct)
    {
        RequireRight(w);
        if (row.Source != PersonSource.Manual || row.UserId is not null)
        {
            throw new SyncRejection(SyncCodes.NotSupported, "Only manual persons without a user are deleted.");
        }
        return Task.CompletedTask;
    }

    protected override void Validate(PersonEntity row, ApiValidator v)
    {
        v.Check(!string.IsNullOrWhiteSpace(row.DisplayName) && row.DisplayName.Length <= 200, "displayName", "1 to 200 characters.")
            .Check(row.Email is null || (row.Email.Length <= 254 && row.Email.Contains('@', StringComparison.Ordinal)), "email",
                "An e-mail address (at most 254 characters).")
            .Check(row.UserType is null or "Member" or "Guest", "userType", "Member or Guest.")
            .Check(row.Notes is null || row.Notes.Length <= 100_000, "notes", "At most 100,000 characters.");
        foreach (var (key, text) in new[]
        {
            ("givenName", row.GivenName), ("surname", row.Surname), ("userPrincipalName", row.UserPrincipalName), ("jobTitle", row.JobTitle),
            ("department", row.Department), ("companyName", row.CompanyName), ("phone", row.Phone), ("mobilePhone", row.MobilePhone),
        })
        {
            v.Check(text is null || text.Length <= 200, key, "At most 200 characters.");
        }
    }
}

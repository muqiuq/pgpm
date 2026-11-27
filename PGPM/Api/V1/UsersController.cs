using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Microsoft;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Api.V1;

/// <summary>User administration (PNMTD, docs/03 §4.5, §4.6): admins only. The last active admin cannot be deleted, demoted or deactivated.</summary>
[Route("api/v1/users")]
[RequireAdmin]
public sealed class UsersController(UserAccounts accounts, PgpmDbContext db) : V1ControllerBase
{
    private static readonly string[] SortFields = ["username", "name", "admin", "lastlogin", "created"];

    /// <summary>Sort: username (default), name, admin, lastLogin, created. Search: username, display name, e-mail. A group: its members.</summary>
    [HttpGet]
    public async Task<ActionResult<Paged<UserItem>>> List([FromQuery] UserQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "username", SortFields);
        var users = db.Users.AsNoTracking().Include(u => u.Groups).ThenInclude(m => m.Group).AsQueryable();
        if (query.GroupId is { } groupId)
        {
            users = users.Where(u => u.Groups.Any(m => m.GroupId == groupId));
        }
        if (SearchText.Pattern(query.Search) is { } pattern)
        {
            users = users.Where(u => EF.Functions.ILike(u.Username, pattern, @"\") || EF.Functions.ILike(u.DisplayName, pattern, @"\")
                || (u.Email != null && EF.Functions.ILike(u.Email, pattern, @"\")));
        }
        users = (field, descending) switch
        {
            ("name", false) => users.OrderBy(u => u.DisplayName).ThenBy(u => u.NormalizedUsername),
            ("name", true) => users.OrderByDescending(u => u.DisplayName).ThenBy(u => u.NormalizedUsername),
            ("admin", false) => users.OrderBy(u => u.IsAdmin).ThenBy(u => u.NormalizedUsername),
            ("admin", true) => users.OrderByDescending(u => u.IsAdmin).ThenBy(u => u.NormalizedUsername),
            ("lastlogin", false) => users.OrderBy(u => u.LastLoginAt).ThenBy(u => u.NormalizedUsername),
            ("lastlogin", true) => users.OrderByDescending(u => u.LastLoginAt).ThenBy(u => u.NormalizedUsername),
            ("created", false) => users.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id),
            ("created", true) => users.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id),
            (_, true) => users.OrderByDescending(u => u.NormalizedUsername),
            _ => users.OrderBy(u => u.NormalizedUsername),
        };
        var page = await Paging.PageAsync(users, query, ct);
        var persons = await PersonsAsync(page.Items.Select(u => u.Id).ToList(), ct);
        return new Paged<UserItem>(page.Items.Select(u => ToItem(u, persons.GetValueOrDefault(u.Id))).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    [HttpGet("{id:guid}")]
    public async Task<UserItem> Get(Guid id, CancellationToken ct) => await ItemAsync(id, ct);

    /// <summary>A user's effective rights, with the group each level comes from.</summary>
    [HttpGet("{id:guid}/access")]
    public async Task<AccessItem> Access(Guid id, [FromServices] GroupService groups, CancellationToken ct) => await groups.AccessAsync(id, ct);

    /// <summary>A local user with a manual person and a temporary password (shown once, 7 days, changed at the first sign-in).</summary>
    [HttpPost]
    [Audit(AuditEvents.UserCreated)]
    public async Task<ActionResult<CreatedUser>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var (user, temporary) = await accounts.CreateAsync(request.Username, request.DisplayName, request.Email, request.IsAdmin, request.GroupIds, null,
            AuditWho.From(HttpContext), ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id },
            new CreatedUser(await ItemAsync(user.Id, ct), new TemporaryPassword(temporary!, user.TemporaryPasswordExpiresAt!.Value)));
    }

    /// <summary>Name, e-mail, admin flag, active state (deactivating ends the sessions), groups. 409 last_admin, membership_managed_by_entra.</summary>
    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.UserUpdated, AuditEvents.UserAdminChanged, AuditEvents.UserGroupsChanged)]
    public async Task<UserItem> Update(Guid id, UpdateUserRequest request, CancellationToken ct) =>
        await ItemAsync((await accounts.UpdateAsync(id, request.DisplayName, request.Email, request.IsAdmin, request.IsActive, request.GroupIds,
            AuditWho.From(HttpContext), ct)).Id, ct);

    [HttpDelete("{id:guid}")]
    [Audit(AuditEvents.UserDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await accounts.DeleteAsync(id, AuditWho.From(HttpContext), ct);
        return NoContent();
    }

    /// <summary>A temporary password (shown once); the user's sessions end and a lockout is lifted.</summary>
    [HttpPost("{id:guid}/reset-password")]
    [Audit(AuditEvents.PasswordReset)]
    public async Task<ActionResult<TemporaryPassword>> ResetPassword(Guid id, CancellationToken ct)
    {
        var password = await accounts.ResetPasswordAsync(id, null, AuditWho.From(HttpContext), ct);
        var user = await accounts.GetAsync(id, ct);
        return new TemporaryPassword(password!, user.TemporaryPasswordExpiresAt!.Value);
    }

    /// <summary>"Reset 2FA" for a user who lost the authenticator: 2FA off, sessions ended, a lockout lifted.</summary>
    [HttpPost("{id:guid}/reset-totp")]
    [Audit(AuditEvents.TotpReset)]
    public async Task<IActionResult> ResetTotp(Guid id, CancellationToken ct)
    {
        await accounts.ResetTotpAsync(id, AuditWho.From(HttpContext), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/unlock")]
    [Audit(AuditEvents.UserUnlocked)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
    {
        await accounts.UnlockAsync(id, AuditWho.From(HttpContext), ct);
        return NoContent();
    }

    /// <summary>
    /// Links a Microsoft account (GGITSV): an existing local user signs in with Microsoft from then on, e.g. after an
    /// account-conflict refusal. The tenant defaults to Entra:TenantId. 409 duplicate_key when another user has it.
    /// </summary>
    [HttpPut("{id:guid}/microsoft-account")]
    [Audit(AuditEvents.MicrosoftLinked)]
    public async Task<UserItem> LinkMicrosoft(Guid id, MicrosoftAccountRequest request, [FromServices] IOptions<EntraOptions> entra, CancellationToken ct)
    {
        await accounts.LinkMicrosoftAsync(id, request.ObjectId, request.TenantId, entra.Value.Tenant, AuditWho.From(HttpContext), ct);
        return await ItemAsync(id, ct);
    }

    /// <summary>Unlinks the Microsoft account; the user's sessions end. Without a password the user can't sign in until one is reset.</summary>
    [HttpDelete("{id:guid}/microsoft-account")]
    [Audit(AuditEvents.MicrosoftUnlinked)]
    public async Task<UserItem> UnlinkMicrosoft(Guid id, CancellationToken ct)
    {
        await accounts.UnlinkMicrosoftAsync(id, AuditWho.From(HttpContext), ct);
        return await ItemAsync(id, ct);
    }

    private async Task<UserItem> ItemAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Groups).ThenInclude(m => m.Group).SingleOrDefaultAsync(u => u.Id == id, ct)
            ?? throw ApiException.NotFound("User");
        return ToItem(user, (await PersonsAsync([id], ct)).GetValueOrDefault(id));
    }

    private Task<Dictionary<Guid, Guid>> PersonsAsync(IReadOnlyList<Guid> userIds, CancellationToken ct) =>
        db.Persons.AsNoTracking().Where(p => p.UserId != null && userIds.Contains(p.UserId.Value))
            .ToDictionaryAsync(p => p.UserId!.Value, p => p.Id, ct);

    internal static UserItem ToItem(UserEntity u, Guid personId) => new(u.Id, u.Username, u.DisplayName, u.Email, u.IsAdmin,
        u.Groups.Where(m => m.Group is not null).Select(m => new UserGroupRef(m.GroupId, m.Group.Name, m.Group.EntraAppRole is not null))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        u.IsActive, u.IsTotpEnabled, u.PasswordHash is not null, u.MustChangePassword, u.TemporaryPasswordExpiresAt,
        u.LockoutEnd is { } end && end > Clock.UtcNow ? end : null, u.FailedLoginCount, u.CreatedAt, u.LastLoginAt, u.LastSeenAt, u.PasswordChangedAt,
        u is { MicrosoftTenantId: { } tid, MicrosoftObjectId: { } oid } ? new MicrosoftAccount(tid, oid, u.MicrosoftUserName, u.DirectoryReadAt) : null,
        personId == Guid.Empty ? null : personId);
}

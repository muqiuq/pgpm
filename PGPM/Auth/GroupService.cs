using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Data;

namespace PGPM.Auth;

/// <summary>
/// Groups and their rights (PNMTD, docs/03 §4.7), for admins only. A change drops its members' cached sessions, so it
/// applies from their next request; nobody is signed out. A group with an Entra app role has its members set at every
/// Microsoft sign-in (GGITSV): its membership can't be edited here (409 membership_managed_by_entra).
/// </summary>
public sealed class GroupService(PgpmDbContext db, SessionCache sessions, IHttpContextAccessor accessor)
{
    public async Task<IReadOnlyList<GroupItem>> ListAsync(CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().Include(g => g.Rights).OrderBy(g => g.NormalizedName)
            .Select(g => new { Group = g, Members = g.Members.Count }).ToListAsync(ct);
        return groups.Select(g => new GroupItem(g.Group.Id, g.Group.Name, g.Group.Description, g.Group.EntraAppRole, Levels(g.Group), g.Members))
            .ToList();
    }

    public async Task<GroupDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().Include(g => g.Rights).SingleOrDefaultAsync(g => g.Id == id, ct)
            ?? throw ApiException.NotFound("Group");
        var members = await db.UserGroups.AsNoTracking().Where(m => m.GroupId == id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u).OrderBy(u => u.NormalizedUsername)
            .Select(u => new GroupMember(u.Id, u.Username, u.DisplayName, u.IsActive)).ToListAsync(ct);
        return new GroupDetail(group.Id, group.Name, group.Description, group.EntraAppRole, Levels(group), members);
    }

    public async Task<GroupDetail> CreateAsync(GroupRequest request, CancellationToken ct)
    {
        var (name, description, role, validated) = await ValidateAsync(request, null, ct);
        var rights = validated ?? [];
        var group = new GroupEntity
        {
            Id = Lib.Helper.Ids.New(),
            Name = name,
            NormalizedName = GroupEntity.Normalize(name),
            Description = description,
            EntraAppRole = role,
            Rights = rights.Select(kv => new GroupRightEntity { Area = kv.Key, Level = kv.Value }).ToList(),
        };
        db.Groups.Add(group);
        AuditLog.Record(db, AuditEvents.GroupCreated, $"{name}: {Summary(rights)}{(role is null ? "" : $", Entra app role {role}")}",
            AuditWho.From(accessor), Target(group));
        await db.SaveChangesAsync(ct);
        return await GetAsync(group.Id, ct);
    }

    public async Task<GroupDetail> UpdateAsync(Guid id, GroupRequest request, CancellationToken ct)
    {
        var group = await db.Groups.Include(g => g.Rights).SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var (name, description, role, rights) = await ValidateAsync(request, id, ct);
        var who = AuditWho.From(accessor);
        if (group.Name != name || group.Description != description || group.EntraAppRole != role)
        {
            var what = group.Name != name ? $"{group.Name} renamed {name}"
                : group.EntraAppRole != role ? $"{name}: Entra app role {group.EntraAppRole ?? "none"} → {role ?? "none"}"
                : $"{name}: description changed";
            AuditLog.Record(db, AuditEvents.GroupUpdated, what, who, Target(group));
            group.Name = name;
            group.NormalizedName = GroupEntity.Normalize(name);
            group.Description = description;
            group.EntraAppRole = role;
        }
        if (rights is not null)
        {
            await ApplyRightsAsync(group, rights, who, ct);
        }
        await db.SaveChangesAsync(ct);
        await ForgetMembersAsync(id, ct);
        return await GetAsync(id, ct);
    }

    /// <summary><c>PUT /groups/{id}/rights</c>: the group's level per area, all of them.</summary>
    public async Task<GroupDetail> SetRightsAsync(Guid id, Dictionary<Area, Level>? request, CancellationToken ct)
    {
        var group = await db.Groups.Include(g => g.Rights).SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var v = new ApiValidator();
        var rights = ValidateRights(request ?? [], v);
        v.ThrowIfInvalid();
        await ApplyRightsAsync(group, rights, AuditWho.From(accessor), ct);
        await db.SaveChangesAsync(ct);
        await ForgetMembersAsync(id, ct);
        return await GetAsync(id, ct);
    }

    private async Task ApplyRightsAsync(GroupEntity group, Dictionary<Area, Level> rights, AuditWho who, CancellationToken ct)
    {
        var name = group.Name;
        var before = Levels(group);
        var changes = RightsCatalog.Areas.Select(a => a.Area)
            .Where(a => before.GetValueOrDefault(a, Level.None) != rights.GetValueOrDefault(a, Level.None))
            .Select(a => $"{RightsCatalog.Of(a).Label}: {Show(before.GetValueOrDefault(a, Level.None))} → {Show(rights.GetValueOrDefault(a, Level.None))}")
            .ToList();
        if (changes.Count > 0)
        {
            AuditLog.Record(db, AuditEvents.GroupRightsChanged, $"{name}: {string.Join(", ", changes)}", who, Target(group));
            db.GroupRights.RemoveRange(group.Rights);
            await db.SaveChangesAsync(ct);
            group.Rights = rights.Select(kv => new GroupRightEntity { GroupId = group.Id, Area = kv.Key, Level = kv.Value }).ToList();
        }
    }

    /// <summary>Its members lose what it gave them; their accounts stay.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var members = await MemberNamesAsync(id, ct);
        var memberIds = await db.UserGroups.Where(m => m.GroupId == id).Select(m => m.UserId).ToListAsync(ct);
        AuditLog.Record(db, AuditEvents.GroupDeleted, $"{group.Name} deleted; members then: {(members.Count == 0 ? "none" : string.Join(", ", members))}",
            AuditWho.From(accessor), Target(group));
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        memberIds.ForEach(sessions.Forget);
    }

    public async Task<GroupDetail> SetMembersAsync(Guid id, GroupMembersRequest request, CancellationToken ct)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var wanted = (request.UserIds ?? []).Distinct().ToList();
        var users = await db.Users.Where(u => wanted.Contains(u.Id)).Select(u => new { u.Id, u.Username }).ToListAsync(ct);
        new ApiValidator().Check(users.Count == wanted.Count, "userIds", "Unknown user.").ThrowIfInvalid();
        var current = await db.UserGroups.Where(m => m.GroupId == id).ToListAsync(ct);
        var removed = current.Where(m => !wanted.Contains(m.UserId)).ToList();
        var added = wanted.Where(u => current.All(m => m.UserId != u)).ToList();
        if (removed.Count + added.Count > 0)
        {
            if (group.EntraAppRole is not null)
            {
                throw UserAccounts.ManagedByEntra(group);
            }
            var names = await db.Users.Where(u => removed.Select(m => m.UserId).Contains(u.Id)).Select(u => u.Username).ToListAsync(ct);
            var parts = new List<string>();
            if (added.Count > 0)
            {
                parts.Add("added " + string.Join(", ", users.Where(u => added.Contains(u.Id)).Select(u => u.Username).Order()));
            }
            if (removed.Count > 0)
            {
                parts.Add("removed " + string.Join(", ", names.Order()));
            }
            AuditLog.Record(db, AuditEvents.GroupMembersChanged, $"{group.Name}: {string.Join("; ", parts)}", AuditWho.From(accessor), Target(group));
            db.UserGroups.RemoveRange(removed);
            db.UserGroups.AddRange(added.Select(u => new UserGroupEntity { UserId = u, GroupId = id }));
            await db.SaveChangesAsync(ct);
            removed.Select(m => m.UserId).Concat(added).ToList().ForEach(sessions.Forget);
        }
        return await GetAsync(id, ct);
    }

    /// <summary>A user's effective rights, and which groups give each level (docs/03 §4.7).</summary>
    public async Task<AccessItem> AccessAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.IsAdmin }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("User");
        var groups = await db.Groups.AsNoTracking().Include(g => g.Rights).Where(g => g.Members.Any(m => m.UserId == userId))
            .OrderBy(g => g.NormalizedName).ToListAsync(ct);
        var combined = UserRights.Combine(groups.SelectMany(g => g.Rights.Select(r => (r.Area, r.Level))));
        var rights = RightsCatalog.Areas.Where(a => combined.ContainsKey(a.Area)).Select(a => new AccessLevel(a.Area, combined[a.Area],
            groups.Where(g => g.Rights.Any(r => r.Area == a.Area && r.Level == combined[a.Area])).Select(g => g.Name).ToList())).ToList();
        return new AccessItem(user.IsAdmin, groups.Select(g => new Ref(g.Id, g.Name)).ToList(), rights);
    }

    public static IReadOnlyList<RightsAreaItem> Catalog() => RightsCatalog.Areas
        .Select(a => new RightsAreaItem(a.Area, a.Label, a.Levels.Select(l => new RightsLevelItem(l.Level, l.Allows)).ToList())).ToList();

    private async Task<(string Name, string? Description, string? Role, Dictionary<Area, Level>? Rights)> ValidateAsync(GroupRequest request, Guid? id,
        CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var role = GroupEntity.NormalizeAppRole(request.EntraAppRole);
        var v = new ApiValidator()
            .Check(name.Length is >= 1 and <= 100, "name", "1 to 100 characters.")
            .Check(description is null || description.Length <= 500, "description", "At most 500 characters.")
            .Check(role is null || (role.Length <= 120 && role.All(c => c is > ' ' and < '\u007f')), "entraAppRole",
                "An app role's value: at most 120 characters, no spaces.");
        var rights = request.Rights is null ? null : ValidateRights(request.Rights, v);
        v.ThrowIfInvalid();
        var normalized = GroupEntity.Normalize(name);
        if (await db.Groups.AnyAsync(g => g.NormalizedName == normalized && g.Id != id, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"A group named {name} exists already.");
        }
        if (role is not null && await db.Groups.AnyAsync(g => g.EntraAppRole == role && g.Id != id, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"Another group follows the Entra app role {role}.");
        }
        return (name, description, role, rights);
    }

    /// <summary>The levels a request asks for: each one its area's (400 rights otherwise); None gives nothing.</summary>
    private static Dictionary<Area, Level> ValidateRights(Dictionary<Area, Level> requested, ApiValidator v)
    {
        var rights = new Dictionary<Area, Level>();
        foreach (var (area, level) in requested)
        {
            if (level == Level.None)
            {
                continue;
            }
            if (!Enum.IsDefined(area) || RightsCatalog.Of(area).Rank(level) == 0)
            {
                v.Add("rights", Enum.IsDefined(area)
                    ? $"{RightsCatalog.Of(area).Label} has no level {level}: {string.Join(", ", RightsCatalog.Of(area).Levels.Select(l => l.Level))}."
                    : $"No area {area}.");
                continue;
            }
            rights[area] = level;
        }
        return rights;
    }

    private async Task ForgetMembersAsync(Guid id, CancellationToken ct) =>
        (await db.UserGroups.Where(m => m.GroupId == id).Select(m => m.UserId).ToListAsync(ct)).ForEach(sessions.Forget);

    private Task<List<string>> MemberNamesAsync(Guid id, CancellationToken ct) =>
        db.UserGroups.Where(m => m.GroupId == id).Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Username).OrderBy(n => n).ToListAsync(ct);

    private static IReadOnlyDictionary<Area, Level> Levels(GroupEntity group) => group.Rights.ToDictionary(r => r.Area, r => r.Level);

    private static string Summary(IReadOnlyDictionary<Area, Level> rights) => rights.Count == 0 ? "no rights"
        : string.Join(", ", RightsCatalog.Areas.Where(a => rights.ContainsKey(a.Area)).Select(a => $"{a.Label} {rights[a.Area]}"));

    private static string Show(Level level) => level == Level.None ? "none" : level.ToString();

    private static AuditTarget Target(GroupEntity group) => AuditTarget.Group(group.Id, group.Name);
}

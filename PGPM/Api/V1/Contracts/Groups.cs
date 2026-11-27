using PGPM.Auth.Rights;

namespace PGPM.Api.V1.Contracts;

/// <summary>An area of the catalogue (docs/03 §6.2), its levels in order and what each allows.</summary>
public sealed record RightsAreaItem(Area Area, string Label, IReadOnlyList<RightsLevelItem> Levels);

public sealed record RightsLevelItem(Level Level, string Allows);

/// <summary>A group: its level per area (an area it gives nothing in is missing) and how many members it has.</summary>
public sealed record GroupItem(Guid Id, string Name, string? Description, string? EntraAppRole, IReadOnlyDictionary<Area, Level> Rights, int Members);

public sealed record GroupDetail(Guid Id, string Name, string? Description, string? EntraAppRole, IReadOnlyDictionary<Area, Level> Rights,
    IReadOnlyList<GroupMember> Members);

public sealed record GroupMember(Guid Id, string Username, string DisplayName, bool IsActive);

/// <summary>
/// <c>POST /groups</c>, <c>PUT /groups/{id}</c>: a level per area (None or a missing area gives nothing; on PUT, no
/// rights: unchanged), and the Entra app role whose holders are its members (upper-cased; none: members are managed here).
/// </summary>
public sealed class GroupRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? EntraAppRole { get; init; }

    public Dictionary<Area, Level>? Rights { get; init; }
}

/// <summary><c>PUT /groups/{id}/rights</c>: a level per area, all of them.</summary>
public sealed class GroupRightsRequest
{
    public Dictionary<Area, Level>? Rights { get; init; }
}

/// <summary><c>PUT /groups/{id}/members</c>: the group's members, all of them.</summary>
public sealed class GroupMembersRequest
{
    public List<Guid>? UserIds { get; init; }
}

/// <summary>What a user may do: the admin flag, their groups, and each level with the groups that give it.</summary>
public sealed record AccessItem(bool IsAdmin, IReadOnlyList<Ref> Groups, IReadOnlyList<AccessLevel> Rights);

public sealed record AccessLevel(Area Area, Level Level, IReadOnlyList<string> From);

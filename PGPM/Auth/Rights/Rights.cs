using System.Security.Claims;

namespace PGPM.Auth.Rights;

/// <summary>
/// What a group gives rights in (docs/03 §6.2): capabilities only – what a user may <i>do</i>; what they may <i>see</i>
/// is decided by sharing. Users, groups and rights themselves are no area: only the admin flag manages them.
/// </summary>
public enum Area { Notes, Ideas, Leads, Projects, Maps, Persons, Configuration, Directory, Mcp, AuditLog, System }

/// <summary>
/// The levels, by name. Each area has its own ordered list of them (<see cref="RightsCatalog"/>); a level includes the
/// ones before it in its area's list. <see cref="None"/> only appears in requests: "nothing in this area".
/// </summary>
public enum Level { None, Create, Edit, Use, View, Manage }

/// <summary>An area, its levels in order, and what each allows (the text of docs/03 §6.2, for the group page and Your access).</summary>
public sealed record AreaInfo(Area Area, string Label, IReadOnlyList<(Level Level, string Allows)> Levels)
{
    /// <summary>1 for the first level, 0 for None or a level the area doesn't have.</summary>
    public int Rank(Level level) => Levels.Select((l, i) => (l.Level, i)).Where(x => x.Level == level).Select(x => x.i + 1).FirstOrDefault();
}

/// <summary>The fixed areas and levels of docs/03 §6.2 (PNMTD's model: one level per area, higher includes lower).</summary>
public static class RightsCatalog
{
    public static readonly IReadOnlyList<AreaInfo> Areas =
    [
        new(Area.Notes, "Notes", [(Level.Create, "Create notes (and become their owner).")]),
        new(Area.Ideas, "Ideas", [(Level.Create, "Create ideas (and become their owner).")]),
        new(Area.Leads, "Leads", [(Level.Create, "Create leads (and become their owner).")]),
        new(Area.Projects, "Projects", [(Level.Create, "Create projects (and become their owner); converting an idea or a lead into a project.")]),
        new(Area.Maps, "Maps", [(Level.Create, "Create maps (and become their owner).")]),
        new(Area.Persons, "Persons", [(Level.Edit, "Create and edit manual persons, and notes on persons. Everybody reads the directory.")]),
        new(Area.Configuration, "Configuration", [
            (Level.Manage, "Statuses, sections, fields, project templates, map templates, shared saved views and configuration packages.")]),
        new(Area.Directory, "Directory", [(Level.Manage, "The Microsoft integration's settings, and running the directory sync now.")]),
        new(Area.Mcp, "MCP", [
            (Level.Use, "Own MCP tokens for AI assistants."),
            (Level.View, "Use, plus the MCP log of all users."),
            (Level.Manage, "View, plus switching off other users' tokens.")]),
        new(Area.AuditLog, "Audit log", [(Level.View, "Administration → Audit log.")]),
        new(Area.System, "System", [(Level.View, "Sync clients, live connections, storage and versions.")]),
    ];

    private static readonly Dictionary<Area, AreaInfo> ByArea = Areas.ToDictionary(a => a.Area);

    public static AreaInfo Of(Area area) => ByArea[area];

    /// <summary>"Projects:Create" → (Projects, Create), if the area has the level.</summary>
    public static bool TryParse(string? value, out Area area, out Level level)
    {
        area = default;
        level = Level.None;
        var parts = value?.Split(':');
        return parts is [var a, var l] && Enum.TryParse(a, out area) && Enum.IsDefined(area) && Enum.TryParse(l, out level)
            && Of(area).Rank(level) > 0;
    }

    public static string Name(Area area, Level level) => $"{area}:{level}";
}

/// <summary>
/// What a signed-in user may do: the admin flag, or the highest level their groups give per area. Read per request from
/// the session's claims (<see cref="SessionClaims.Admin"/>, <see cref="SessionClaims.Right"/>), never from the cookie.
/// </summary>
public sealed record UserRights(bool IsAdmin, IReadOnlyDictionary<Area, Level> Levels)
{
    public static readonly UserRights None = new(false, new Dictionary<Area, Level>());

    public bool Has(Area area, Level level)
    {
        if (IsAdmin)
        {
            return true;
        }
        var info = RightsCatalog.Of(area);
        return Levels.TryGetValue(area, out var held) && info.Rank(level) > 0 && info.Rank(held) >= info.Rank(level);
    }

    /// <summary>Several groups' levels together: the highest per area.</summary>
    public static IReadOnlyDictionary<Area, Level> Combine(IEnumerable<(Area Area, Level Level)> levels)
    {
        var result = new Dictionary<Area, Level>();
        foreach (var (area, level) in levels)
        {
            var info = RightsCatalog.Of(area);
            if (info.Rank(level) > 0 && (!result.TryGetValue(area, out var held) || info.Rank(level) > info.Rank(held)))
            {
                result[area] = level;
            }
        }
        return result;
    }

    public static UserRights Of(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return None;
        }
        var levels = Combine(principal.FindAll(SessionClaims.Right)
            .Select(c => RightsCatalog.TryParse(c.Value, out var a, out var l) ? (a, l) : (Area.Notes, Level.None)));
        return new UserRights(principal.HasClaim(SessionClaims.Admin, "true"), levels);
    }

    /// <summary>The claims a request carries for these rights.</summary>
    public IEnumerable<Claim> Claims() =>
        (IsAdmin ? [new Claim(SessionClaims.Admin, "true")] : Array.Empty<Claim>())
        .Concat(Levels.Select(kv => new Claim(SessionClaims.Right, RightsCatalog.Name(kv.Key, kv.Value))));
}

public static class RightsExtensions
{
    public static UserRights Rights(this ClaimsPrincipal principal) => UserRights.Of(principal);

    /// <summary>A check the attributes can't express (docs/03 §6.1): <c>if (!User.Has(Area.Projects, Level.Create)) throw ApiException.MissingRight(…)</c>.</summary>
    public static bool Has(this ClaimsPrincipal principal, Area area, Level level) => UserRights.Of(principal).Has(area, level);

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.HasClaim(SessionClaims.Admin, "true");
}

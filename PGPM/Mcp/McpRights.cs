using PGPM.Auth.Rights;
using PGPM.Items;

namespace PGPM.Mcp;

/// <summary>
/// What a token may do (docs/03 §7.2, PNMTD's McpRights): its user's rights – an admin counting as every area's highest
/// level –, capped by the write token's <see cref="Ceiling"/> or the read token's <see cref="ReadOnlyCap"/>. Never the admin
/// flag. Object access is capped too (<see cref="AccessCap"/>): no sharing, no deletes, no owner transfer. One place, in code.
/// </summary>
public static class McpRights
{
    /// <summary>
    /// The most the write token gets per area: creating items and maps. The areas that aren't here (Persons, Configuration,
    /// Directory, MCP, Audit log, System) are PGPM's administration.
    /// </summary>
    public static readonly IReadOnlyDictionary<Area, Level> Ceiling = new Dictionary<Area, Level>
    {
        [Area.Notes] = Level.Create,
        [Area.Ideas] = Level.Create,
        [Area.Leads] = Level.Create,
        [Area.Projects] = Level.Create,
        [Area.Maps] = Level.Create,
    };

    /// <summary>The read token: nothing. Reading needs no right in PGPM – sharing decides what a user sees (docs/03 §6.2).</summary>
    public static readonly IReadOnlyDictionary<Area, Level> ReadOnlyCap = new Dictionary<Area, Level>();

    public static UserRights For(UserRights user, McpTokenKind kind)
    {
        var cap = kind == McpTokenKind.Write ? Ceiling : ReadOnlyCap;
        var levels = new Dictionary<Area, Level>();
        foreach (var (area, max) in cap)
        {
            var info = RightsCatalog.Of(area);
            var held = user.IsAdmin ? info.Levels[^1].Level : user.Levels.GetValueOrDefault(area, Level.None);
            var rank = Math.Min(info.Rank(held), info.Rank(max));
            if (rank > 0)
            {
                levels[area] = info.Levels[rank - 1].Level;
            }
        }
        return new UserRights(false, levels);
    }

    /// <summary>The highest access a token has to any item or map: read for the read token, edit for the write token.</summary>
    public static ItemAccess AccessCap(McpTokenKind kind) => kind == McpTokenKind.Write ? ItemAccess.Edit : ItemAccess.Read;
}

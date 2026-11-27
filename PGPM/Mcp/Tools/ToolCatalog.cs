using PGPM.Auth.Rights;

namespace PGPM.Mcp.Tools;

/// <summary>
/// Every tool, in one list (docs/03 §7.2–7.3, PNMTD's ToolCatalog). The rules about who gets what are here and nowhere else:
/// the read token only reads; nothing outside the token's capped rights (<see cref="McpRights"/>). One list of specs per
/// area: items (with <c>create_{kind}</c> per <see cref="ItemKinds"/>), persons, links, milestones and gates, maps, the
/// conversion, the lookups.
/// </summary>
public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        .. ItemTools.All,
        .. PersonTools.All,
        .. LinkTools.All,
        .. MilestoneTools.All,
        .. MapTools.All,
        .. ConvertTools.All,
        .. LookupTools.All,
    ];

    private static readonly Dictionary<string, ToolSpec> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static ToolSpec? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>Whether a token of this kind is ever offered the tool, rights aside.</summary>
    public static bool KindAllows(ToolSpec tool, McpTokenKind kind) => kind == McpTokenKind.Write || tool.Kind is ToolKind.Read or ToolKind.Ask;

    /// <summary>A token's tools: its kind's, within its capped rights.</summary>
    public static IEnumerable<ToolSpec> ForMcp(UserRights rights, McpTokenKind kind) => All.Where(t => KindAllows(t, kind) && t.Allows(rights));
}

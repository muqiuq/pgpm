using PGPM.Auth.Rights;

namespace PGPM.Mcp.Tools;

/// <summary>
/// Every tool, in one list (docs/03 §7.2, PNMTD's ToolCatalog). The rules about who gets what are here and nowhere else:
/// the read token only reads; nothing outside the token's capped rights (<see cref="McpRights"/>).
/// <para>
/// A later phase adds its tools as specs: phase 3 <c>assign_person</c>, <c>unassign_person</c>, <c>link_items</c>,
/// <c>unlink_items</c>, <c>convert_to_project</c> (Action, <c>Requires</c> Projects: Create) and – through
/// <see cref="ItemKinds"/> – <c>create_idea</c>, <c>create_lead</c>, <c>create_project</c>; phase 4 <c>add_milestone</c>,
/// <c>update_milestone</c>, <c>set_dod_item</c>, <c>record_gate_decision</c>; phase 5 <c>list_maps</c>, <c>get_map</c>,
/// <c>add_item_to_map</c>. Each one a list of specs, added below.
/// </para>
/// </summary>
public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        .. ItemTools.All,
        .. LookupTools.All,
    ];

    private static readonly Dictionary<string, ToolSpec> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static ToolSpec? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>Whether a token of this kind is ever offered the tool, rights aside.</summary>
    public static bool KindAllows(ToolSpec tool, McpTokenKind kind) => kind == McpTokenKind.Write || tool.Kind is ToolKind.Read or ToolKind.Ask;

    /// <summary>A token's tools: its kind's, within its capped rights.</summary>
    public static IEnumerable<ToolSpec> ForMcp(UserRights rights, McpTokenKind kind) => All.Where(t => KindAllows(t, kind) && t.Allows(rights));
}

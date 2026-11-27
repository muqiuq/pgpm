using PGPM.Auth;
using PGPM.Items;
using PGPM.Persons;
using PGPM.Settings;
using PGPM.Sharing;

namespace PGPM.Sync;

/// <summary>
/// <c>groups</c> (docs/02 §3) [S, ro]: names and descriptions for the share picker, written by the admin endpoints only;
/// never the normalized name or the Entra app role. Deleted groups stay as tombstones.
/// </summary>
public sealed class GroupsTable() : SyncTable<GroupEntity>("groups")
{
    public override bool ReadOnly => true;

    public override bool History => false;

    public override IReadOnlySet<string> Hidden { get; } = new HashSet<string>(StringComparer.Ordinal) { "normalizedName", "entraAppRole" };

    protected override GroupEntity Create() => new() { Name = "", NormalizedName = "" };

    public override IQueryable<GroupEntity> Visible(IQueryable<GroupEntity> rows, Viewer viewer, AccessService access) => rows;
}

/// <summary>
/// Every synced table, in dependency order: parents before children (a restore un-deletes in this order, a purge deletes
/// in the reverse). The pull reads them all, and a test asserts that every <see cref="ISyncedEntity"/> is here (PLM
/// pitfall: a table missing from the cursor). Phase 3 adds statuses, assignments, item links, files … – one descriptor each.
/// </summary>
public static class SyncTables
{
    public static readonly IReadOnlyList<SyncTable> All =
    [
        new GroupsTable(),
        new PersonsTable(),
        new ItemsTable(),
        new ItemSharesTable(),
        new UserSettingsTable(),
    ];

    private static readonly Dictionary<string, SyncTable> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    private static readonly Dictionary<Type, SyncTable> ByEntity = All.ToDictionary(t => t.EntityType);

    public static SyncTable? Find(string name) => ByName.GetValueOrDefault(name);

    public static SyncTable Get(string name) => ByName[name];

    public static SyncTable? ByType(Type type) => ByEntity.GetValueOrDefault(type);

    /// <summary>The tables with a parent link to <paramref name="table"/>, and the link.</summary>
    public static IEnumerable<(SyncTable Child, ParentRef Parent)> ChildrenOf(string table) =>
        All.SelectMany(t => t.Parents.Where(p => p.Table == table).Select(p => (t, p)));
}

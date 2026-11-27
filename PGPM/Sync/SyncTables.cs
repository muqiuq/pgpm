using PGPM.Auth;
using PGPM.Config;
using PGPM.Files;
using PGPM.Items;
using PGPM.Maps;
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
/// pitfall: a table missing from the cursor). The wire lists the tables of a page in this order.
/// </summary>
public static class SyncTables
{
    public static readonly IReadOnlyList<SyncTable> All =
    [
        new GroupsTable(),
        new PersonsTable(),
        // Configuration (docs/02 §7) [S, ro], saved views [S].
        new StatusesTable(),
        new SectionsTable(),
        new FieldDefinitionsTable(),
        new ProjectTemplatesTable(),
        new TemplateStatusesTable(),
        new TemplateSectionsTable(),
        new TemplateFieldDefaultsTable(),
        new TemplateMilestonesTable(),
        new AssignmentRolesTable(),
        new SavedViewsTable(),
        // Items and what hangs below them (docs/02 §6–§8).
        new ItemsTable(),
        new ItemSharesTable(),
        new MapsTable(),
        new MapSharesTable(),
        new FilesTable(),
        new ItemSectionsTable(),
        new FieldValuesTable(),
        new MilestonesTable(),
        new DodItemsTable(),
        new AssignmentsTable(),
        new ItemLinksTable(),
        // Maps (docs/02 §9).
        new MapLayersTable(),
        new MapElementsTable(),
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

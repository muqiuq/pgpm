using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Lib.Helper;

namespace PGPM.Data.CustomMigrations;

/// <summary>
/// The seeded groups of docs/03 §6.2, with fixed ids, once per database (a key_values marker, so a group an admin deleted
/// stays deleted): <b>Members</b> (Notes, Ideas, Leads, Projects: Create; Maps: Create; MCP: Use) and <b>Viewers</b> (no
/// rights: they see what is shared with them). Phase 4's <c>core</c> configuration package takes them over.
/// </summary>
public sealed class SeedGroups : ICustomMigration
{
    public const string Marker = "seed.groups";

    public bool ShouldRun(PgpmDbContext db) => !db.KeyValues.Any(k => k.Key == Marker);

    public void Run(PgpmDbContext db)
    {
        var groups = new[]
        {
            (GroupEntity.MembersId, "Members", "Create notes, ideas, leads, projects and maps; own MCP tokens.",
                new[] { Area.Notes, Area.Ideas, Area.Leads, Area.Projects, Area.Maps }.Select(a => (a, Level.Create)).Append((Area.Mcp, Level.Use)).ToArray()),
            (GroupEntity.ViewersId, "Viewers", "No rights: they see what is shared with them.", Array.Empty<(Area, Level)>()),
        };
        foreach (var (id, name, description, rights) in groups)
        {
            if (db.Groups.Any(g => g.Id == id || g.NormalizedName == GroupEntity.Normalize(name)))
            {
                continue;
            }
            db.Groups.Add(new GroupEntity
            {
                Id = id,
                Name = name,
                NormalizedName = GroupEntity.Normalize(name),
                Description = description,
                Rights = rights.Select(r => new GroupRightEntity { GroupId = id, Area = r.Item1, Level = r.Item2 }).ToList(),
            });
            AuditLog.Record(db, AuditEvents.GroupCreated, $"{name}: seeded", AuditWho.Seed, AuditTarget.Group(id, name));
        }
        db.KeyValues.Add(new KeyValueEntity { Key = Marker, Value = "1", UpdatedAt = Clock.UtcNow });
        db.SaveChanges();
    }
}

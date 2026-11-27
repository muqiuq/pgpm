namespace PGPM.Data.CustomMigrations;

/// <summary>
/// A data migration after the schema migrations (PNMTD): found by reflection (a public parameterless class of this
/// assembly), run in name order on every start while <see cref="ShouldRun"/> says so – so it must be idempotent. For
/// example: seeding the default statuses, the default layer for old maps.
/// </summary>
public interface ICustomMigration
{
    bool ShouldRun(PgpmDbContext db);

    void Run(PgpmDbContext db);
}

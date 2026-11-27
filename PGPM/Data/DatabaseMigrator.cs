using Microsoft.EntityFrameworkCore;
using PGPM.Data.CustomMigrations;

namespace PGPM.Data;

/// <summary>
/// Brings the database to this version's schema: at startup (<see cref="DatabaseMigrationService"/>) and from
/// `pgpm db migrate`. A database that a newer version migrated is refused: going back means restoring the backup taken
/// before the update. After the EF migrations, the <see cref="ICustomMigration"/>s run (data that SQL can't produce:
/// seeds, back-fills).
/// </summary>
public static class DatabaseMigrator
{
    public static void Migrate(PgpmDbContext db, ILogger logger)
    {
        var known = db.Database.GetMigrations().ToHashSet();
        var unknown = db.Database.GetAppliedMigrations().Where(m => !known.Contains(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"The database was migrated by a newer PGPM ({string.Join(", ", unknown)}). " +
                "Run that version, or restore the backup taken before the update.");
        }
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            db.Database.Migrate();
        }
        RunCustomMigrations(db, logger);
    }

    /// <summary>Every <see cref="ICustomMigration"/> of this assembly, by name, that says it should run.</summary>
    internal static void RunCustomMigrations(PgpmDbContext db, ILogger logger)
    {
        foreach (var migration in CustomMigrationTypes().Select(t => (ICustomMigration)Activator.CreateInstance(t)!))
        {
            if (migration.ShouldRun(db))
            {
                logger.LogInformation("Running custom migration {Migration}", migration.GetType().Name);
                migration.Run(db);
            }
        }
    }

    internal static IEnumerable<Type> CustomMigrationTypes() =>
        typeof(ICustomMigration).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICustomMigration).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal);
}

/// <summary>
/// Registered before every other hosted service, so they start on a migrated database. Then the first admin while there
/// is no user (Auth:InitialAdmin:*, PNMTD's UserAccounts.SeedAsync): a generated password is logged once.
/// </summary>
public sealed class DatabaseMigrationService(IDbContextFactory<PgpmDbContext> factory, IConfiguration configuration,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        DatabaseMigrator.Migrate(db, logger);
        var (admin, generated) = await new Auth.UserAccounts(db).SeedAsync(configuration, cancellationToken);
        if (admin is not null)
        {
            if (generated is not null)
            {
                logger.LogWarning("Created the first admin {Username} with the password {Password} – shown only now; it must be changed at the "
                    + "first sign-in. Set Auth__InitialAdmin__Password to choose it.", admin.Username, generated);
            }
            else
            {
                logger.LogInformation("Created the first admin {Username} with Auth:InitialAdmin:Password; it must be changed at the first sign-in.",
                    admin.Username);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

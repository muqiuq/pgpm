using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Packages;

/// <summary>
/// The first start's configuration (docs/03 §9, §17): after the migration, the built-in packages of
/// <c>Packages:SeedOnFirstStart</c> (<c>core,innovation-ai</c>) are imported once per database (a <c>key_values</c> marker;
/// later imports are the admins'). They take over the migration's seed groups. Shared views and map templates belong to the
/// first admin. A failure is logged and doesn't stop the start: an admin can import the packages by hand.
/// </summary>
public sealed class ConfigPackageSeeder(IServiceScopeFactory scopes, IOptions<PackageOptions> options, ILogger<ConfigPackageSeeder> logger) : IHostedService
{
    public const string Marker = "seed.packages";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var names = options.Value.SeedOnFirstStart.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0)
        {
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PgpmDbContext>();
        if (await db.KeyValues.AnyAsync(k => k.Key == Marker, cancellationToken))
        {
            return;
        }
        try
        {
            await SeedAsync(scope.ServiceProvider, names, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Importing the configuration packages {Packages} failed; import them under Administration → Configuration packages",
                string.Join(", ", names));
        }
    }

    /// <summary>Imports the packages in order, overwriting what the migration seeded, and sets the marker.</summary>
    public static async Task SeedAsync(IServiceProvider services, IReadOnlyList<string> names, CancellationToken ct)
    {
        var db = services.GetRequiredService<PgpmDbContext>();
        var importer = services.GetRequiredService<PackageImporter>();
        var admin = await db.Users.AsNoTracking().Where(u => u.IsAdmin).OrderBy(u => u.CreatedAt).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        var viewer = new Viewer(admin ?? Guid.Empty, true, [], new UserRights(true, new Dictionary<Area, Level>()));
        foreach (var name in names)
        {
            if (!BuiltInPackages.Exists(name))
            {
                throw new InvalidOperationException($"Packages:SeedOnFirstStart names {name}, which is no built-in package.");
            }
            var package = BuiltInPackages.Load(name);
            // A first start: nothing was edited here yet, the migration's seeds become the package's.
            var keys = PackageChecker.Collections.SelectMany(c => (package[c] as System.Text.Json.Nodes.JsonObject ?? []).Select(p => p.Key)).ToList();
            var result = await importer.ApplyAsync(viewer, package, keys, ChangeSource.System, ct);
            services.GetRequiredService<ILogger<ConfigPackageSeeder>>().LogInformation(
                "Configuration package {Package} imported: {Created} created, {Updated} updated", name, result.Created, result.Updated);
            db.ChangeTracker.Clear();
        }
        db.KeyValues.Add(new KeyValueEntity { Key = Marker, Value = string.Join(",", names), UpdatedAt = Clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PGPM.Lib.Helper;

namespace PGPM.Data;

/// <summary>
/// PGPM's database: PostgreSQL 18 through Npgsql (docs/02-data-model.md §1, docs/03 §3.2). Conventions for every table:
/// <list type="bullet">
/// <item>snake_case tables and columns (<c>EFCore.NamingConventions</c>): <c>KeyValueEntity.UpdatedAt</c> →
///   <c>key_values.updated_at</c>.</item>
/// <item>Every <see cref="DateTime"/> is UTC in <c>timestamptz</c> (<see cref="UtcDateTimeConverter"/>).</item>
/// <item>Enums are lower-case text (<see cref="LowerCaseEnumConverter{TEnum}"/>); the CHECK constraint comes with each
///   table's migration.</item>
/// <item>Guid keys are UUID v7: Npgsql generates them on the client (and synced rows come with one from the PWA).</item>
/// <item>No lazy loading: every navigation a query needs is in an Include or a projection.</item>
/// </list>
/// </summary>
public class PgpmDbContext(DbContextOptions<PgpmDbContext> options) : DbContext(options)
{
    public DbSet<KeyValueEntity> KeyValues => Set<KeyValueEntity>();

    /// <summary>The one place the provider is chosen, for the app, the CLI, the design-time factory and the tests.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    public static PgpmDbContext Create(string connectionString) =>
        new(((DbContextOptionsBuilder<PgpmDbContext>)Configure(new DbContextOptionsBuilder<PgpmDbContext>(), connectionString)).Options);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // timestamptz takes UTC only. An Unspecified value is taken as UTC, a Local one converted (Clock.AsUtc).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KeyValueEntity>(e =>
        {
            e.ToTable("key_values");
            e.HasKey(k => k.Key);
        });

        EnumsAsLowerCaseText(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>Every enum property without a conversion of its own becomes lower-case text (docs/02-data-model.md §1).</summary>
    private static void EnumsAsLowerCaseText(ModelBuilder modelBuilder)
    {
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (type.IsEnum && property.GetValueConverter() is null)
            {
                property.SetValueConverter((ValueConverter)Activator.CreateInstance(typeof(LowerCaseEnumConverter<>).MakeGenericType(type))!);
            }
        }
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => Clock.AsUtc(v),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? Clock.AsUtc(v.Value) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}

/// <summary>An enum as lower-case text: <c>GateDecision.NoGo</c> ↔ <c>"nogo"</c>. Reading ignores case.</summary>
public sealed class LowerCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => v.ToString().ToLowerInvariant(),
    v => Enum.Parse<TEnum>(v, true))
    where TEnum : struct, Enum;

/// <summary>A few small values with a name (docs/02-data-model.md §3: task health, small system settings).</summary>
public sealed class KeyValueEntity
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The connection: Database:ConnectionString when set (tests), otherwise built from Database:Host, :Port, :Name,
/// :Username and :Password (the compose file sets these).
/// </summary>
public static class DatabaseSettings
{
    public static string ConnectionString(IConfiguration configuration)
    {
        var explicitString = configuration["Database:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(explicitString))
        {
            return explicitString;
        }
        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = configuration["Database:Host"] ?? "localhost",
            Port = int.TryParse(configuration["Database:Port"], out var port) ? port : 5432,
            Database = configuration["Database:Name"] ?? "pgpm",
            Username = configuration["Database:Username"] ?? "pgpm",
            Password = configuration["Database:Password"],
        };
        return builder.ConnectionString;
    }
}

/// <summary>For `dotnet ef migrations add`: never connects.</summary>
public sealed class PgpmDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PgpmDbContext>
{
    public PgpmDbContext CreateDbContext(string[] args) =>
        PgpmDbContext.Create("Host=localhost;Database=pgpm;Username=pgpm;Password=design-time");
}

public static class DataSetup
{
    /// <summary>
    /// A context per request for controllers (AddDbContextFactory registers the scoped context too), the factory for
    /// background tasks and the migration service.
    /// </summary>
    public static IServiceCollection AddPgpmData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = DatabaseSettings.ConnectionString(configuration);
        services.AddDbContextFactory<PgpmDbContext>(o => PgpmDbContext.Configure(o, connectionString));
        return services;
    }
}

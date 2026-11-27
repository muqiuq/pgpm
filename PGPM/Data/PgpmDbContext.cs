using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Persons;
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

    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<GroupEntity> Groups => Set<GroupEntity>();

    public DbSet<UserGroupEntity> UserGroups => Set<UserGroupEntity>();

    public DbSet<GroupRightEntity> GroupRights => Set<GroupRightEntity>();

    public DbSet<PersonEntity> Persons => Set<PersonEntity>();

    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    /// <summary>
    /// The one place the provider is chosen, for the app, the CLI, the design-time factory and the tests; with the audit's
    /// change interceptor, which only acts inside a write request's audit scope.
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention().AddInterceptors(AuditChangesInterceptor.Instance);

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

        // Identity (docs/02 §3). Phase 2 adds the synced-row columns to groups and persons.
        modelBuilder.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.NormalizedUsername).IsUnique();
            e.HasIndex(u => new { u.MicrosoftTenantId, u.MicrosoftObjectId }).IsUnique()
                .HasFilter("microsoft_object_id IS NOT NULL");
            e.Property(u => u.AccessEpoch).HasDefaultValue(1);
            e.Ignore(u => u.HasMicrosoftAccount);
        });
        modelBuilder.Entity<GroupEntity>(e =>
        {
            e.ToTable("groups");
            e.HasIndex(g => g.NormalizedName).IsUnique();
            e.HasIndex(g => g.EntraAppRole).IsUnique().HasFilter("entra_app_role IS NOT NULL");
            e.Property(g => g.Description).HasMaxLength(500);
        });
        modelBuilder.Entity<UserGroupEntity>(e =>
        {
            e.ToTable("user_groups");
            e.HasKey(m => new { m.UserId, m.GroupId });
            e.HasOne(m => m.User).WithMany(u => u.Groups).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.GroupId);
        });
        modelBuilder.Entity<GroupRightEntity>(e =>
        {
            e.ToTable("group_rights", t =>
            {
                EnumCheck<Area>(t, "group_rights", "area");
                EnumCheck(t, "group_rights", "level", Enum.GetValues<Level>().Where(l => l != Level.None));
            });
            e.HasKey(r => new { r.GroupId, r.Area });
            e.HasOne(r => r.Group).WithMany(g => g.Rights).HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        // Persons (docs/02 §5), minimal: every user has one.
        modelBuilder.Entity<PersonEntity>(e =>
        {
            e.ToTable("persons", t =>
            {
                EnumCheck<PersonSource>(t, "persons", "source");
                t.HasCheckConstraint("ck_persons_user_type", "user_type IS NULL OR user_type IN ('Member', 'Guest')");
            });
            e.HasIndex(p => new { p.EntraTenantId, p.EntraObjectId }).IsUnique().HasFilter("entra_object_id IS NOT NULL");
            e.HasIndex(p => p.Email);
            e.HasIndex(p => p.UserId).IsUnique().HasFilter("user_id IS NOT NULL");
            e.HasOne<UserEntity>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        // The audit log (docs/02 §12): no foreign keys, an entry outlives its user.
        modelBuilder.Entity<AuditLogEntity>(e =>
        {
            e.ToTable("audit_log", t => EnumCheck<AuditActor>(t, "audit_log", "actor"));
            e.Property(a => a.Detail).HasColumnType("jsonb");
            e.HasIndex(a => a.At);
            e.HasIndex(a => new { a.SubjectId, a.At });
            e.HasIndex(a => new { a.ActorUserId, a.At });
            e.HasIndex(a => a.Event);
        });

        EnumsAsLowerCaseText(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>The CHECK constraint of an enum's lower-case text column (docs/02-data-model.md §1): <c>ck_&lt;table&gt;_&lt;column&gt;</c>.</summary>
    internal static void EnumCheck<TEnum>(TableBuilder table, string tableName, string column, IEnumerable<TEnum>? values = null)
        where TEnum : struct, Enum
    {
        var allowed = (values ?? Enum.GetValues<TEnum>()).Select(v => $"'{v.ToString().ToLowerInvariant()}'");
        table.HasCheckConstraint($"ck_{tableName}_{column}", $"{column} IN ({string.Join(", ", allowed)})");
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

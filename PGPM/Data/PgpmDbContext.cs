using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Items;
using PGPM.Mcp;
using PGPM.Persons;
using PGPM.Persons.DirectorySync;
using PGPM.Lib.Helper;
using PGPM.Settings;
using PGPM.Sync;

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

    // The sync core (docs/02 §6, §11–§13).
    public DbSet<ItemEntity> Items => Set<ItemEntity>();

    public DbSet<ItemShareEntity> ItemShares => Set<ItemShareEntity>();

    public DbSet<NumberCounterEntity> NumberCounters => Set<NumberCounterEntity>();

    public DbSet<UserSettingEntity> UserSettings => Set<UserSettingEntity>();

    public DbSet<SyncCounterEntity> SyncCounter => Set<SyncCounterEntity>();

    public DbSet<SyncStateEntity> SyncState => Set<SyncStateEntity>();

    public DbSet<DeleteBatchEntity> DeleteBatches => Set<DeleteBatchEntity>();

    public DbSet<SyncMutationEntity> SyncMutations => Set<SyncMutationEntity>();

    public DbSet<SyncClientEntity> SyncClients => Set<SyncClientEntity>();

    public DbSet<SyncEvictionEntity> SyncEvictions => Set<SyncEvictionEntity>();

    public DbSet<ChangeLogEntity> ChangeLog => Set<ChangeLogEntity>();

    // Directory sync and MCP (docs/02 §3, §5; phase 6).
    public DbSet<DirectorySyncStateEntity> DirectorySyncState => Set<DirectorySyncStateEntity>();

    public DbSet<McpTokenEntity> McpTokens => Set<McpTokenEntity>();

    public DbSet<McpLogEntity> McpLog => Set<McpLogEntity>();

    /// <summary>The context's part in the sync counter (its lock, the head to publish after the commit).</summary>
    internal SyncSession SyncSession { get; } = new();

    /// <summary>
    /// The one place the provider is chosen, for the app, the CLI, the design-time factory and the tests; with the
    /// interceptors, in this order: the audit's (only inside a write request's audit scope), the History's
    /// (<c>change_log</c>), the stamp of synced rows (seq, rev, updated_*), and the commit's, which publishes the new head
    /// to the live connections (<paramref name="feed"/>: none for the CLI, another process).
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString, ChangeFeed? feed = null) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention()
            .AddInterceptors(AuditChangesInterceptor.Instance, ChangeLogInterceptor.Instance, SyncStampInterceptor.Instance,
                new SyncCommitInterceptor(feed))
            // A deleted group is a tombstone its memberships and shares never point at (GroupService removes them).
            .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

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

        // Identity (docs/02 §3). Groups and persons are synced rows (phase 2).
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
            e.HasIndex(g => g.NormalizedName).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(g => g.EntraAppRole).IsUnique().HasFilter("entra_app_role IS NOT NULL");
            e.Property(g => g.Description).HasMaxLength(500);
            // A deleted group stays as a tombstone for the clients; nothing but the sync sees it.
            e.HasQueryFilter(g => !g.Deleted);
            Synced(e);
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
            Synced(e);
        });

        // The directory sync's state per tenant (docs/02 §5).
        modelBuilder.Entity<DirectorySyncStateEntity>(e =>
        {
            e.ToTable("directory_sync_state", t =>
            {
                t.HasCheckConstraint("ck_directory_sync_state_last_status",
                    $"last_status IS NULL OR last_status IN ({string.Join(", ", DirectoryRunStatus.All.Select(v => $"'{v}'"))})");
                t.HasCheckConstraint("ck_directory_sync_state_last_mode", "last_mode IS NULL OR last_mode IN ('full', 'delta')");
            });
            e.HasKey(s => s.TenantId);
            e.Property(s => s.TenantId).ValueGeneratedNever();
        });

        // MCP (docs/02 §3): two tokens per user, the log of every request. No foreign key from the log: it outlives its user.
        modelBuilder.Entity<McpTokenEntity>(e =>
        {
            e.ToTable("mcp_tokens", t =>
            {
                EnumCheck<McpTokenKind>(t, "mcp_tokens", "kind");
                t.HasCheckConstraint("ck_mcp_tokens_ended_why",
                    $"ended_why IS NULL OR ended_why IN ({string.Join(", ", McpTokenEnds.All.Select(v => $"'{v}'"))})");
            });
            e.HasOne<UserEntity>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => new { t.UserId, t.Kind }).IsUnique();
            e.HasIndex(t => t.Hash).IsUnique().HasFilter("hash IS NOT NULL");
            e.Property(t => t.Hint).HasMaxLength(8);
            e.Property(t => t.LastUsedFrom).HasMaxLength(64);
            e.Property(t => t.LastClient).HasMaxLength(200);
            e.Property(t => t.EndedNote).HasMaxLength(300);
        });
        modelBuilder.Entity<McpLogEntity>(e =>
        {
            e.ToTable("mcp_log", t =>
            {
                t.HasCheckConstraint("ck_mcp_log_outcome", $"outcome IN ({string.Join(", ", McpOutcomes.All.Select(v => $"'{v}'"))})");
                t.HasCheckConstraint("ck_mcp_log_kind", "kind IS NULL OR kind IN ('read', 'write')");
            });
            e.Property(l => l.Id).UseIdentityAlwaysColumn();
            e.Property(l => l.Arguments).HasColumnType("jsonb");
            e.Property(l => l.Targets).HasColumnType("jsonb");
            e.HasIndex(l => l.At);
            e.HasIndex(l => new { l.TokenId, l.Id });
            e.HasIndex(l => new { l.UserId, l.Id });
        });

        // Items and sharing (docs/02 §4, §6). No foreign keys to users: an item outlives its owner's account.
        modelBuilder.Entity<ItemEntity>(e =>
        {
            e.ToTable("items", t =>
            {
                EnumCheck<ItemType>(t, "items", "type");
                t.HasCheckConstraint("ck_items_status", "type = 'note' OR status_id IS NOT NULL");
                t.HasCheckConstraint("ck_items_title", "char_length(title) BETWEEN 1 AND 300");
                t.HasCheckConstraint("ck_items_lead_probability", "lead_probability IS NULL OR lead_probability BETWEEN 0 AND 100");
            });
            e.Property(i => i.LeadValue).HasPrecision(14, 2);
            e.HasIndex(i => new { i.Type, i.Deleted });
            e.HasIndex(i => i.StatusId);
            e.HasIndex(i => i.OwnerUserId);
            e.HasIndex(i => i.Tags).HasMethod("gin");
            Synced(e);
        });
        modelBuilder.Entity<ItemShareEntity>(e =>
        {
            e.ToTable("item_shares", t => EnumCheck<ItemAccess>(t, "item_shares", "access", [ItemAccess.Read, ItemAccess.Edit, ItemAccess.Manage]));
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(s => s.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<GroupEntity>().WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.ItemId, s.GroupId }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(s => new { s.GroupId, s.ItemId }).HasFilter("NOT deleted");
            Synced(e);
        });
        modelBuilder.Entity<NumberCounterEntity>(e =>
        {
            e.ToTable("number_counters");
            e.HasKey(n => n.Type);
        });

        // User settings (docs/02 §13): own rows only.
        modelBuilder.Entity<UserSettingEntity>(e =>
        {
            e.ToTable("user_settings");
            e.Property(s => s.Value).HasColumnType("jsonb");
            e.HasOne<UserEntity>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.Key }).IsUnique().HasFilter("NOT deleted");
            Synced(e);
        });

        // Synchronization (docs/02 §11) and the History (§12). The counter's and the state's single rows come with the migration.
        modelBuilder.Entity<SyncCounterEntity>(e =>
        {
            e.ToTable("sync_counter");
            e.Property(c => c.Id).ValueGeneratedNever();
        });
        modelBuilder.Entity<SyncStateEntity>(e =>
        {
            e.ToTable("sync_state");
            e.Property(c => c.Id).ValueGeneratedNever();
        });
        modelBuilder.Entity<DeleteBatchEntity>(e =>
        {
            e.ToTable("delete_batches", t => EnumCheck<DeleteRootKind>(t, "delete_batches", "root_kind"));
            e.HasIndex(b => new { b.RootKind, b.DeletedAt });
            e.HasIndex(b => b.RootId);
        });
        modelBuilder.Entity<SyncMutationEntity>(e =>
        {
            e.ToTable("sync_mutations");
            e.HasKey(m => m.MutationId);
            e.Property(m => m.Result).HasColumnType("jsonb");
            e.HasIndex(m => new { m.UserId, m.BatchId });
            e.HasIndex(m => m.ProcessedAt);
        });
        modelBuilder.Entity<SyncClientEntity>(e =>
        {
            e.ToTable("sync_clients");
            e.Property(c => c.Id).ValueGeneratedNever();
            e.HasOne<UserEntity>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.UserId);
        });
        modelBuilder.Entity<SyncEvictionEntity>(e =>
        {
            e.ToTable("sync_evictions", t => EnumCheck<DeleteRootKind>(t, "sync_evictions", "root_kind", [DeleteRootKind.Item, DeleteRootKind.Map]));
            e.HasIndex(v => new { v.UserId, v.Seq });
            e.HasIndex(v => v.At);
        });
        modelBuilder.Entity<ChangeLogEntity>(e =>
        {
            e.ToTable("change_log", t =>
            {
                EnumCheck<ChangeSource>(t, "change_log", "source");
                t.HasCheckConstraint("ck_change_log_action", $"action IN ({string.Join(", ", ChangeActions.All.Select(a => $"'{a}'"))})");
            });
            e.Property(c => c.Changes).HasColumnType("jsonb");
            e.HasIndex(c => new { c.ItemId, c.At }).IsDescending(false, true);
            e.HasIndex(c => new { c.MapId, c.At }).IsDescending(false, true);
            e.HasIndex(c => new { c.Entity, c.EntityId });
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

    /// <summary>The synced-row columns' indexes (docs/02 §1.1): seq unique (the pull's cursor), the delete batch's rows.</summary>
    private static void Synced<T>(EntityTypeBuilder<T> e) where T : class, ISyncedEntity
    {
        e.HasIndex(nameof(ISyncedEntity.Seq)).IsUnique();
        e.HasIndex(nameof(ISyncedEntity.DeleteBatchId)).HasFilter("delete_batch_id IS NOT NULL");
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
        services.AddSingleton<ChangeFeed>();
        services.AddDbContextFactory<PgpmDbContext>((sp, o) => PgpmDbContext.Configure(o, connectionString, sp.GetRequiredService<ChangeFeed>()));
        return services;
    }
}

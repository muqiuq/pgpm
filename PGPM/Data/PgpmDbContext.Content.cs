using Microsoft.EntityFrameworkCore;
using PGPM.Config;
using PGPM.Files;
using PGPM.Items;
using PGPM.Maps;

namespace PGPM.Data;

/// <summary>
/// The tables of phases 3–5 (docs/02 §6–§9): the items' children, the configuration, files and maps. Children have a
/// foreign key to the row they belong to (cascading: a purge removes them anyway); references a row may outlive (a
/// status, a template, a cover image, a connector's end) have none. Fractional keys sort with <c>COLLATE "C"</c> (§10).
/// </summary>
public partial class PgpmDbContext
{
    public DbSet<StatusEntity> Statuses => Set<StatusEntity>();

    public DbSet<SectionEntity> Sections => Set<SectionEntity>();

    public DbSet<SectionGroupEntity> SectionGroups => Set<SectionGroupEntity>();

    public DbSet<FieldDefinitionEntity> FieldDefinitions => Set<FieldDefinitionEntity>();

    public DbSet<ProjectTemplateEntity> ProjectTemplates => Set<ProjectTemplateEntity>();

    public DbSet<TemplateStatusEntity> TemplateStatuses => Set<TemplateStatusEntity>();

    public DbSet<TemplateSectionEntity> TemplateSections => Set<TemplateSectionEntity>();

    public DbSet<TemplateFieldDefaultEntity> TemplateFieldDefaults => Set<TemplateFieldDefaultEntity>();

    public DbSet<TemplateMilestoneEntity> TemplateMilestones => Set<TemplateMilestoneEntity>();

    public DbSet<AssignmentRoleEntity> AssignmentRoles => Set<AssignmentRoleEntity>();

    public DbSet<SavedViewEntity> SavedViews => Set<SavedViewEntity>();

    public DbSet<ConfigKeyEntity> ConfigKeys => Set<ConfigKeyEntity>();

    public DbSet<AssignmentEntity> Assignments => Set<AssignmentEntity>();

    public DbSet<ItemLinkEntity> ItemLinks => Set<ItemLinkEntity>();

    public DbSet<ItemSectionEntity> ItemSections => Set<ItemSectionEntity>();

    public DbSet<FieldValueEntity> FieldValues => Set<FieldValueEntity>();

    public DbSet<MilestoneEntity> Milestones => Set<MilestoneEntity>();

    public DbSet<DodItemEntity> DodItems => Set<DodItemEntity>();

    public DbSet<FileEntity> Files => Set<FileEntity>();

    public DbSet<FileUploadEntity> FileUploads => Set<FileUploadEntity>();

    public DbSet<MapEntity> Maps => Set<MapEntity>();

    public DbSet<MapShareEntity> MapShares => Set<MapShareEntity>();

    public DbSet<MapLayerEntity> MapLayers => Set<MapLayerEntity>();

    public DbSet<MapElementEntity> MapElements => Set<MapElementEntity>();

    private const string Fractional = "C";

    private static void ConfigureContent(ModelBuilder b)
    {
        // The search over titles (MCP, later the server's search; docs/03 §15 #37): a trigram index.
        b.HasPostgresExtension("pg_trgm");
        b.Entity<ItemEntity>().HasIndex(i => i.Title).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_items_title_trgm");

        // Configuration (docs/02 §7).
        b.Entity<StatusEntity>(e =>
        {
            e.ToTable("statuses", t =>
            {
                EnumCheck(t, "statuses", "item_type", [ItemType.Idea, ItemType.Lead, ItemType.Project]);
                EnumCheck<StatusCategory>(t, "statuses", "category");
                t.HasCheckConstraint("ck_statuses_name", "char_length(name) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_statuses_max_days", "max_days IS NULL OR max_days > 0");
            });
            e.HasIndex(s => new { s.ItemType, s.Name }).IsUnique().HasFilter("NOT is_archived AND NOT deleted");
            e.HasIndex(s => s.ItemType).IsUnique().HasFilter("is_default AND NOT deleted").HasDatabaseName("ix_statuses_default");
            Synced(e);
        });
        b.Entity<SectionEntity>(e =>
        {
            e.ToTable("sections", t =>
            {
                t.HasCheckConstraint("ck_sections_applies_to",
                    "cardinality(applies_to) > 0 AND applies_to <@ ARRAY['idea', 'project']::text[] AND default_for <@ applies_to");
                t.HasCheckConstraint("ck_sections_name", "char_length(name) BETWEEN 1 AND 100");
            });
            Synced(e);
        });
        b.Entity<SectionGroupEntity>(e =>
        {
            e.ToTable("section_groups", t => EnumCheck<SectionAccess>(t, "section_groups", "access"));
            e.HasKey(g => new { g.SectionId, g.GroupId });
            e.HasOne<SectionEntity>().WithMany().HasForeignKey(g => g.SectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Auth.GroupEntity>().WithMany().HasForeignKey(g => g.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(g => g.GroupId);
        });
        b.Entity<FieldDefinitionEntity>(e =>
        {
            e.ToTable("field_definitions", t =>
            {
                EnumCheck<FieldType>(t, "field_definitions", "type");
                t.HasCheckConstraint("ck_field_definitions_key", "key ~ '^[a-z][a-z0-9_]{1,62}$'");
                t.HasCheckConstraint("ck_field_definitions_score", "type <> 'score' OR default_value IS NULL");
            });
            e.Property(f => f.Config).HasColumnType("jsonb");
            e.Property(f => f.DefaultValue).HasColumnType("jsonb");
            e.HasOne<SectionEntity>().WithMany().HasForeignKey(f => f.SectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => f.Key).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(f => f.SectionId);
            Synced(e);
        });
        b.Entity<ProjectTemplateEntity>(e =>
        {
            e.ToTable("project_templates", t => t.HasCheckConstraint("ck_project_templates_name", "char_length(name) BETWEEN 1 AND 100"));
            e.Property(p => p.DefaultShares).HasColumnType("jsonb");
            Synced(e);
        });
        b.Entity<TemplateStatusEntity>(e =>
        {
            e.ToTable("template_statuses");
            e.HasOne<ProjectTemplateEntity>().WithMany().HasForeignKey(s => s.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StatusEntity>().WithMany().HasForeignKey(s => s.StatusId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.TemplateId, s.StatusId }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(s => s.StatusId);
            Synced(e);
        });
        b.Entity<TemplateSectionEntity>(e =>
        {
            e.ToTable("template_sections");
            e.HasOne<ProjectTemplateEntity>().WithMany().HasForeignKey(s => s.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SectionEntity>().WithMany().HasForeignKey(s => s.SectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.TemplateId, s.SectionId }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(s => s.SectionId);
            Synced(e);
        });
        b.Entity<TemplateFieldDefaultEntity>(e =>
        {
            e.ToTable("template_field_defaults");
            e.Property(d => d.Value).HasColumnType("jsonb");
            e.HasOne<ProjectTemplateEntity>().WithMany().HasForeignKey(d => d.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FieldDefinitionEntity>().WithMany().HasForeignKey(d => d.FieldId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.TemplateId, d.FieldId }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(d => d.FieldId);
            Synced(e);
        });
        b.Entity<TemplateMilestoneEntity>(e =>
        {
            e.ToTable("template_milestones", t => EnumCheck<MilestoneKind>(t, "template_milestones", "kind"));
            e.Property(m => m.Dod).HasColumnType("jsonb");
            e.Property(m => m.SortOrder).UseCollation(Fractional);
            e.HasOne<ProjectTemplateEntity>().WithMany().HasForeignKey(m => m.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.TemplateId);
            Synced(e);
        });
        b.Entity<AssignmentRoleEntity>(e =>
        {
            e.ToTable("assignment_roles");
            Synced(e);
        });
        b.Entity<SavedViewEntity>(e =>
        {
            e.ToTable("saved_views", t => EnumCheck<ViewItemType>(t, "saved_views", "item_type"));
            e.Property(v => v.Definition).HasColumnType("jsonb");
            e.HasIndex(v => v.OwnerUserId);
            Synced(e);
        });
        b.Entity<ConfigKeyEntity>(e =>
        {
            e.ToTable("config_keys");
            e.HasKey(k => new { k.Entity, k.Key });
            e.HasIndex(k => k.Id);
        });

        // Items' children (docs/02 §6, §7.5, §7.6, §8).
        b.Entity<AssignmentEntity>(e =>
        {
            e.ToTable("assignments", t => t.HasCheckConstraint("ck_assignments_parent", "num_nonnulls(item_id, milestone_id) = 1"));
            e.Property(a => a.SortOrder).UseCollation(Fractional);
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(a => a.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MilestoneEntity>().WithMany().HasForeignKey(a => a.MilestoneId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Persons.PersonEntity>().WithMany().HasForeignKey(a => a.PersonId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.ItemId, a.PersonId }).IsUnique().HasFilter("NOT deleted AND item_id IS NOT NULL");
            e.HasIndex(a => new { a.MilestoneId, a.PersonId }).IsUnique().HasFilter("NOT deleted AND milestone_id IS NOT NULL");
            e.HasIndex(a => a.PersonId);
            Synced(e);
        });
        b.Entity<ItemLinkEntity>(e =>
        {
            e.ToTable("item_links", t =>
            {
                EnumCheck<LinkRelation>(t, "item_links", "relation");
                t.HasCheckConstraint("ck_item_links_self", "from_item_id <> to_item_id");
            });
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(l => l.FromItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(l => l.ToItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.FromItemId, l.ToItemId, l.Relation }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(l => l.ToItemId);
            Synced(e);
        });
        b.Entity<ItemSectionEntity>(e =>
        {
            e.ToTable("item_sections");
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(s => s.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SectionEntity>().WithMany().HasForeignKey(s => s.SectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.ItemId, s.SectionId }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(s => s.SectionId);
            Synced(e);
        });
        b.Entity<FieldValueEntity>(e =>
        {
            e.ToTable("field_values");
            e.Property(v => v.Value).HasColumnType("jsonb");
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(v => v.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FieldDefinitionEntity>().WithMany().HasForeignKey(v => v.FieldId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(v => new { v.ItemId, v.FieldId }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(v => v.FieldId);
            Synced(e);
        });
        b.Entity<MilestoneEntity>(e =>
        {
            e.ToTable("milestones", t =>
            {
                EnumCheck<MilestoneKind>(t, "milestones", "kind");
                EnumCheck<MilestoneStatus>(t, "milestones", "status");
                t.HasCheckConstraint("ck_milestones_decision",
                    $"decision IS NULL OR (kind = 'gate' AND decision IN ({string.Join(", ", EnumText.All<GateDecision>().Select(d => $"'{d}'"))}))");
                t.HasCheckConstraint("ck_milestones_title", "char_length(title) BETWEEN 1 AND 300");
            });
            e.Property(m => m.SortOrder).UseCollation(Fractional);
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(m => m.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.ItemId);
            Synced(e);
        });
        b.Entity<DodItemEntity>(e =>
        {
            e.ToTable("dod_items", t =>
            {
                EnumCheck<DodState>(t, "dod_items", "state");
                t.HasCheckConstraint("ck_dod_items_na_note", "state <> 'na' OR char_length(btrim(coalesce(note, ''))) > 0");
            });
            e.Property(d => d.SortOrder).UseCollation(Fractional);
            e.HasOne<MilestoneEntity>().WithMany().HasForeignKey(d => d.MilestoneId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.MilestoneId);
            Synced(e);
        });

        // Files (docs/02 §6, docs/03 §10).
        b.Entity<FileEntity>(e =>
        {
            e.ToTable("files", t =>
            {
                EnumCheck<FilePurpose>(t, "files", "purpose");
                EnumCheck<FileStatus>(t, "files", "status");
                t.HasCheckConstraint("ck_files_owner", "num_nonnulls(item_id, map_id) = 1");
                t.HasCheckConstraint("ck_files_size", "size_bytes > 0");
            });
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(f => f.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MapEntity>().WithMany().HasForeignKey(f => f.MapId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => f.Sha256);
            e.HasIndex(f => f.ItemId);
            e.HasIndex(f => f.MapId);
            e.HasIndex(f => f.StoragePath);
            Synced(e);
        });
        b.Entity<FileUploadEntity>(e =>
        {
            e.ToTable("file_uploads");
            e.HasKey(u => u.FileId);
            e.Property(u => u.FileId).ValueGeneratedNever();
            e.HasOne<FileEntity>().WithMany().HasForeignKey(u => u.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.StartedAt);
        });

        // Maps (docs/02 §9). No foreign key to users: a map outlives its owner's account.
        b.Entity<MapEntity>(e =>
        {
            e.ToTable("maps", t =>
            {
                EnumCheck<MapKind>(t, "maps", "kind");
                t.HasCheckConstraint("ck_maps_name", "char_length(name) BETWEEN 1 AND 200");
                t.HasCheckConstraint("ck_maps_matrix", "kind = 'matrix' OR matrix IS NULL");
            });
            e.Property(m => m.Settings).HasColumnType("jsonb");
            e.Property(m => m.Matrix).HasColumnType("jsonb");
            e.Property(m => m.DefaultView).HasColumnType("jsonb");
            e.HasIndex(m => m.OwnerUserId);
            Synced(e);
        });
        b.Entity<MapShareEntity>(e =>
        {
            e.ToTable("map_shares", t => EnumCheck(t, "map_shares", "access", [ItemAccess.Read, ItemAccess.Edit, ItemAccess.Manage]));
            e.HasOne<MapEntity>().WithMany().HasForeignKey(s => s.MapId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Auth.GroupEntity>().WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.MapId, s.GroupId }).IsUnique().HasFilter("NOT deleted");
            e.HasIndex(s => new { s.GroupId, s.MapId }).HasFilter("NOT deleted");
            Synced(e);
        });
        b.Entity<MapLayerEntity>(e =>
        {
            e.ToTable("map_layers");
            e.Property(l => l.SortOrder).UseCollation(Fractional);
            e.HasOne<MapEntity>().WithMany().HasForeignKey(l => l.MapId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => l.MapId);
            Synced(e);
        });
        b.Entity<MapElementEntity>(e =>
        {
            e.ToTable("map_elements", t =>
            {
                EnumCheck<ElementKind>(t, "map_elements", "kind");
                t.HasCheckConstraint("ck_map_elements_position",
                    "x BETWEEN -1000000 AND 1000000 AND y BETWEEN -1000000 AND 1000000 AND width > 0 AND height > 0");
                t.HasCheckConstraint("ck_map_elements_refs",
                    "(kind = 'item') = (item_id IS NOT NULL) AND (kind = 'milestone') = (milestone_id IS NOT NULL) "
                    + "AND (kind = 'timeline') = (project_id IS NOT NULL) AND (kind = 'image') = (file_id IS NOT NULL)");
            });
            e.Property(m => m.Z).UseCollation(Fractional);
            e.Property(m => m.Points).HasColumnType("jsonb");
            e.Property(m => m.Style).HasColumnType("jsonb");
            e.Property(m => m.Config).HasColumnType("jsonb");
            e.HasOne<MapEntity>().WithMany().HasForeignKey(m => m.MapId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MapLayerEntity>().WithMany().HasForeignKey(m => m.LayerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(m => m.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MilestoneEntity>().WithMany().HasForeignKey(m => m.MilestoneId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ItemEntity>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FileEntity>().WithMany().HasForeignKey(m => m.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.MapId);
            e.HasIndex(m => m.LayerId);
            e.HasIndex(m => m.ItemId);
            e.HasIndex(m => m.MilestoneId);
            e.HasIndex(m => m.ProjectId);
            e.HasIndex(m => m.FileId);
            Synced(e);
        });
    }
}

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PGPM.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase345Server : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "assignment_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "config_keys",
                columns: table => new
                {
                    entity = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    package = table.Column<string>(type: "text", nullable: true),
                    hash = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_config_keys", x => new { x.entity, x.key });
                });

            migrationBuilder.CreateTable(
                name: "item_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation = table.Column<string>(type: "text", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_links", x => x.id);
                    table.CheckConstraint("ck_item_links_relation", "relation IN ('contributes_to', 'learning_of', 'depends_on', 'related')");
                    table.CheckConstraint("ck_item_links_self", "from_item_id <> to_item_id");
                    table.ForeignKey(
                        name: "fk_item_links_items_from_item_id",
                        column: x => x.from_item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_links_items_to_item_id",
                        column: x => x.to_item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "maps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    matrix = table.Column<string>(type: "jsonb", nullable: true),
                    default_view = table.Column<string>(type: "jsonb", nullable: true),
                    is_template = table.Column<bool>(type: "boolean", nullable: false),
                    template_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preview = table.Column<byte[]>(type: "bytea", nullable: true),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maps", x => x.id);
                    table.CheckConstraint("ck_maps_kind", "kind IN ('canvas', 'matrix')");
                    table.CheckConstraint("ck_maps_matrix", "kind = 'matrix' OR matrix IS NULL");
                    table.CheckConstraint("ck_maps_name", "char_length(name) BETWEEN 1 AND 200");
                });

            migrationBuilder.CreateTable(
                name: "milestones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision = table.Column<string>(type: "text", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    on_go_status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    on_no_go_status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_milestones", x => x.id);
                    table.CheckConstraint("ck_milestones_decision", "decision IS NULL OR (kind = 'gate' AND decision IN ('pending', 'go', 'no_go', 'iterate', 'pivot', 'park'))");
                    table.CheckConstraint("ck_milestones_kind", "kind IN ('milestone', 'gate')");
                    table.CheckConstraint("ck_milestones_status", "status IN ('planned', 'in_progress', 'done', 'cancelled')");
                    table.CheckConstraint("ck_milestones_title", "char_length(title) BETWEEN 1 AND 300");
                    table.ForeignKey(
                        name: "fk_milestones_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    icon = table.Column<string>(type: "text", nullable: true),
                    default_status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_shares = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_templates", x => x.id);
                    table.CheckConstraint("ck_project_templates_name", "char_length(name) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "saved_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_views", x => x.id);
                    table.CheckConstraint("ck_saved_views_item_type", "item_type IN ('note', 'idea', 'lead', 'project', 'gate')");
                });

            migrationBuilder.CreateTable(
                name: "sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    applies_to = table.Column<List<string>>(type: "text[]", nullable: false),
                    default_for = table.Column<List<string>>(type: "text[]", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    collapsed_by_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sections", x => x.id);
                    table.CheckConstraint("ck_sections_applies_to", "cardinality(applies_to) > 0 AND applies_to <@ ARRAY['idea', 'project']::text[] AND default_for <@ applies_to");
                    table.CheckConstraint("ck_sections_name", "char_length(name) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "statuses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: false),
                    icon = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    max_days = table.Column<int>(type: "integer", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statuses", x => x.id);
                    table.CheckConstraint("ck_statuses_category", "category IN ('open', 'active', 'closed')");
                    table.CheckConstraint("ck_statuses_item_type", "item_type IN ('idea', 'lead', 'project')");
                    table.CheckConstraint("ck_statuses_max_days", "max_days IS NULL OR max_days > 0");
                    table.CheckConstraint("ck_statuses_name", "char_length(name) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    map_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    thumbnail = table.Column<byte[]>(type: "bytea", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: true),
                    unreferenced_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                    table.CheckConstraint("ck_files_owner", "num_nonnulls(item_id, map_id) = 1");
                    table.CheckConstraint("ck_files_purpose", "purpose IN ('attachment', 'cover', 'map_image', 'embedded')");
                    table.CheckConstraint("ck_files_size", "size_bytes > 0");
                    table.CheckConstraint("ck_files_status", "status IN ('pending', 'available')");
                    table.ForeignKey(
                        name: "fk_files_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_files_maps_map_id",
                        column: x => x.map_id,
                        principalTable: "maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "map_layers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    map_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_layers", x => x.id);
                    table.ForeignKey(
                        name: "fk_map_layers_maps_map_id",
                        column: x => x.map_id,
                        principalTable: "maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "map_shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    map_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access = table.Column<string>(type: "text", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_shares", x => x.id);
                    table.CheckConstraint("ck_map_shares_access", "access IN ('read', 'edit', 'manage')");
                    table.ForeignKey(
                        name: "fk_map_shares_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_map_shares_maps_map_id",
                        column: x => x.map_id,
                        principalTable: "maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignments", x => x.id);
                    table.CheckConstraint("ck_assignments_parent", "num_nonnulls(item_id, milestone_id) = 1");
                    table.ForeignKey(
                        name: "fk_assignments_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assignments_milestones_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assignments_persons_person_id",
                        column: x => x.person_id,
                        principalTable: "persons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dod_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    state_changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    state_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sort_order = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dod_items", x => x.id);
                    table.CheckConstraint("ck_dod_items_na_note", "state <> 'na' OR char_length(btrim(coalesce(note, ''))) > 0");
                    table.CheckConstraint("ck_dod_items_state", "state IN ('open', 'done', 'na')");
                    table.ForeignKey(
                        name: "fk_dod_items_milestones_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_milestones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    offset_days = table.Column<int>(type: "integer", nullable: true),
                    dod = table.Column<string>(type: "jsonb", nullable: false),
                    on_go_status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    on_no_go_status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_milestones", x => x.id);
                    table.CheckConstraint("ck_template_milestones_kind", "kind IN ('milestone', 'gate')");
                    table.ForeignKey(
                        name: "fk_template_milestones_project_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "project_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    help_text = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    default_value = table.Column<string>(type: "jsonb", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_definitions", x => x.id);
                    table.CheckConstraint("ck_field_definitions_key", "key ~ '^[a-z][a-z0-9_]{1,62}$'");
                    table.CheckConstraint("ck_field_definitions_score", "type <> 'score' OR default_value IS NULL");
                    table.CheckConstraint("ck_field_definitions_type", "type IN ('text', 'textarea', 'markdown', 'number', 'date', 'datetime', 'bool', 'select', 'multiselect', 'person', 'url', 'email', 'rating', 'score')");
                    table.ForeignKey(
                        name: "fk_field_definitions_sections_section_id",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_sections_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_sections_sections_section_id",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "section_groups",
                columns: table => new
                {
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_section_groups", x => new { x.section_id, x.group_id });
                    table.CheckConstraint("ck_section_groups_access", "access IN ('view', 'edit')");
                    table.ForeignKey(
                        name: "fk_section_groups_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_section_groups_sections_section_id",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_sections_project_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "project_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_template_sections_sections_section_id",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "template_statuses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_statuses", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_statuses_project_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "project_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_template_statuses_statuses_status_id",
                        column: x => x.status_id,
                        principalTable: "statuses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_uploads",
                columns: table => new
                {
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_bytes = table.Column<long>(type: "bigint", nullable: false),
                    next_chunk = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    temp_path = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_uploads", x => x.file_id);
                    table.ForeignKey(
                        name: "fk_file_uploads_files_file_id",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "map_elements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    map_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    z = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    width = table.Column<double>(type: "double precision", nullable: false),
                    height = table.Column<double>(type: "double precision", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    points = table.Column<string>(type: "jsonb", nullable: true),
                    start_element_id = table.Column<Guid>(type: "uuid", nullable: true),
                    end_element_id = table.Column<Guid>(type: "uuid", nullable: true),
                    style = table.Column<string>(type: "jsonb", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: true),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_elements", x => x.id);
                    table.CheckConstraint("ck_map_elements_kind", "kind IN ('item', 'milestone', 'timeline', 'rect', 'ellipse', 'line', 'text', 'image')");
                    table.CheckConstraint("ck_map_elements_position", "x BETWEEN -1000000 AND 1000000 AND y BETWEEN -1000000 AND 1000000 AND width > 0 AND height > 0");
                    table.CheckConstraint("ck_map_elements_refs", "(kind = 'item') = (item_id IS NOT NULL) AND (kind = 'milestone') = (milestone_id IS NOT NULL) AND (kind = 'timeline') = (project_id IS NOT NULL) AND (kind = 'image') = (file_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_map_elements_files_file_id",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_elements_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_elements_items_project_id",
                        column: x => x.project_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_elements_map_layers_layer_id",
                        column: x => x.layer_id,
                        principalTable: "map_layers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_elements_maps_map_id",
                        column: x => x.map_id,
                        principalTable: "maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_elements_milestones_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_field_values_field_definitions_field_id",
                        column: x => x.field_id,
                        principalTable: "field_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_values_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_field_defaults",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    delete_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_field_defaults", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_field_defaults_field_definitions_field_id",
                        column: x => x.field_id,
                        principalTable: "field_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_template_field_defaults_project_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "project_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_items_title_trgm",
                table: "items",
                column: "title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_roles_delete_batch_id",
                table: "assignment_roles",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assignment_roles_seq",
                table: "assignment_roles",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assignments_delete_batch_id",
                table: "assignments",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_item_id_person_id",
                table: "assignments",
                columns: new[] { "item_id", "person_id" },
                unique: true,
                filter: "NOT deleted AND item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_milestone_id_person_id",
                table: "assignments",
                columns: new[] { "milestone_id", "person_id" },
                unique: true,
                filter: "NOT deleted AND milestone_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_person_id",
                table: "assignments",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_seq",
                table: "assignments",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_config_keys_id",
                table: "config_keys",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_dod_items_delete_batch_id",
                table: "dod_items",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_dod_items_milestone_id",
                table: "dod_items",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_dod_items_seq",
                table: "dod_items",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_definitions_delete_batch_id",
                table: "field_definitions",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_field_definitions_key",
                table: "field_definitions",
                column: "key",
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_field_definitions_section_id",
                table: "field_definitions",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_definitions_seq",
                table: "field_definitions",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_values_delete_batch_id",
                table: "field_values",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_field_values_field_id",
                table: "field_values",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_values_item_id_field_id",
                table: "field_values",
                columns: new[] { "item_id", "field_id" },
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_field_values_seq",
                table: "field_values",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_file_uploads_started_at",
                table: "file_uploads",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_files_delete_batch_id",
                table: "files",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_files_item_id",
                table: "files",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_files_map_id",
                table: "files",
                column: "map_id");

            migrationBuilder.CreateIndex(
                name: "ix_files_seq",
                table: "files",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_files_sha256",
                table: "files",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_files_storage_path",
                table: "files",
                column: "storage_path");

            migrationBuilder.CreateIndex(
                name: "ix_item_links_delete_batch_id",
                table: "item_links",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_item_links_from_item_id_to_item_id_relation",
                table: "item_links",
                columns: new[] { "from_item_id", "to_item_id", "relation" },
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_item_links_seq",
                table: "item_links",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_links_to_item_id",
                table: "item_links",
                column: "to_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_sections_delete_batch_id",
                table: "item_sections",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_item_sections_item_id_section_id",
                table: "item_sections",
                columns: new[] { "item_id", "section_id" },
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_item_sections_section_id",
                table: "item_sections",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_sections_seq",
                table: "item_sections",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_delete_batch_id",
                table: "map_elements",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_file_id",
                table: "map_elements",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_item_id",
                table: "map_elements",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_layer_id",
                table: "map_elements",
                column: "layer_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_map_id",
                table: "map_elements",
                column: "map_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_milestone_id",
                table: "map_elements",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_project_id",
                table: "map_elements",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_elements_seq",
                table: "map_elements",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_map_layers_delete_batch_id",
                table: "map_layers",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_map_layers_map_id",
                table: "map_layers",
                column: "map_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_layers_seq",
                table: "map_layers",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_map_shares_delete_batch_id",
                table: "map_shares",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_map_shares_group_id_map_id",
                table: "map_shares",
                columns: new[] { "group_id", "map_id" },
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_map_shares_map_id_group_id",
                table: "map_shares",
                columns: new[] { "map_id", "group_id" },
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_map_shares_seq",
                table: "map_shares",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_maps_delete_batch_id",
                table: "maps",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_maps_owner_user_id",
                table: "maps",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_maps_seq",
                table: "maps",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_milestones_delete_batch_id",
                table: "milestones",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_milestones_item_id",
                table: "milestones",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_milestones_seq",
                table: "milestones",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_templates_delete_batch_id",
                table: "project_templates",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_project_templates_seq",
                table: "project_templates",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_delete_batch_id",
                table: "saved_views",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_owner_user_id",
                table: "saved_views",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_seq",
                table: "saved_views",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_section_groups_group_id",
                table: "section_groups",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_sections_delete_batch_id",
                table: "sections",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sections_seq",
                table: "sections",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_statuses_default",
                table: "statuses",
                column: "item_type",
                unique: true,
                filter: "is_default AND NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_statuses_delete_batch_id",
                table: "statuses",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_statuses_item_type_name",
                table: "statuses",
                columns: new[] { "item_type", "name" },
                unique: true,
                filter: "NOT is_archived AND NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_statuses_seq",
                table: "statuses",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_field_defaults_delete_batch_id",
                table: "template_field_defaults",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_template_field_defaults_field_id",
                table: "template_field_defaults",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_field_defaults_seq",
                table: "template_field_defaults",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_field_defaults_template_id_field_id",
                table: "template_field_defaults",
                columns: new[] { "template_id", "field_id" },
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_template_milestones_delete_batch_id",
                table: "template_milestones",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_template_milestones_seq",
                table: "template_milestones",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_milestones_template_id",
                table: "template_milestones",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_sections_delete_batch_id",
                table: "template_sections",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_template_sections_section_id",
                table: "template_sections",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_sections_seq",
                table: "template_sections",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_sections_template_id_section_id",
                table: "template_sections",
                columns: new[] { "template_id", "section_id" },
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_template_statuses_delete_batch_id",
                table: "template_statuses",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_template_statuses_seq",
                table: "template_statuses",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_statuses_status_id",
                table: "template_statuses",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_template_statuses_template_id_status_id",
                table: "template_statuses",
                columns: new[] { "template_id", "status_id" },
                unique: true,
                filter: "NOT deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignment_roles");

            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropTable(
                name: "config_keys");

            migrationBuilder.DropTable(
                name: "dod_items");

            migrationBuilder.DropTable(
                name: "field_values");

            migrationBuilder.DropTable(
                name: "file_uploads");

            migrationBuilder.DropTable(
                name: "item_links");

            migrationBuilder.DropTable(
                name: "item_sections");

            migrationBuilder.DropTable(
                name: "map_elements");

            migrationBuilder.DropTable(
                name: "map_shares");

            migrationBuilder.DropTable(
                name: "saved_views");

            migrationBuilder.DropTable(
                name: "section_groups");

            migrationBuilder.DropTable(
                name: "template_field_defaults");

            migrationBuilder.DropTable(
                name: "template_milestones");

            migrationBuilder.DropTable(
                name: "template_sections");

            migrationBuilder.DropTable(
                name: "template_statuses");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "map_layers");

            migrationBuilder.DropTable(
                name: "milestones");

            migrationBuilder.DropTable(
                name: "field_definitions");

            migrationBuilder.DropTable(
                name: "project_templates");

            migrationBuilder.DropTable(
                name: "statuses");

            migrationBuilder.DropTable(
                name: "maps");

            migrationBuilder.DropTable(
                name: "sections");

            migrationBuilder.DropIndex(
                name: "ix_items_title_trgm",
                table: "items");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}

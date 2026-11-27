using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PGPM.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_groups_normalized_name",
                table: "groups");

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                table: "persons",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                table: "persons",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "delete_batch_id",
                table: "persons",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted",
                table: "persons",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "rev",
                table: "persons",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "seq",
                table: "persons",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "persons",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by",
                table: "persons",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                table: "groups",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                table: "groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "delete_batch_id",
                table: "groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted",
                table: "groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "rev",
                table: "groups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "seq",
                table: "groups",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "groups",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by",
                table: "groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "change_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    entity = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    map_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    client_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_log", x => x.id);
                    table.CheckConstraint("ck_change_log_action", "action IN ('create', 'update', 'delete', 'restore', 'share', 'conflict', 'conflict_resolved')");
                    table.CheckConstraint("ck_change_log_source", "source IN ('ui', 'mcp', 'admin', 'directory', 'restore', 'system')");
                });

            migrationBuilder.CreateTable(
                name: "delete_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_kind = table.Column<string>(type: "text", nullable: false),
                    root_table = table.Column<string>(type: "text", nullable: false),
                    root_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    restored_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    restored_by = table.Column<Guid>(type: "uuid", nullable: true),
                    purged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    purged_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delete_batches", x => x.id);
                    table.CheckConstraint("ck_delete_batches_root_kind", "root_kind IN ('item', 'map', 'other')");
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true),
                    image_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    origin_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    converted_to_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pinned = table.Column<bool>(type: "boolean", nullable: false),
                    lead_organisation = table.Column<string>(type: "text", nullable: true),
                    lead_value = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    lead_currency = table.Column<string>(type: "text", nullable: true),
                    lead_probability = table.Column<short>(type: "smallint", nullable: true),
                    lead_decision_date = table.Column<DateOnly>(type: "date", nullable: true),
                    lead_next_action = table.Column<string>(type: "text", nullable: true),
                    lead_next_action_due = table.Column<DateOnly>(type: "date", nullable: true),
                    lead_source = table.Column<string>(type: "text", nullable: true),
                    project_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    project_end_date = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("pk_items", x => x.id);
                    table.CheckConstraint("ck_items_lead_probability", "lead_probability IS NULL OR lead_probability BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_items_status", "type = 'note' OR status_id IS NOT NULL");
                    table.CheckConstraint("ck_items_title", "char_length(title) BETWEEN 1 AND 300");
                    table.CheckConstraint("ck_items_type", "type IN ('note', 'idea', 'lead', 'project')");
                });

            migrationBuilder.CreateTable(
                name: "number_counters",
                columns: table => new
                {
                    type = table.Column<string>(type: "text", nullable: false),
                    next_value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_number_counters", x => x.type);
                });

            migrationBuilder.CreateTable(
                name: "sync_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    app_version = table.Column<string>(type: "text", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seq = table.Column<long>(type: "bigint", nullable: false),
                    pending_count = table.Column<int>(type: "integer", nullable: false),
                    conflict_count = table.Column<int>(type: "integer", nullable: false),
                    live_connected = table.Column<bool>(type: "boolean", nullable: false),
                    horizon_seq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_clients", x => x.id);
                    table.ForeignKey(
                        name: "fk_sync_clients_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_counter",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_counter", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_evictions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    root_kind = table.Column<string>(type: "text", nullable: false),
                    root_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_evictions", x => x.id);
                    table.CheckConstraint("ck_sync_evictions_root_kind", "root_kind IN ('item', 'map')");
                });

            migrationBuilder.CreateTable(
                name: "sync_mutations",
                columns: table => new
                {
                    mutation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    table_name = table.Column<string>(type: "text", nullable: false),
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    op = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_mutations", x => x.mutation_id);
                });

            migrationBuilder.CreateTable(
                name: "sync_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    tombstone_horizon_seq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_user_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_item_shares", x => x.id);
                    table.CheckConstraint("ck_item_shares_access", "access IN ('read', 'edit', 'manage')");
                    table.ForeignKey(
                        name: "fk_item_shares_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_shares_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The counter's and the state's single rows; the groups and persons there are (the seeds, phase 1's users) get
            // their synced-row columns: rev 1, now, and seqs from the counter – before seq becomes unique.
            migrationBuilder.Sql("""
                INSERT INTO sync_counter (id, value) VALUES (1, 0);
                INSERT INTO sync_state (id, tombstone_horizon_seq) VALUES (1, 0);
                UPDATE groups SET rev = 1, created_at = now(), updated_at = now();
                UPDATE persons SET rev = 1, created_at = now(), updated_at = now();
                WITH numbered AS (SELECT id, row_number() OVER (ORDER BY id) AS n FROM groups)
                UPDATE groups g SET seq = numbered.n FROM numbered WHERE g.id = numbered.id;
                WITH numbered AS (SELECT id, row_number() OVER (ORDER BY id) + (SELECT count(*) FROM groups) AS n FROM persons)
                UPDATE persons p SET seq = numbered.n FROM numbered WHERE p.id = numbered.id;
                UPDATE sync_counter SET value = (SELECT count(*) FROM groups) + (SELECT count(*) FROM persons) WHERE id = 1;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_persons_delete_batch_id",
                table: "persons",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_persons_seq",
                table: "persons",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_groups_delete_batch_id",
                table: "groups",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_groups_normalized_name",
                table: "groups",
                column: "normalized_name",
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_groups_seq",
                table: "groups",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_log_entity_entity_id",
                table: "change_log",
                columns: new[] { "entity", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_log_item_id_at",
                table: "change_log",
                columns: new[] { "item_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_change_log_map_id_at",
                table: "change_log",
                columns: new[] { "map_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_delete_batches_root_id",
                table: "delete_batches",
                column: "root_id");

            migrationBuilder.CreateIndex(
                name: "ix_delete_batches_root_kind_deleted_at",
                table: "delete_batches",
                columns: new[] { "root_kind", "deleted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_item_shares_delete_batch_id",
                table: "item_shares",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_item_shares_group_id_item_id",
                table: "item_shares",
                columns: new[] { "group_id", "item_id" },
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_item_shares_item_id_group_id",
                table: "item_shares",
                columns: new[] { "item_id", "group_id" },
                unique: true,
                filter: "NOT deleted");

            migrationBuilder.CreateIndex(
                name: "ix_item_shares_seq",
                table: "item_shares",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_delete_batch_id",
                table: "items",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_items_owner_user_id",
                table: "items",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_seq",
                table: "items",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_status_id",
                table: "items",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_tags",
                table: "items",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_items_type_deleted",
                table: "items",
                columns: new[] { "type", "deleted" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_clients_user_id",
                table: "sync_clients",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sync_evictions_at",
                table: "sync_evictions",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_sync_evictions_user_id_seq",
                table: "sync_evictions",
                columns: new[] { "user_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_mutations_processed_at",
                table: "sync_mutations",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_sync_mutations_user_id_batch_id",
                table: "sync_mutations",
                columns: new[] { "user_id", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_delete_batch_id",
                table: "user_settings",
                column: "delete_batch_id",
                filter: "delete_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_seq",
                table: "user_settings",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_user_id_key",
                table: "user_settings",
                columns: new[] { "user_id", "key" },
                unique: true,
                filter: "NOT deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_log");

            migrationBuilder.DropTable(
                name: "delete_batches");

            migrationBuilder.DropTable(
                name: "item_shares");

            migrationBuilder.DropTable(
                name: "number_counters");

            migrationBuilder.DropTable(
                name: "sync_clients");

            migrationBuilder.DropTable(
                name: "sync_counter");

            migrationBuilder.DropTable(
                name: "sync_evictions");

            migrationBuilder.DropTable(
                name: "sync_mutations");

            migrationBuilder.DropTable(
                name: "sync_state");

            migrationBuilder.DropTable(
                name: "user_settings");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropIndex(
                name: "ix_persons_delete_batch_id",
                table: "persons");

            migrationBuilder.DropIndex(
                name: "ix_persons_seq",
                table: "persons");

            migrationBuilder.DropIndex(
                name: "ix_groups_delete_batch_id",
                table: "groups");

            migrationBuilder.DropIndex(
                name: "ix_groups_normalized_name",
                table: "groups");

            migrationBuilder.DropIndex(
                name: "ix_groups_seq",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "delete_batch_id",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "deleted",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "rev",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "seq",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "persons");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "delete_batch_id",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "deleted",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "rev",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "seq",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "updated_by",
                table: "groups");

            migrationBuilder.CreateIndex(
                name: "ix_groups_normalized_name",
                table: "groups",
                column: "normalized_name",
                unique: true);
        }
    }
}

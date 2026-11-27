using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PGPM.Data.Migrations
{
    /// <inheritdoc />
    public partial class Identity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    @event = table.Column<string>(name: "event", type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_ref = table.Column<string>(type: "text", nullable: true),
                    subject_type = table.Column<string>(type: "text", nullable: true),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: false),
                    ip = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                    table.CheckConstraint("ck_audit_log_actor", "actor IN ('user', 'mcp', 'system', 'entra')");
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    entra_app_role = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    normalized_username = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    temporary_password_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: false),
                    totp_secret_protected = table.Column<string>(type: "text", nullable: true),
                    is_totp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    totp_last_used_step = table.Column<long>(type: "bigint", nullable: true),
                    pending_totp_secret_protected = table.Column<string>(type: "text", nullable: true),
                    pending_totp_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    password_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    microsoft_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    microsoft_object_id = table.Column<Guid>(type: "uuid", nullable: true),
                    microsoft_user_name = table.Column<string>(type: "text", nullable: true),
                    directory_read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    access_epoch = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "group_rights",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_rights", x => new { x.group_id, x.area });
                    table.CheckConstraint("ck_group_rights_area", "area IN ('notes', 'ideas', 'leads', 'projects', 'maps', 'persons', 'configuration', 'directory', 'mcp', 'auditlog', 'system')");
                    table.CheckConstraint("ck_group_rights_level", "level IN ('create', 'edit', 'use', 'view', 'manage')");
                    table.ForeignKey(
                        name: "fk_group_rights_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "persons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    entra_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entra_object_id = table.Column<Guid>(type: "uuid", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    given_name = table.Column<string>(type: "text", nullable: true),
                    surname = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    user_principal_name = table.Column<string>(type: "text", nullable: true),
                    user_type = table.Column<string>(type: "text", nullable: true),
                    job_title = table.Column<string>(type: "text", nullable: true),
                    department = table.Column<string>(type: "text", nullable: true),
                    company_name = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    mobile_phone = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    directory_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_persons", x => x.id);
                    table.CheckConstraint("ck_persons_source", "source IN ('microsoft', 'manual')");
                    table.CheckConstraint("ck_persons_user_type", "user_type IS NULL OR user_type IN ('Member', 'Guest')");
                    table.ForeignKey(
                        name: "fk_persons_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_groups",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_groups", x => new { x.user_id, x.group_id });
                    table.ForeignKey(
                        name: "fk_user_groups_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_groups_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_user_id_at",
                table: "audit_log",
                columns: new[] { "actor_user_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_event",
                table: "audit_log",
                column: "event");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_subject_id_at",
                table: "audit_log",
                columns: new[] { "subject_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_groups_entra_app_role",
                table: "groups",
                column: "entra_app_role",
                unique: true,
                filter: "entra_app_role IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_groups_normalized_name",
                table: "groups",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_persons_email",
                table: "persons",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_persons_entra_tenant_id_entra_object_id",
                table: "persons",
                columns: new[] { "entra_tenant_id", "entra_object_id" },
                unique: true,
                filter: "entra_object_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_persons_user_id",
                table: "persons",
                column: "user_id",
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_groups_group_id",
                table: "user_groups",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_microsoft_tenant_id_microsoft_object_id",
                table: "users",
                columns: new[] { "microsoft_tenant_id", "microsoft_object_id" },
                unique: true,
                filter: "microsoft_object_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_username",
                table: "users",
                column: "normalized_username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "group_rights");

            migrationBuilder.DropTable(
                name: "persons");

            migrationBuilder.DropTable(
                name: "user_groups");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}

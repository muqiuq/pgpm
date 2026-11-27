using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PGPM.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase6DirectoryMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "directory_sync_state",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delta_link = table.Column<string>(type: "text", nullable: true),
                    last_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_status = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    last_mode = table.Column<string>(type: "text", nullable: true),
                    created_count = table.Column<int>(type: "integer", nullable: false),
                    updated_count = table.Column<int>(type: "integer", nullable: false),
                    deactivated_count = table.Column<int>(type: "integer", nullable: false),
                    seen_count = table.Column<int>(type: "integer", nullable: false),
                    linked_count = table.Column<int>(type: "integer", nullable: false),
                    last_full_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_succeeded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_sync_state", x => x.tenant_id);
                    table.CheckConstraint("ck_directory_sync_state_last_mode", "last_mode IS NULL OR last_mode IN ('full', 'delta')");
                    table.CheckConstraint("ck_directory_sync_state_last_status", "last_status IS NULL OR last_status IN ('running', 'ok', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "mcp_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    username = table.Column<string>(type: "text", nullable: false),
                    ip = table.Column<string>(type: "text", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    client = table.Column<string>(type: "text", nullable: true),
                    method = table.Column<string>(type: "text", nullable: false),
                    tool = table.Column<string>(type: "text", nullable: true),
                    tool_kind = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: false),
                    arguments = table.Column<string>(type: "jsonb", nullable: true),
                    result = table.Column<byte[]>(type: "bytea", nullable: true),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    strike = table.Column<bool>(type: "boolean", nullable: false),
                    targets = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_log", x => x.id);
                    table.CheckConstraint("ck_mcp_log_kind", "kind IS NULL OR kind IN ('read', 'write')");
                    table.CheckConstraint("ck_mcp_log_outcome", "outcome IN ('ok', 'error', 'denied', 'rate_limited', 'unauthenticated')");
                });

            migrationBuilder.CreateTable(
                name: "mcp_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: true),
                    hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    on_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    on_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    on_stamp = table.Column<string>(type: "text", nullable: true),
                    delete_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delete_count = table.Column<int>(type: "integer", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_from = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_client = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    strikes = table.Column<List<DateTime>>(type: "timestamp with time zone[]", nullable: false),
                    blocked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_why = table.Column<string>(type: "text", nullable: true),
                    ended_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_tokens", x => x.id);
                    table.CheckConstraint("ck_mcp_tokens_ended_why", "ended_why IS NULL OR ended_why IN ('off', 'expired', 'regenerated', 'stamp', 'strikes', 'admin', 'user_inactive', 'totp_off')");
                    table.CheckConstraint("ck_mcp_tokens_kind", "kind IN ('read', 'write')");
                    table.ForeignKey(
                        name: "fk_mcp_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_log_at",
                table: "mcp_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_log_token_id_id",
                table: "mcp_log",
                columns: new[] { "token_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_log_user_id_id",
                table: "mcp_log",
                columns: new[] { "user_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_tokens_hash",
                table: "mcp_tokens",
                column: "hash",
                unique: true,
                filter: "hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_tokens_user_id_kind",
                table: "mcp_tokens",
                columns: new[] { "user_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "directory_sync_state");

            migrationBuilder.DropTable(
                name: "mcp_log");

            migrationBuilder.DropTable(
                name: "mcp_tokens");
        }
    }
}

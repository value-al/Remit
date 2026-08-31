using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Remit.Reconciliation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reconciliation");

            migrationBuilder.CreateTable(
                name: "exceptions",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exceptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "reconciliation",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "movements",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    first_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_movements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "statements",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lines = table.Column<int>(type: "integer", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    exceptions = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statements", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exceptions_kind_provider_reference",
                schema: "reconciliation",
                table: "exceptions",
                columns: new[] { "kind", "provider", "reference" },
                unique: true,
                filter: "resolved_at IS NULL AND reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_exceptions_provider_resolved_at",
                schema: "reconciliation",
                table: "exceptions",
                columns: new[] { "provider", "resolved_at" });

            migrationBuilder.CreateIndex(
                name: "IX_movements_provider_reference",
                schema: "reconciliation",
                table: "movements",
                columns: new[] { "provider", "reference" });

            migrationBuilder.CreateIndex(
                name: "IX_movements_status_first_event_at",
                schema: "reconciliation",
                table: "movements",
                columns: new[] { "status", "first_event_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exceptions",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "movements",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "statements",
                schema: "reconciliation");
        }
    }
}

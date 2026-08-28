using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Remit.Funding.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DepositProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provider",
                schema: "funding",
                table: "deposits",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "provider",
                schema: "funding",
                table: "deposits");
        }
    }
}

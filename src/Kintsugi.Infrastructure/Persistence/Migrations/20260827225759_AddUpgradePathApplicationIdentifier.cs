using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUpgradePathApplicationIdentifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicationIdentifier",
                schema: "patching",
                table: "upgrade_paths",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_upgrade_paths_ApplicationIdentifier",
                schema: "patching",
                table: "upgrade_paths",
                column: "ApplicationIdentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_upgrade_paths_ApplicationIdentifier",
                schema: "patching",
                table: "upgrade_paths");

            migrationBuilder.DropColumn(
                name: "ApplicationIdentifier",
                schema: "patching",
                table: "upgrade_paths");
        }
    }
}

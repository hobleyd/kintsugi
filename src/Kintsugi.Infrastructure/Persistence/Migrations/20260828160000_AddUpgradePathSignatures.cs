using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUpgradePathSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScriptSignature",
                schema: "patching",
                table: "upgrade_paths",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandSignature",
                schema: "patching",
                table: "upgrade_paths",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScriptSignature",
                schema: "patching",
                table: "upgrade_paths");

            migrationBuilder.DropColumn(
                name: "CommandSignature",
                schema: "patching",
                table: "upgrade_paths");
        }
    }
}

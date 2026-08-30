using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostOperatingSystemUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OperatingSystemUpdateAvailable",
                schema: "patching",
                table: "hosts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatingSystemLatestVersion",
                schema: "patching",
                table: "hosts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperatingSystemUpdateAvailable",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "OperatingSystemLatestVersion",
                schema: "patching",
                table: "hosts");
        }
    }
}

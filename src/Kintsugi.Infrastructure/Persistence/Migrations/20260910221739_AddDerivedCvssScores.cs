using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDerivedCvssScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CvssDerivedFromVector",
                schema: "patching",
                table: "vulnerabilities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CvssVector",
                schema: "patching",
                table: "osv_advisories",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CvssDerivedFromVector",
                schema: "patching",
                table: "vulnerabilities");

            migrationBuilder.DropColumn(
                name: "CvssVector",
                schema: "patching",
                table: "osv_advisories");
        }
    }
}

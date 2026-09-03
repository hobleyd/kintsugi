using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVantaSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vanta_settings",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ClientSecret = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ApiBaseUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    VulnerableComponentResourceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PackageVulnerabilityResourceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ConsoleBaseUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Severity = table.Column<double>(type: "double precision", nullable: false),
                    SyncIntervalHours = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vanta_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vanta_settings",
                schema: "patching");
        }
    }
}

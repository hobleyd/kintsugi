using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLinuxPackageAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installed_packages",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installed_packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_installed_packages_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "osv_advisories",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OsvId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CveIds = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_osv_advisories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "package_assessments",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ecosystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastAssessedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MatchCount = table.Column<int>(type: "integer", nullable: false),
                    KnownExploitedCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_assessments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "package_vulnerability_matches",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageAssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VulnerabilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_vulnerability_matches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_package_vulnerability_matches_package_assessments_PackageAs~",
                        column: x => x.PackageAssessmentId,
                        principalSchema: "patching",
                        principalTable: "package_assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_package_vulnerability_matches_vulnerabilities_Vulnerability~",
                        column: x => x.VulnerabilityId,
                        principalSchema: "patching",
                        principalTable: "vulnerabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_installed_packages_HostId",
                schema: "patching",
                table: "installed_packages",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_installed_packages_Name_Version",
                schema: "patching",
                table: "installed_packages",
                columns: new[] { "Name", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_osv_advisories_OsvId",
                schema: "patching",
                table: "osv_advisories",
                column: "OsvId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_package_assessments_Ecosystem_Name_Version",
                schema: "patching",
                table: "package_assessments",
                columns: new[] { "Ecosystem", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_package_assessments_LastAssessedUtc",
                schema: "patching",
                table: "package_assessments",
                column: "LastAssessedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_package_vulnerability_matches_PackageAssessmentId_Vulnerabi~",
                schema: "patching",
                table: "package_vulnerability_matches",
                columns: new[] { "PackageAssessmentId", "VulnerabilityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_package_vulnerability_matches_VulnerabilityId",
                schema: "patching",
                table: "package_vulnerability_matches",
                column: "VulnerabilityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installed_packages",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "osv_advisories",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "package_vulnerability_matches",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "package_assessments",
                schema: "patching");
        }
    }
}

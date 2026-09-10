using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPatchFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patch_failures",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InstalledVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttemptedVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    FirstFailedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastFailedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    Resolution = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ResolvedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patch_failures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_patch_failures_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_patch_failures_HostId",
                schema: "patching",
                table: "patch_failures",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_patch_failures_HostId_ApplicationName",
                schema: "patching",
                table: "patch_failures",
                columns: new[] { "HostId", "ApplicationName" });

            migrationBuilder.CreateIndex(
                name: "IX_patch_failures_Resolution_LastFailedUtc",
                schema: "patching",
                table: "patch_failures",
                columns: new[] { "Resolution", "LastFailedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "patch_failures",
                schema: "patching");
        }
    }
}

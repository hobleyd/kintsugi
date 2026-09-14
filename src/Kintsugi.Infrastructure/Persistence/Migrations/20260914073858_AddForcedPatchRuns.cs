using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForcedPatchRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "forced_patch_runs",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CollectedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forced_patch_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_forced_patch_runs_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_forced_patch_runs_HostId_ApplicationName_Platform",
                schema: "patching",
                table: "forced_patch_runs",
                columns: new[] { "HostId", "ApplicationName", "Platform" });

            migrationBuilder.CreateIndex(
                name: "IX_forced_patch_runs_HostId_CollectedUtc_ExpiresUtc",
                schema: "patching",
                table: "forced_patch_runs",
                columns: new[] { "HostId", "CollectedUtc", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forced_patch_runs",
                schema: "patching");
        }
    }
}

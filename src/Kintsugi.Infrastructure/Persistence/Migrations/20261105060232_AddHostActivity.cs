using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "host_activity_settings",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RetentionMonths = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_activity_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "host_events",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ToVersion = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Detail = table.Column<string>(type: "character varying(4064)", maxLength: 4064, nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_host_events_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_host_events_HostId_OccurredUtc",
                schema: "patching",
                table: "host_events",
                columns: new[] { "HostId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_host_events_OccurredUtc",
                schema: "patching",
                table: "host_events",
                column: "OccurredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_activity_settings",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "host_events",
                schema: "patching");
        }
    }
}

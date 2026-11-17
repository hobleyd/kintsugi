using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "baselines",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AssignedToAllWindowsHosts = table.Column<bool>(type: "boolean", nullable: false),
                    AssignedHostIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "host_facts",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LastCheckedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastChangedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_facts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_host_facts_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "baseline_versions",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Document = table.Column<string>(type: "text", nullable: false),
                    DocumentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Signature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SignedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SignedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_versions_baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalSchema: "patching",
                        principalTable: "baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "host_baseline_item_states",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ObservedValue = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastCheckedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastChangedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_baseline_item_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_host_baseline_item_states_baseline_versions_BaselineVersion~",
                        column: x => x.BaselineVersionId,
                        principalSchema: "patching",
                        principalTable: "baseline_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_host_baseline_item_states_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_versions_BaselineId_Number",
                schema: "patching",
                table: "baseline_versions",
                columns: new[] { "BaselineId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baselines_Name",
                schema: "patching",
                table: "baselines",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_host_baseline_item_states_BaselineVersionId",
                schema: "patching",
                table: "host_baseline_item_states",
                column: "BaselineVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_host_baseline_item_states_HostId_BaselineVersionId_ItemKey",
                schema: "patching",
                table: "host_baseline_item_states",
                columns: new[] { "HostId", "BaselineVersionId", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_host_facts_HostId_Key",
                schema: "patching",
                table: "host_facts",
                columns: new[] { "HostId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_baseline_item_states",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "host_facts",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "baseline_versions",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "baselines",
                schema: "patching");
        }
    }
}

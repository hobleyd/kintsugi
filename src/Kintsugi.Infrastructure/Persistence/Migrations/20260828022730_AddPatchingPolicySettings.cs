using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPatchingPolicySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patching_policy_settings",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntervalValue = table.Column<int>(type: "integer", nullable: false),
                    IntervalUnit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DelayValue = table.Column<int>(type: "integer", nullable: false),
                    DelayUnit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MaxDelayCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patching_policy_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "patching_policy_settings",
                schema: "patching");
        }
    }
}

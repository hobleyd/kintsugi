using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "managed_applications",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Owner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "managed_owners",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_owners", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_managed_applications_NormalizedName",
                schema: "patching",
                table: "managed_applications",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_owners_NormalizedName",
                schema: "patching",
                table: "managed_owners",
                column: "NormalizedName",
                unique: true);

            // The tools an administrator can name as patching an application, to begin with. They can
            // add to the list and take from it on the Managed Elsewhere page; these are only what a
            // fresh install starts with, so the first application is not marked before there is
            // anybody to say patches it. Plain SQL so a migration does not depend on the entity.
            migrationBuilder.Sql(@"
                INSERT INTO patching.managed_owners (""Id"", ""Name"", ""NormalizedName"", ""CreatedAtUtc"")
                VALUES
                    (gen_random_uuid(), 'CrowdStrike', 'crowdstrike', now()),
                    (gen_random_uuid(), 'Jamf Pro', 'jamf pro', now()),
                    (gen_random_uuid(), 'NinjaOne', 'ninjaone', now()),
                    (gen_random_uuid(), 'Windows Update', 'windows update', now());");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_applications",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "managed_owners",
                schema: "patching");
        }
    }
}

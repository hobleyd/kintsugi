using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovedScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approved_scripts",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlatformBucket = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Script = table.Column<string>(type: "text", nullable: false),
                    ApplicationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ApplicationIdentifier = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SignerFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SignerPublicKeyPem = table.Column<string>(type: "text", nullable: false),
                    Signature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SignedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImportedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approved_scripts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approved_scripts_ApplicationName_PlatformBucket",
                schema: "patching",
                table: "approved_scripts",
                columns: new[] { "ApplicationName", "PlatformBucket" });

            migrationBuilder.CreateIndex(
                name: "IX_approved_scripts_Sha256_SignerFingerprint",
                schema: "patching",
                table: "approved_scripts",
                columns: new[] { "Sha256", "SignerFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approved_scripts",
                schema: "patching");
        }
    }
}

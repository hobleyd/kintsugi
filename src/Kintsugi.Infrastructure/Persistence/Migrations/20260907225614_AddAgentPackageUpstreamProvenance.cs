using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentPackageUpstreamProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpstreamDownloadUrl",
                schema: "patching",
                table: "agent_packages",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpstreamSha256",
                schema: "patching",
                table: "agent_packages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpstreamDownloadUrl",
                schema: "patching",
                table: "agent_packages");

            migrationBuilder.DropColumn(
                name: "UpstreamSha256",
                schema: "patching",
                table: "agent_packages");
        }
    }
}

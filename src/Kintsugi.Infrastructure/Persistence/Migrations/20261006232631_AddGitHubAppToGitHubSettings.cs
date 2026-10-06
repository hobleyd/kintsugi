using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubAppToGitHubSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GitHubAppId",
                schema: "patching",
                table: "github_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GitHubAppInstallationId",
                schema: "patching",
                table: "github_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitHubAppOwner",
                schema: "patching",
                table: "github_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitHubAppPrivateKey",
                schema: "patching",
                table: "github_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitHubAppSlug",
                schema: "patching",
                table: "github_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitHubAppId",
                schema: "patching",
                table: "github_settings");

            migrationBuilder.DropColumn(
                name: "GitHubAppInstallationId",
                schema: "patching",
                table: "github_settings");

            migrationBuilder.DropColumn(
                name: "GitHubAppOwner",
                schema: "patching",
                table: "github_settings");

            migrationBuilder.DropColumn(
                name: "GitHubAppPrivateKey",
                schema: "patching",
                table: "github_settings");

            migrationBuilder.DropColumn(
                name: "GitHubAppSlug",
                schema: "patching",
                table: "github_settings");
        }
    }
}

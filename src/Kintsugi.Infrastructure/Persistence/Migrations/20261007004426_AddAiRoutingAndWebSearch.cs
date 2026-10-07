using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiRoutingAndWebSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebSearchApiKey",
                schema: "patching",
                table: "ai_agent_settings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebSearchBackend",
                schema: "patching",
                table: "ai_agent_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "WebSearchBaseUrl",
                schema: "patching",
                table: "ai_agent_settings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_catalog_cache",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    FetchedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_catalog_cache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_connections",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CatalogProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AuthMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApiKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    GoogleCloudProject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GoogleCloudLocation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UseHostedWebSearch = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_feature_routes",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Feature = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_feature_routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_feature_routes_ai_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "patching",
                        principalTable: "ai_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_feature_routes_ConnectionId",
                schema: "patching",
                table: "ai_feature_routes",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_feature_routes_Feature",
                schema: "patching",
                table: "ai_feature_routes",
                column: "Feature",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_catalog_cache",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "ai_feature_routes",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "ai_connections",
                schema: "patching");

            migrationBuilder.DropColumn(
                name: "WebSearchApiKey",
                schema: "patching",
                table: "ai_agent_settings");

            migrationBuilder.DropColumn(
                name: "WebSearchBackend",
                schema: "patching",
                table: "ai_agent_settings");

            migrationBuilder.DropColumn(
                name: "WebSearchBaseUrl",
                schema: "patching",
                table: "ai_agent_settings");
        }
    }
}

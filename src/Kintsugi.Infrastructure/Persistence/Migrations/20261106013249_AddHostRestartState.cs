using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostRestartState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RestartRequiredBecause",
                schema: "patching",
                table: "hosts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RestartRequiredSinceUtc",
                schema: "patching",
                table: "hosts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestartRequiredBecause",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "RestartRequiredSinceUtc",
                schema: "patching",
                table: "hosts");
        }
    }
}

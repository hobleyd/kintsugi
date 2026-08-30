using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationParentChild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentApplicationId",
                schema: "patching",
                table: "installed_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_installed_applications_ParentApplicationId",
                schema: "patching",
                table: "installed_applications",
                column: "ParentApplicationId");

            migrationBuilder.AddForeignKey(
                name: "FK_installed_applications_installed_applications_ParentApplica~",
                schema: "patching",
                table: "installed_applications",
                column: "ParentApplicationId",
                principalSchema: "patching",
                principalTable: "installed_applications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_installed_applications_installed_applications_ParentApplica~",
                schema: "patching",
                table: "installed_applications");

            migrationBuilder.DropIndex(
                name: "IX_installed_applications_ParentApplicationId",
                schema: "patching",
                table: "installed_applications");

            migrationBuilder.DropColumn(
                name: "ParentApplicationId",
                schema: "patching",
                table: "installed_applications");
        }
    }
}

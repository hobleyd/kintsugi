using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteControlSessionKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Screen", not the empty string EF scaffolds for a non-nullable string. Kind is stored
            // as the enum's *name*, so an empty default would give every session recorded before
            // shell sessions existed a value that parses as nothing — and the failure would not be
            // this migration, it would be a read of the audit trail months later. Every existing
            // row is a screen session, because there was no other kind.
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "patching",
                table: "remote_control_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Screen");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "patching",
                table: "remote_control_sessions");
        }
    }
}

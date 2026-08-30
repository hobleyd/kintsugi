using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Data-only: moves every package-manager-managed upgrade path out of the old shared "generic"
    /// platform bucket and into its manager's own bucket (see <c>PlatformBucket.ForPackageManager</c>).
    /// </summary>
    /// <remarks>
    /// Homebrew was the only package manager that existed when "generic" was written, so every row
    /// under it is a Homebrew row and this is a straight rename — done in place rather than by
    /// deleting and letting the next scan re-create them, specifically so each row keeps its
    /// <c>ScriptSignature</c>: those represent a human's "Sign Script" review, and re-creating the
    /// rows would silently discard that and leave the whole fleet unable to patch anything
    /// Homebrew-managed until someone signed again.
    ///
    /// The unique index is on (ApplicationName, Platform), and this maps one distinct Platform value
    /// onto another, so it cannot introduce a collision.
    /// </remarks>
    public partial class SplitPackageManagerPlatformBucket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE patching.upgrade_paths
                SET "Platform" = 'pm:Homebrew'
                WHERE "Platform" = 'generic';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE patching.upgrade_paths
                SET "Platform" = 'generic'
                WHERE "Platform" = 'pm:Homebrew';
                """);
        }
    }
}

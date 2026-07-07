using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddRumSiteAppId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RumSites.AppId (from the RUM deployment-scoping work) reached some
            // environments out-of-band, before this migration existed — its model
            // change never had a migration committed. Guard the operations so the
            // migration is idempotent across every database state (column already
            // present, or not).
            migrationBuilder.Sql(
                "IF COL_LENGTH('RumSites', 'AppId') IS NULL " +
                "ALTER TABLE [RumSites] ADD [AppId] uniqueidentifier NULL;");
            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RumSites_AppId' " +
                "AND object_id = OBJECT_ID('[RumSites]')) " +
                "CREATE INDEX [IX_RumSites_AppId] ON [RumSites] ([AppId]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RumSites_AppId' " +
                "AND object_id = OBJECT_ID('[RumSites]')) DROP INDEX [IX_RumSites_AppId] ON [RumSites];");
            migrationBuilder.Sql(
                "IF COL_LENGTH('RumSites', 'AppId') IS NOT NULL " +
                "ALTER TABLE [RumSites] DROP COLUMN [AppId];");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRumSiteAppId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RumSites.AppId (from the RUM deployment-scoping work) reached some
            // environments out-of-band, before this migration existed — its model
            // change never had a migration committed. Guard the operations with
            // IF [NOT] EXISTS so the migration is idempotent across every database
            // state (column already present, or not).
            migrationBuilder.Sql(
                @"ALTER TABLE ""RumSites"" ADD COLUMN IF NOT EXISTS ""AppId"" uuid;");
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_RumSites_AppId"" ON ""RumSites"" (""AppId"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_RumSites_AppId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""RumSites"" DROP COLUMN IF EXISTS ""AppId"";");
        }
    }
}

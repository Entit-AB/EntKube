using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class FixCnpgMaxBackupsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill rows that got MaxBackups=0 from the initial migration which
            // was generated with defaultValue:0 instead of defaultValue:20.
            migrationBuilder.Sql(
                "UPDATE \"CnpgClusters\" SET \"MaxBackups\" = 20 WHERE \"MaxBackups\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data migrations cannot be cleanly reversed.
        }
    }
}

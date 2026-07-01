using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class RenameKyvernoAppIdToTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op on SQLite. The AddKyvernoPolicies migration creates the table with the
            // final TenantId column (plus its Tenants FK and index) directly, so on a fresh
            // SQLite database there is no AppId column to rename — the original strongly-typed
            // rename here failed with "no such column: AppId" and aborted startup. SQLite
            // cannot express a conditional column rename in migration SQL, and no released
            // build ever created the AppId column on SQLite, so skipping is safe. Databases
            // that already applied this migration keep it recorded in history and are unaffected.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KyvernoPolicies_Tenants_TenantId",
                table: "KyvernoPolicies");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                table: "KyvernoPolicies",
                newName: "AppId");

            migrationBuilder.RenameIndex(
                name: "IX_KyvernoPolicies_TenantId_EnvironmentId_PolicyType",
                table: "KyvernoPolicies",
                newName: "IX_KyvernoPolicies_AppId_EnvironmentId_PolicyType");

            migrationBuilder.AddForeignKey(
                name: "FK_KyvernoPolicies_Apps_AppId",
                table: "KyvernoPolicies",
                column: "AppId",
                principalTable: "Apps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

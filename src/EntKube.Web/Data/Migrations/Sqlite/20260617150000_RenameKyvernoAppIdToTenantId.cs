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
            migrationBuilder.DropForeignKey(
                name: "FK_KyvernoPolicies_Apps_AppId",
                table: "KyvernoPolicies");

            migrationBuilder.RenameColumn(
                name: "AppId",
                table: "KyvernoPolicies",
                newName: "TenantId");

            migrationBuilder.RenameIndex(
                name: "IX_KyvernoPolicies_AppId_EnvironmentId_PolicyType",
                table: "KyvernoPolicies",
                newName: "IX_KyvernoPolicies_TenantId_EnvironmentId_PolicyType");

            migrationBuilder.AddForeignKey(
                name: "FK_KyvernoPolicies_Tenants_TenantId",
                table: "KyvernoPolicies",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
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

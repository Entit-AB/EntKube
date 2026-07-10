using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class PerTenantNotificationProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationProviderConfigs_ProviderType",
                table: "NotificationProviderConfigs");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "NotificationProviderConfigs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Notification provider configs used to be global. Now they belong to a tenant.
            // If the deployment has exactly one tenant, hand the existing rows to it; otherwise
            // there is no unambiguous owner, so drop them and require per-tenant re-entry.
            migrationBuilder.Sql(
                "DELETE FROM \"NotificationProviderConfigs\" WHERE (SELECT COUNT(*) FROM \"Tenants\") <> 1;");
            migrationBuilder.Sql(
                "UPDATE \"NotificationProviderConfigs\" SET \"TenantId\" = (SELECT \"Id\" FROM \"Tenants\" LIMIT 1) WHERE (SELECT COUNT(*) FROM \"Tenants\") = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationProviderConfigs_TenantId_ProviderType",
                table: "NotificationProviderConfigs",
                columns: new[] { "TenantId", "ProviderType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationProviderConfigs_Tenants_TenantId",
                table: "NotificationProviderConfigs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotificationProviderConfigs_Tenants_TenantId",
                table: "NotificationProviderConfigs");

            migrationBuilder.DropIndex(
                name: "IX_NotificationProviderConfigs_TenantId_ProviderType",
                table: "NotificationProviderConfigs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "NotificationProviderConfigs");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationProviderConfigs_ProviderType",
                table: "NotificationProviderConfigs",
                column: "ProviderType",
                unique: true);
        }
    }
}

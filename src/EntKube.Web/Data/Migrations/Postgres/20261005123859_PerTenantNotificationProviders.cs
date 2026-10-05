using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
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
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));


            // Provider rows used to be installation-wide, so no existing row says which tenant
            // it belongs to and the column above defaulted every one of them to Guid.Empty —
            // which no tenant has, so the foreign key below would reject them.
            //
            // There is only one case where the owner is knowable: a single-tenant installation,
            // where the sole tenant is necessarily who these credentials were for. Hand them
            // over. With no tenants, or more than one, there is no defensible answer — guessing
            // would hand one tenant another's SMTP password — so the rows go and each tenant
            // re-enters its own on the new "Notification providers" tab.
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

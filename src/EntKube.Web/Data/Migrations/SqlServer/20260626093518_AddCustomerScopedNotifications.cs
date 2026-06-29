using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddCustomerScopedNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SecretExpiryNotifications_TenantId_SentAt",
                table: "SecretExpiryNotifications");

            migrationBuilder.DropIndex(
                name: "IX_SecretExpiryNotificationConfigs_TenantId",
                table: "SecretExpiryNotificationConfigs");

            migrationBuilder.DropIndex(
                name: "IX_NotificationChannels_TenantId_Name",
                table: "NotificationChannels");

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "SecretExpiryNotifications",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "SecretExpiryNotificationConfigs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "NotificationChannels",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecretExpiryNotifications_TenantId_CustomerId_SentAt",
                table: "SecretExpiryNotifications",
                columns: new[] { "TenantId", "CustomerId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecretExpiryNotificationConfigs_TenantId_CustomerId",
                table: "SecretExpiryNotificationConfigs",
                columns: new[] { "TenantId", "CustomerId" },
                unique: true,
                filter: "[CustomerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_TenantId_CustomerId",
                table: "NotificationChannels",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_TenantId_CustomerId_Name",
                table: "NotificationChannels",
                columns: new[] { "TenantId", "CustomerId", "Name" },
                unique: true,
                filter: "[CustomerId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SecretExpiryNotifications_TenantId_CustomerId_SentAt",
                table: "SecretExpiryNotifications");

            migrationBuilder.DropIndex(
                name: "IX_SecretExpiryNotificationConfigs_TenantId_CustomerId",
                table: "SecretExpiryNotificationConfigs");

            migrationBuilder.DropIndex(
                name: "IX_NotificationChannels_TenantId_CustomerId",
                table: "NotificationChannels");

            migrationBuilder.DropIndex(
                name: "IX_NotificationChannels_TenantId_CustomerId_Name",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "SecretExpiryNotifications");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "SecretExpiryNotificationConfigs");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "NotificationChannels");

            migrationBuilder.CreateIndex(
                name: "IX_SecretExpiryNotifications_TenantId_SentAt",
                table: "SecretExpiryNotifications",
                columns: new[] { "TenantId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecretExpiryNotificationConfigs_TenantId",
                table: "SecretExpiryNotificationConfigs",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_TenantId_Name",
                table: "NotificationChannels",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}

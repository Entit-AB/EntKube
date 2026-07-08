using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddKeycloakRealmBackupSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackupSchedule",
                table: "KeycloakRealms",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StorageLinkId",
                table: "KeycloakRealms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakRealms_StorageLinkId",
                table: "KeycloakRealms",
                column: "StorageLinkId");

            migrationBuilder.AddForeignKey(
                name: "FK_KeycloakRealms_StorageLinks_StorageLinkId",
                table: "KeycloakRealms",
                column: "StorageLinkId",
                principalTable: "StorageLinks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakRealms_StorageLinks_StorageLinkId",
                table: "KeycloakRealms");

            migrationBuilder.DropIndex(
                name: "IX_KeycloakRealms_StorageLinkId",
                table: "KeycloakRealms");

            migrationBuilder.DropColumn(
                name: "BackupSchedule",
                table: "KeycloakRealms");

            migrationBuilder.DropColumn(
                name: "StorageLinkId",
                table: "KeycloakRealms");
        }
    }
}

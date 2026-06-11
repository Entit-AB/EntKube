using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class KeycloakBackupStorageLinkNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakBackups_StorageLinks_StorageLinkId",
                table: "KeycloakBackups");

            migrationBuilder.AlterColumn<Guid>(
                name: "StorageLinkId",
                table: "KeycloakBackups",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddForeignKey(
                name: "FK_KeycloakBackups_StorageLinks_StorageLinkId",
                table: "KeycloakBackups",
                column: "StorageLinkId",
                principalTable: "StorageLinks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakBackups_StorageLinks_StorageLinkId",
                table: "KeycloakBackups");

            migrationBuilder.AlterColumn<Guid>(
                name: "StorageLinkId",
                table: "KeycloakBackups",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_KeycloakBackups_StorageLinks_StorageLinkId",
                table: "KeycloakBackups",
                column: "StorageLinkId",
                principalTable: "StorageLinks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

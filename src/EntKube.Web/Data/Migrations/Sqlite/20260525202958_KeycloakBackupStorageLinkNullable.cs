using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class KeycloakBackupStorageLinkNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard: create table with old schema if it doesn't exist (created by a deleted migration).
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "KeycloakBackups" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_KeycloakBackups" PRIMARY KEY,
                    "CompletedAt" TEXT,
                    "CreatedAt" TEXT NOT NULL DEFAULT '',
                    "KeycloakRealmId" TEXT NOT NULL DEFAULT '',
                    "LastError" TEXT,
                    "ObjectKey" TEXT NOT NULL DEFAULT '',
                    "RealmName" TEXT NOT NULL DEFAULT '',
                    "SizeBytes" INTEGER NOT NULL DEFAULT 0,
                    "Status" INTEGER NOT NULL DEFAULT 0,
                    "StorageLinkId" TEXT NOT NULL DEFAULT '',
                    "TenantId" TEXT NOT NULL DEFAULT ''
                );
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakBackups_StorageLinks_StorageLinkId",
                table: "KeycloakBackups");

            migrationBuilder.AlterColumn<Guid>(
                name: "StorageLinkId",
                table: "KeycloakBackups",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

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
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
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

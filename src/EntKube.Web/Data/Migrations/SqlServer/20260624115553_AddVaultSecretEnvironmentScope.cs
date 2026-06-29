using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddVaultSecretEnvironmentScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_VaultId_AppId_Name",
                table: "VaultSecrets");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_EnvironmentId",
                table: "VaultSecrets",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_VaultId_AppId_EnvironmentId_Name",
                table: "VaultSecrets",
                columns: new[] { "VaultId", "AppId", "EnvironmentId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_Environments_EnvironmentId",
                table: "VaultSecrets",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_Environments_EnvironmentId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_EnvironmentId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_VaultId_AppId_EnvironmentId_Name",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "VaultSecrets");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_VaultId_AppId_Name",
                table: "VaultSecrets",
                columns: new[] { "VaultId", "AppId", "Name" },
                unique: true);
        }
    }
}

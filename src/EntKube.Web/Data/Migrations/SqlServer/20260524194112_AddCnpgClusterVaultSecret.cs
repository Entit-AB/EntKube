using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddCnpgClusterVaultSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CnpgClusterId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_CnpgClusterId",
                table: "VaultSecrets",
                column: "CnpgClusterId");

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_CnpgClusters_CnpgClusterId",
                table: "VaultSecrets",
                column: "CnpgClusterId",
                principalTable: "CnpgClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_CnpgClusters_CnpgClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_CnpgClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "CnpgClusterId",
                table: "VaultSecrets");
        }
    }
}

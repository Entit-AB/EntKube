using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddRabbitMQCredentialVaultSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RabbitMQClusterId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_RabbitMQClusterId",
                table: "VaultSecrets",
                column: "RabbitMQClusterId");

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_RabbitMQClusters_RabbitMQClusterId",
                table: "VaultSecrets",
                column: "RabbitMQClusterId",
                principalTable: "RabbitMQClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_RabbitMQClusters_RabbitMQClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_RabbitMQClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "RabbitMQClusterId",
                table: "VaultSecrets");
        }
    }
}

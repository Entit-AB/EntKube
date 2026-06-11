using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddAppSecretClusterId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "KubernetesClusterId",
                table: "VaultSecrets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_KubernetesClusterId",
                table: "VaultSecrets",
                column: "KubernetesClusterId");

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_KubernetesClusters_KubernetesClusterId",
                table: "VaultSecrets",
                column: "KubernetesClusterId",
                principalTable: "KubernetesClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_KubernetesClusters_KubernetesClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_KubernetesClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "KubernetesClusterId",
                table: "VaultSecrets");
        }
    }
}

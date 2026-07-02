using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddClusterKubeconfigVault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: the legacy plaintext "Kubeconfig" column is intentionally left in place here
            // (EF's scaffolder guessed a rename to KubeconfigSecretId, which would corrupt the data).
            // The startup backfill (EnsureClusterKubeconfigsMigratedAsync) reads it to migrate each
            // cluster's kubeconfig into the encrypted vault, then drops the column once every cluster
            // has been migrated.
            migrationBuilder.AddColumn<Guid>(
                name: "KubeconfigSecretId",
                table: "KubernetesClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerClusterId",
                table: "VaultSecrets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_OwnerClusterId",
                table: "VaultSecrets",
                column: "OwnerClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_VaultId_OwnerClusterId_Name",
                table: "VaultSecrets",
                columns: new[] { "VaultId", "OwnerClusterId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_KubernetesClusters_OwnerClusterId",
                table: "VaultSecrets",
                column: "OwnerClusterId",
                principalTable: "KubernetesClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_KubernetesClusters_OwnerClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_OwnerClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_VaultId_OwnerClusterId_Name",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "OwnerClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "KubeconfigSecretId",
                table: "KubernetesClusters");
        }
    }
}

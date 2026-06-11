using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddMongoClusterVaultSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MongoClusterId",
                table: "VaultSecrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_MongoClusterId",
                table: "VaultSecrets",
                column: "MongoClusterId");

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_MongoClusters_MongoClusterId",
                table: "VaultSecrets",
                column: "MongoClusterId",
                principalTable: "MongoClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_MongoClusters_MongoClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_MongoClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "MongoClusterId",
                table: "VaultSecrets");
        }
    }
}

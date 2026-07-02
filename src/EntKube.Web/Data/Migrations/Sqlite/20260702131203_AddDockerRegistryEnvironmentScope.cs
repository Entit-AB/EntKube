using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddDockerRegistryEnvironmentScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "DockerRegistryCredentials",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DockerRegistryCredentials_EnvironmentId",
                table: "DockerRegistryCredentials",
                column: "EnvironmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_DockerRegistryCredentials_Environments_EnvironmentId",
                table: "DockerRegistryCredentials",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DockerRegistryCredentials_Environments_EnvironmentId",
                table: "DockerRegistryCredentials");

            migrationBuilder.DropIndex(
                name: "IX_DockerRegistryCredentials_EnvironmentId",
                table: "DockerRegistryCredentials");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "DockerRegistryCredentials");
        }
    }
}

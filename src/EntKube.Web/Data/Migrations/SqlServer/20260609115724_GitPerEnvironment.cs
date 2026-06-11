using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class GitPerEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerGitRepoPolicies_CustomerId_UrlPattern",
                table: "CustomerGitRepoPolicies");

            migrationBuilder.DropIndex(
                name: "IX_CustomerGitCredentials_CustomerId_Name",
                table: "CustomerGitCredentials");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "CustomerGitRepoPolicies",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "CustomerGitCredentials",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitRepoPolicies_CustomerId_EnvironmentId_UrlPattern",
                table: "CustomerGitRepoPolicies",
                columns: new[] { "CustomerId", "EnvironmentId", "UrlPattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitRepoPolicies_EnvironmentId",
                table: "CustomerGitRepoPolicies",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitCredentials_CustomerId_EnvironmentId_Name",
                table: "CustomerGitCredentials",
                columns: new[] { "CustomerId", "EnvironmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitCredentials_EnvironmentId",
                table: "CustomerGitCredentials",
                column: "EnvironmentId");

            migrationBuilder.Sql("DELETE FROM CustomerGitRepoPolicies WHERE EnvironmentId = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql("DELETE FROM CustomerGitCredentials WHERE EnvironmentId = '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerGitCredentials_Environments_EnvironmentId",
                table: "CustomerGitCredentials",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerGitRepoPolicies_Environments_EnvironmentId",
                table: "CustomerGitRepoPolicies",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerGitCredentials_Environments_EnvironmentId",
                table: "CustomerGitCredentials");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerGitRepoPolicies_Environments_EnvironmentId",
                table: "CustomerGitRepoPolicies");

            migrationBuilder.DropIndex(
                name: "IX_CustomerGitRepoPolicies_CustomerId_EnvironmentId_UrlPattern",
                table: "CustomerGitRepoPolicies");

            migrationBuilder.DropIndex(
                name: "IX_CustomerGitRepoPolicies_EnvironmentId",
                table: "CustomerGitRepoPolicies");

            migrationBuilder.DropIndex(
                name: "IX_CustomerGitCredentials_CustomerId_EnvironmentId_Name",
                table: "CustomerGitCredentials");

            migrationBuilder.DropIndex(
                name: "IX_CustomerGitCredentials_EnvironmentId",
                table: "CustomerGitCredentials");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "CustomerGitRepoPolicies");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "CustomerGitCredentials");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitRepoPolicies_CustomerId_UrlPattern",
                table: "CustomerGitRepoPolicies",
                columns: new[] { "CustomerId", "UrlPattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitCredentials_CustomerId_Name",
                table: "CustomerGitCredentials",
                columns: new[] { "CustomerId", "Name" },
                unique: true);
        }
    }
}

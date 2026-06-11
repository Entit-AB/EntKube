using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddCustomerGitCredentialToGitRepository : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerGitCredentialId",
                table: "GitRepositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositories_CustomerGitCredentialId",
                table: "GitRepositories",
                column: "CustomerGitCredentialId");

            migrationBuilder.AddForeignKey(
                name: "FK_GitRepositories_CustomerGitCredentials_CustomerGitCredentialId",
                table: "GitRepositories",
                column: "CustomerGitCredentialId",
                principalTable: "CustomerGitCredentials",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GitRepositories_CustomerGitCredentials_CustomerGitCredentialId",
                table: "GitRepositories");

            migrationBuilder.DropIndex(
                name: "IX_GitRepositories_CustomerGitCredentialId",
                table: "GitRepositories");

            migrationBuilder.DropColumn(
                name: "CustomerGitCredentialId",
                table: "GitRepositories");
        }
    }
}

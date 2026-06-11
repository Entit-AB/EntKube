using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCustomerGitPoliciesAndCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerGitCredentialId",
                table: "VaultSecrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerGitCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Username = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGitCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerGitCredentials_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerGitCredentials_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerGitRepoPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UrlPattern = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGitRepoPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerGitRepoPolicies_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_CustomerGitCredentialId",
                table: "VaultSecrets",
                column: "CustomerGitCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitCredentials_CustomerId_Name",
                table: "CustomerGitCredentials",
                columns: new[] { "CustomerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitCredentials_TenantId",
                table: "CustomerGitCredentials",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGitRepoPolicies_CustomerId_UrlPattern",
                table: "CustomerGitRepoPolicies",
                columns: new[] { "CustomerId", "UrlPattern" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_CustomerGitCredentials_CustomerGitCredentialId",
                table: "VaultSecrets",
                column: "CustomerGitCredentialId",
                principalTable: "CustomerGitCredentials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_CustomerGitCredentials_CustomerGitCredentialId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "CustomerGitCredentials");

            migrationBuilder.DropTable(
                name: "CustomerGitRepoPolicies");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_CustomerGitCredentialId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "CustomerGitCredentialId",
                table: "VaultSecrets");
        }
    }
}

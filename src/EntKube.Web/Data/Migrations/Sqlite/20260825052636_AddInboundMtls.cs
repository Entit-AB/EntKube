using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddInboundMtls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientCaBundleId",
                table: "AppRoutes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClientCertificateOnly",
                table: "AppRoutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireClientCertificate",
                table: "AppRoutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ClientCaBundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ListenerPort = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 8443),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaBundles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientCaCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BundleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Pem = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaCertificates_ClientCaBundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "ClientCaBundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppRoutes_ClientCaBundleId",
                table: "AppRoutes",
                column: "ClientCaBundleId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaBundles_TenantId_Name",
                table: "ClientCaBundles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaCertificates_BundleId",
                table: "ClientCaCertificates",
                column: "BundleId");

            migrationBuilder.AddForeignKey(
                name: "FK_AppRoutes_ClientCaBundles_ClientCaBundleId",
                table: "AppRoutes",
                column: "ClientCaBundleId",
                principalTable: "ClientCaBundles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppRoutes_ClientCaBundles_ClientCaBundleId",
                table: "AppRoutes");

            migrationBuilder.DropTable(
                name: "ClientCaCertificates");

            migrationBuilder.DropTable(
                name: "ClientCaBundles");

            migrationBuilder.DropIndex(
                name: "IX_AppRoutes_ClientCaBundleId",
                table: "AppRoutes");

            migrationBuilder.DropColumn(
                name: "ClientCaBundleId",
                table: "AppRoutes");

            migrationBuilder.DropColumn(
                name: "ClientCertificateOnly",
                table: "AppRoutes");

            migrationBuilder.DropColumn(
                name: "RequireClientCertificate",
                table: "AppRoutes");
        }
    }
}

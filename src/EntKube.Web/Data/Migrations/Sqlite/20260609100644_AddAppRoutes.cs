using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddAppRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    TlsMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ClusterIssuerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TlsCertificate = table.Column<string>(type: "TEXT", nullable: true),
                    TlsPrivateKey = table.Column<string>(type: "TEXT", nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppRoutes_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppDeploymentRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppRouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PathPrefix = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ServicePort = table.Column<int>(type: "INTEGER", nullable: false),
                    GatewayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GatewayNamespace = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastHealthCheckAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    IsReachable = table.Column<bool>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppDeploymentRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppDeploymentRoutes_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppDeploymentRoutes_AppRoutes_AppRouteId",
                        column: x => x.AppRouteId,
                        principalTable: "AppRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppDeploymentRoutes_AppDeploymentId",
                table: "AppDeploymentRoutes",
                column: "AppDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppDeploymentRoutes_AppRouteId",
                table: "AppDeploymentRoutes",
                column: "AppRouteId");

            migrationBuilder.CreateIndex(
                name: "IX_AppRoutes_AppId",
                table: "AppRoutes",
                column: "AppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppDeploymentRoutes");

            migrationBuilder.DropTable(
                name: "AppRoutes");
        }
    }
}

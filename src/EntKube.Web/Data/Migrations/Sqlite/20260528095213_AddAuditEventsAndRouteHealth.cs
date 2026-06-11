using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddAuditEventsAndRouteHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReachable",
                table: "ExternalRoutes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHealthCheckAt",
                table: "ExternalRoutes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastStatusCode",
                table: "ExternalRoutes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PerformedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ResourceKind = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ResourceName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_AppDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_DeploymentId",
                table: "AuditEvents",
                column: "DeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAt",
                table: "AuditEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "IsReachable",
                table: "ExternalRoutes");

            migrationBuilder.DropColumn(
                name: "LastHealthCheckAt",
                table: "ExternalRoutes");

            migrationBuilder.DropColumn(
                name: "LastStatusCode",
                table: "ExternalRoutes");
        }
    }
}

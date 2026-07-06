using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAppL4Routes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppL4Routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Protocol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ExternalPort = table.Column<int>(type: "integer", nullable: false),
                    ServiceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ServicePort = table.Column<int>(type: "integer", nullable: false),
                    GatewayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GatewayNamespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsManaged = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ClusterAppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastHealthCheckAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsReachable = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppL4Routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppL4Routes_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppL4Routes_AppDeploymentId",
                table: "AppL4Routes",
                column: "AppDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppL4Routes_AppId",
                table: "AppL4Routes",
                column: "AppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppL4Routes");
        }
    }
}

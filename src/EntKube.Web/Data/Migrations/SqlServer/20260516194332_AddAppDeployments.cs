using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddAppDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppDeployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Namespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    SyncStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HealthStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HelmRepoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HelmChartName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HelmChartVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    HelmValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppDeployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppDeployments_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppDeployments_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppDeployments_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentManifests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    YamlContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentManifests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentManifests_AppDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Namespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    SyncStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HealthStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentResources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentResources_AppDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeploymentResources_DeploymentResources_ParentResourceId",
                        column: x => x.ParentResourceId,
                        principalTable: "DeploymentResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppDeployments_AppId_Name",
                table: "AppDeployments",
                columns: new[] { "AppId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppDeployments_ClusterId",
                table: "AppDeployments",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_AppDeployments_EnvironmentId",
                table: "AppDeployments",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentManifests_DeploymentId",
                table: "DeploymentManifests",
                column: "DeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentResources_DeploymentId",
                table: "DeploymentResources",
                column: "DeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentResources_ParentResourceId",
                table: "DeploymentResources",
                column: "ParentResourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentManifests");

            migrationBuilder.DropTable(
                name: "DeploymentResources");

            migrationBuilder.DropTable(
                name: "AppDeployments");
        }
    }
}

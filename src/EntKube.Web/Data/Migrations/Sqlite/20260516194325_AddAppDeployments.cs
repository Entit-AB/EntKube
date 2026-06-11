using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    SyncStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    HealthStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StatusMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HelmRepoUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    HelmChartName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    HelmChartVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    HelmValues = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    YamlContent = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    SyncStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    HealthStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StatusMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ParentResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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

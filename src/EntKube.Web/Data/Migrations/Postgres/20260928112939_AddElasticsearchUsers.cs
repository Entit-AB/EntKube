using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddElasticsearchUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ElasticsearchUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ElasticsearchClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    IndexPattern = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Access = table.Column<int>(type: "integer", nullable: false),
                    LastAppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchUsers_ElasticsearchClusters_ElasticsearchClust~",
                        column: x => x.ElasticsearchClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ElasticsearchBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ElasticsearchClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ElasticsearchUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    KubernetesSecretName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchBindings_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ElasticsearchBindings_ElasticsearchClusters_ElasticsearchCl~",
                        column: x => x.ElasticsearchClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ElasticsearchBindings_ElasticsearchUsers_ElasticsearchUserId",
                        column: x => x.ElasticsearchUserId,
                        principalTable: "ElasticsearchUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchBindings_AppDeploymentId_KubernetesSecretName",
                table: "ElasticsearchBindings",
                columns: new[] { "AppDeploymentId", "KubernetesSecretName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchBindings_ElasticsearchClusterId",
                table: "ElasticsearchBindings",
                column: "ElasticsearchClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchBindings_ElasticsearchUserId",
                table: "ElasticsearchBindings",
                column: "ElasticsearchUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchUsers_ElasticsearchClusterId_Username",
                table: "ElasticsearchUsers",
                columns: new[] { "ElasticsearchClusterId", "Username" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElasticsearchBindings");

            migrationBuilder.DropTable(
                name: "ElasticsearchUsers");
        }
    }
}

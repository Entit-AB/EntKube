using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddCacheBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CacheBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RedisClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KubernetesSecretName = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    SyncEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CacheBindings_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CacheBindings_RedisClusters_RedisClusterId",
                        column: x => x.RedisClusterId,
                        principalTable: "RedisClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CacheBindings_AppDeploymentId",
                table: "CacheBindings",
                column: "AppDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_CacheBindings_RedisClusterId",
                table: "CacheBindings",
                column: "RedisClusterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CacheBindings");
        }
    }
}

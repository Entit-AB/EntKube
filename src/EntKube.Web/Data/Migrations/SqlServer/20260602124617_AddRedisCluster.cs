using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddRedisCluster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RedisClusterId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RedisClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Namespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    ClusterSize = table.Column<int>(type: "int", nullable: false),
                    RedisVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageSize = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageClass = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    PersistenceEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RedisClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RedisClusters_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RedisClusters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_RedisClusterId",
                table: "VaultSecrets",
                column: "RedisClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_RedisClusters_KubernetesClusterId_Name_Namespace",
                table: "RedisClusters",
                columns: new[] { "KubernetesClusterId", "Name", "Namespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RedisClusters_TenantId",
                table: "RedisClusters",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_RedisClusters_RedisClusterId",
                table: "VaultSecrets",
                column: "RedisClusterId",
                principalTable: "RedisClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_RedisClusters_RedisClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "RedisClusters");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_RedisClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "RedisClusterId",
                table: "VaultSecrets");
        }
    }
}

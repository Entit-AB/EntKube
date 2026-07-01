using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddKafkaClusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "KafkaClusterId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KafkaClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Namespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    KafkaVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Replicas = table.Column<int>(type: "int", nullable: false),
                    StorageSize = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageClass = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    CpuRequest = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MemoryRequest = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MemoryLimit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AuthEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KafkaClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KafkaClusters_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KafkaClusters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KafkaTopics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KafkaClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(249)", maxLength: 249, nullable: false),
                    Partitions = table.Column<int>(type: "int", nullable: false),
                    Replicas = table.Column<int>(type: "int", nullable: false),
                    RetentionMs = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KafkaTopics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KafkaTopics_KafkaClusters_KafkaClusterId",
                        column: x => x.KafkaClusterId,
                        principalTable: "KafkaClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KafkaUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KafkaClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    ProducerTopics = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ConsumerTopics = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ConsumerGroup = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    SuperUser = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KafkaUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KafkaUsers_KafkaClusters_KafkaClusterId",
                        column: x => x.KafkaClusterId,
                        principalTable: "KafkaClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KafkaBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KafkaClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KafkaUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    KubernetesSecretName = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    SyncEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KafkaBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KafkaBindings_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KafkaBindings_KafkaClusters_KafkaClusterId",
                        column: x => x.KafkaClusterId,
                        principalTable: "KafkaClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KafkaBindings_KafkaUsers_KafkaUserId",
                        column: x => x.KafkaUserId,
                        principalTable: "KafkaUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_KafkaClusterId",
                table: "VaultSecrets",
                column: "KafkaClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_KafkaBindings_AppDeploymentId",
                table: "KafkaBindings",
                column: "AppDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_KafkaBindings_KafkaClusterId",
                table: "KafkaBindings",
                column: "KafkaClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_KafkaBindings_KafkaUserId",
                table: "KafkaBindings",
                column: "KafkaUserId");

            migrationBuilder.CreateIndex(
                name: "IX_KafkaClusters_KubernetesClusterId_Name_Namespace",
                table: "KafkaClusters",
                columns: new[] { "KubernetesClusterId", "Name", "Namespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KafkaClusters_TenantId",
                table: "KafkaClusters",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KafkaTopics_KafkaClusterId_Name",
                table: "KafkaTopics",
                columns: new[] { "KafkaClusterId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KafkaUsers_KafkaClusterId_Username",
                table: "KafkaUsers",
                columns: new[] { "KafkaClusterId", "Username" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_KafkaClusters_KafkaClusterId",
                table: "VaultSecrets",
                column: "KafkaClusterId",
                principalTable: "KafkaClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_KafkaClusters_KafkaClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "KafkaBindings");

            migrationBuilder.DropTable(
                name: "KafkaTopics");

            migrationBuilder.DropTable(
                name: "KafkaUsers");

            migrationBuilder.DropTable(
                name: "KafkaClusters");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_KafkaClusterId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "KafkaClusterId",
                table: "VaultSecrets");
        }
    }
}

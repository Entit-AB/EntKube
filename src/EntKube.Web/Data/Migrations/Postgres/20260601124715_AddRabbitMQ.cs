using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRabbitMQ : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RabbitMQClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    RabbitMQVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Replicas = table.Column<int>(type: "integer", nullable: false),
                    StorageSize = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StorageClass = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    StorageLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    BackupSchedule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MaxBackups = table.Column<int>(type: "integer", nullable: false, defaultValue: 10),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RabbitMQClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RabbitMQClusters_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RabbitMQClusters_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RabbitMQClusters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RabbitMQBackups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RabbitMQClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    ObjectKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ClusterName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RabbitMQBackups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RabbitMQBackups_RabbitMQClusters_RabbitMQClusterId",
                        column: x => x.RabbitMQClusterId,
                        principalTable: "RabbitMQClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RabbitMQBackups_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RabbitMQBackups_RabbitMQClusterId",
                table: "RabbitMQBackups",
                column: "RabbitMQClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_RabbitMQBackups_StorageLinkId",
                table: "RabbitMQBackups",
                column: "StorageLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_RabbitMQClusters_KubernetesClusterId_Name_Namespace",
                table: "RabbitMQClusters",
                columns: new[] { "KubernetesClusterId", "Name", "Namespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RabbitMQClusters_StorageLinkId",
                table: "RabbitMQClusters",
                column: "StorageLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_RabbitMQClusters_TenantId",
                table: "RabbitMQClusters",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RabbitMQBackups");

            migrationBuilder.DropTable(
                name: "RabbitMQClusters");
        }
    }
}

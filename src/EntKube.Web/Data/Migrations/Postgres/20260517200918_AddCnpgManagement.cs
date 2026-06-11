using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCnpgManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CnpgDatabaseId",
                table: "VaultSecrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CnpgClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    PostgresVersion = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Instances = table.Column<int>(type: "integer", nullable: false),
                    StorageSize = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    BackupSchedule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CnpgClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CnpgClusters_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CnpgClusters_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CnpgClusters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CnpgBackups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CnpgClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CnpgBackups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CnpgBackups_CnpgClusters_CnpgClusterId",
                        column: x => x.CnpgClusterId,
                        principalTable: "CnpgClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CnpgDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CnpgClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Owner = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CnpgDatabases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CnpgDatabases_CnpgClusters_CnpgClusterId",
                        column: x => x.CnpgClusterId,
                        principalTable: "CnpgClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_CnpgDatabaseId",
                table: "VaultSecrets",
                column: "CnpgDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_CnpgBackups_CnpgClusterId_Name",
                table: "CnpgBackups",
                columns: new[] { "CnpgClusterId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CnpgClusters_KubernetesClusterId_Name_Namespace",
                table: "CnpgClusters",
                columns: new[] { "KubernetesClusterId", "Name", "Namespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CnpgClusters_StorageLinkId",
                table: "CnpgClusters",
                column: "StorageLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_CnpgClusters_TenantId",
                table: "CnpgClusters",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CnpgDatabases_CnpgClusterId_Name",
                table: "CnpgDatabases",
                columns: new[] { "CnpgClusterId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_CnpgDatabases_CnpgDatabaseId",
                table: "VaultSecrets",
                column: "CnpgDatabaseId",
                principalTable: "CnpgDatabases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_CnpgDatabases_CnpgDatabaseId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "CnpgBackups");

            migrationBuilder.DropTable(
                name: "CnpgDatabases");

            migrationBuilder.DropTable(
                name: "CnpgClusters");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_CnpgDatabaseId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "CnpgDatabaseId",
                table: "VaultSecrets");
        }
    }
}

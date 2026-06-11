using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddMongoEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MongoDatabaseId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MongoClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Namespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    MongoVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Members = table.Column<int>(type: "int", nullable: false),
                    StorageSize = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BackupSchedule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RetentionDays = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MongoClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MongoClusters_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MongoClusters_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MongoClusters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MongoBackups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MongoClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MongoBackups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MongoBackups_MongoClusters_MongoClusterId",
                        column: x => x.MongoClusterId,
                        principalTable: "MongoClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MongoDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MongoClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MongoDatabases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MongoDatabases_MongoClusters_MongoClusterId",
                        column: x => x.MongoClusterId,
                        principalTable: "MongoClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_MongoDatabaseId",
                table: "VaultSecrets",
                column: "MongoDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_MongoBackups_MongoClusterId_Name",
                table: "MongoBackups",
                columns: new[] { "MongoClusterId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MongoClusters_KubernetesClusterId_Name_Namespace",
                table: "MongoClusters",
                columns: new[] { "KubernetesClusterId", "Name", "Namespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MongoClusters_StorageLinkId",
                table: "MongoClusters",
                column: "StorageLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_MongoClusters_TenantId",
                table: "MongoClusters",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MongoDatabases_MongoClusterId_Name",
                table: "MongoDatabases",
                columns: new[] { "MongoClusterId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_MongoDatabases_MongoDatabaseId",
                table: "VaultSecrets",
                column: "MongoDatabaseId",
                principalTable: "MongoDatabases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_MongoDatabases_MongoDatabaseId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "MongoBackups");

            migrationBuilder.DropTable(
                name: "MongoDatabases");

            migrationBuilder.DropTable(
                name: "MongoClusters");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_MongoDatabaseId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "MongoDatabaseId",
                table: "VaultSecrets");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddVaultAndComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClusterComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ComponentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClusterComponents_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecretVaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncryptedDataKey = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Nonce = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretVaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecretVaults_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VaultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EncryptedValue = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Nonce = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    AppId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SyncToKubernetes = table.Column<bool>(type: "bit", nullable: false),
                    KubernetesSecretName = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    KubernetesNamespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultSecrets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VaultSecrets_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VaultSecrets_ClusterComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VaultSecrets_SecretVaults_VaultId",
                        column: x => x.VaultId,
                        principalTable: "SecretVaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClusterComponents_ClusterId_Name",
                table: "ClusterComponents",
                columns: new[] { "ClusterId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecretVaults_TenantId",
                table: "SecretVaults",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_AppId",
                table: "VaultSecrets",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_ComponentId",
                table: "VaultSecrets",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_VaultId_AppId_Name",
                table: "VaultSecrets",
                columns: new[] { "VaultId", "AppId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_VaultId_ComponentId_Name",
                table: "VaultSecrets",
                columns: new[] { "VaultId", "ComponentId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "ClusterComponents");

            migrationBuilder.DropTable(
                name: "SecretVaults");
        }
    }
}

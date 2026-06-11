using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddKeycloakComponentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakRealms_KeycloakConnections_KeycloakConnectionId",
                table: "KeycloakRealms");

            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_KeycloakConnections_KeycloakConnectionId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "KeycloakConnections");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_KeycloakConnectionId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "KeycloakConnectionId",
                table: "VaultSecrets");

            migrationBuilder.RenameColumn(
                name: "KeycloakConnectionId",
                table: "KeycloakRealms",
                newName: "KeycloakComponentConfigId");

            migrationBuilder.RenameIndex(
                name: "IX_KeycloakRealms_KeycloakConnectionId_RealmName",
                table: "KeycloakRealms",
                newName: "IX_KeycloakRealms_KeycloakComponentConfigId_RealmName");

            migrationBuilder.CreateTable(
                name: "KeycloakComponentConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CnpgDatabaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AdminUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AdminUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeycloakComponentConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeycloakComponentConfigs_ClusterComponents_ClusterComponentId",
                        column: x => x.ClusterComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KeycloakComponentConfigs_CnpgDatabases_CnpgDatabaseId",
                        column: x => x.CnpgDatabaseId,
                        principalTable: "CnpgDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_KeycloakComponentConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakComponentConfigs_ClusterComponentId",
                table: "KeycloakComponentConfigs",
                column: "ClusterComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakComponentConfigs_CnpgDatabaseId",
                table: "KeycloakComponentConfigs",
                column: "CnpgDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakComponentConfigs_TenantId",
                table: "KeycloakComponentConfigs",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_KeycloakRealms_KeycloakComponentConfigs_KeycloakComponentConfigId",
                table: "KeycloakRealms",
                column: "KeycloakComponentConfigId",
                principalTable: "KeycloakComponentConfigs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakRealms_KeycloakComponentConfigs_KeycloakComponentConfigId",
                table: "KeycloakRealms");

            migrationBuilder.DropTable(
                name: "KeycloakComponentConfigs");

            migrationBuilder.RenameColumn(
                name: "KeycloakComponentConfigId",
                table: "KeycloakRealms",
                newName: "KeycloakConnectionId");

            migrationBuilder.RenameIndex(
                name: "IX_KeycloakRealms_KeycloakComponentConfigId_RealmName",
                table: "KeycloakRealms",
                newName: "IX_KeycloakRealms_KeycloakConnectionId_RealmName");

            migrationBuilder.AddColumn<Guid>(
                name: "KeycloakConnectionId",
                table: "VaultSecrets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KeycloakConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdminPasswordSecretId = table.Column<Guid>(type: "TEXT", nullable: true),
                    KubernetesClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdminUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AdminUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeycloakConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeycloakConnections_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KeycloakConnections_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KeycloakConnections_VaultSecrets_AdminPasswordSecretId",
                        column: x => x.AdminPasswordSecretId,
                        principalTable: "VaultSecrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_KeycloakConnectionId",
                table: "VaultSecrets",
                column: "KeycloakConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakConnections_AdminPasswordSecretId",
                table: "KeycloakConnections",
                column: "AdminPasswordSecretId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakConnections_KubernetesClusterId",
                table: "KeycloakConnections",
                column: "KubernetesClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakConnections_TenantId",
                table: "KeycloakConnections",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_KeycloakRealms_KeycloakConnections_KeycloakConnectionId",
                table: "KeycloakRealms",
                column: "KeycloakConnectionId",
                principalTable: "KeycloakConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_KeycloakConnections_KeycloakConnectionId",
                table: "VaultSecrets",
                column: "KeycloakConnectionId",
                principalTable: "KeycloakConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

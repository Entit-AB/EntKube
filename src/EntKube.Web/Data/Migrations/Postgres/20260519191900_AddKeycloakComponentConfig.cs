using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddKeycloakComponentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The migration that originally created KeycloakConnections and KeycloakRealms
            // was removed from history. This migration now creates both tables from scratch
            // using the final schema (post-rename), so fresh databases work correctly.

            migrationBuilder.CreateTable(
                name: "KeycloakComponentConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CnpgDatabaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdminUsername = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AdminUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeycloakComponentConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeycloakComponentConfigs_ClusterComponents_ClusterComponent~",
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

            migrationBuilder.CreateTable(
                name: "KeycloakRealms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountTheme = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    KeycloakComponentConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAppId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoginTheme = table.Column<string>(type: "text", nullable: true),
                    RealmName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeycloakRealms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeycloakRealms_Apps_LinkedAppId",
                        column: x => x.LinkedAppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_KeycloakRealms_KeycloakComponentConfigs_KeycloakComponentCo~",
                        column: x => x.KeycloakComponentConfigId,
                        principalTable: "KeycloakComponentConfigs",
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

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakRealms_LinkedAppId",
                table: "KeycloakRealms",
                column: "LinkedAppId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakRealms_KeycloakComponentConfigId_RealmName",
                table: "KeycloakRealms",
                columns: new[] { "KeycloakComponentConfigId", "RealmName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "KeycloakRealms");
            migrationBuilder.DropTable(name: "KeycloakComponentConfigs");
        }
    }
}

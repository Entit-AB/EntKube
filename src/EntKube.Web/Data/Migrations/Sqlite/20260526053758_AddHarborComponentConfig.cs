using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddHarborComponentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HarborComponentConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CnpgDatabaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StorageLinkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AdminUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RegistryUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarborComponentConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HarborComponentConfigs_ClusterComponents_ClusterComponentId",
                        column: x => x.ClusterComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HarborComponentConfigs_CnpgDatabases_CnpgDatabaseId",
                        column: x => x.CnpgDatabaseId,
                        principalTable: "CnpgDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HarborComponentConfigs_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HarborComponentConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HarborComponentConfigs_ClusterComponentId",
                table: "HarborComponentConfigs",
                column: "ClusterComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_HarborComponentConfigs_CnpgDatabaseId",
                table: "HarborComponentConfigs",
                column: "CnpgDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_HarborComponentConfigs_StorageLinkId",
                table: "HarborComponentConfigs",
                column: "StorageLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_HarborComponentConfigs_TenantId",
                table: "HarborComponentConfigs",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HarborComponentConfigs");
        }
    }
}

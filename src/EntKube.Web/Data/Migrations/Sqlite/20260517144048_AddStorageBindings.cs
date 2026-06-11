using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddStorageBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    KubernetesSecretName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    SyncEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageBindings_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StorageBindings_ClusterComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StorageBindings_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageBindings_AppDeploymentId",
                table: "StorageBindings",
                column: "AppDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageBindings_ComponentId",
                table: "StorageBindings",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageBindings_StorageLinkId",
                table: "StorageBindings",
                column: "StorageLinkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageBindings");
        }
    }
}

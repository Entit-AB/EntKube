using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddDatabaseBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CnpgDatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MongoDatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppDeploymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KubernetesSecretName = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    SyncEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseBindings_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseBindings_CnpgDatabases_CnpgDatabaseId",
                        column: x => x.CnpgDatabaseId,
                        principalTable: "CnpgDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DatabaseBindings_MongoDatabases_MongoDatabaseId",
                        column: x => x.MongoDatabaseId,
                        principalTable: "MongoDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBindings_AppDeploymentId",
                table: "DatabaseBindings",
                column: "AppDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBindings_CnpgDatabaseId",
                table: "DatabaseBindings",
                column: "CnpgDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBindings_MongoDatabaseId",
                table: "DatabaseBindings",
                column: "MongoDatabaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseBindings");
        }
    }
}

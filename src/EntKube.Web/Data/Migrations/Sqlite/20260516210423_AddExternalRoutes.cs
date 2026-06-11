using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddExternalRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ServicePort = table.Column<int>(type: "INTEGER", nullable: false),
                    PathPrefix = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TlsMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ClusterIssuerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TlsCertificate = table.Column<string>(type: "TEXT", nullable: true),
                    TlsPrivateKey = table.Column<string>(type: "TEXT", nullable: true),
                    GatewayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GatewayNamespace = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalRoutes_ClusterComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalRoutes_ComponentId",
                table: "ExternalRoutes",
                column: "ComponentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalRoutes");
        }
    }
}

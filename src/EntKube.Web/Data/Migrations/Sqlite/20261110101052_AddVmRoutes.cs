using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddVmRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VmRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    VmName = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    VmPort = table.Column<int>(type: "INTEGER", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    ClusterIssuerName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    RequestTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalPort = table.Column<int>(type: "INTEGER", nullable: true),
                    ClusterAppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VmRoutes_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VmRoutes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VmRoutes_ClusterId_Namespace_VmName",
                table: "VmRoutes",
                columns: new[] { "ClusterId", "Namespace", "VmName" });

            migrationBuilder.CreateIndex(
                name: "IX_VmRoutes_TenantId",
                table: "VmRoutes",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VmRoutes");
        }
    }
}

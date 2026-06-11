using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddHarborProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HarborProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HarborComponentConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    LinkedAppId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarborProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HarborProjects_Apps_LinkedAppId",
                        column: x => x.LinkedAppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HarborProjects_HarborComponentConfigs_HarborComponentConfigId",
                        column: x => x.HarborComponentConfigId,
                        principalTable: "HarborComponentConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HarborProjects_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HarborProjects_HarborComponentConfigId_ProjectName",
                table: "HarborProjects",
                columns: new[] { "HarborComponentConfigId", "ProjectName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarborProjects_LinkedAppId",
                table: "HarborProjects",
                column: "LinkedAppId");

            migrationBuilder.CreateIndex(
                name: "IX_HarborProjects_TenantId",
                table: "HarborProjects",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HarborProjects");
        }
    }
}

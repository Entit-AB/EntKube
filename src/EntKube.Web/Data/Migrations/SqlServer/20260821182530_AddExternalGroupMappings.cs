using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddExternalGroupMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalGroupMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalGroup = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalGroupMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalGroupMappings_TenantRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "TenantRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalGroupMappings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGroupMappings_ExternalGroup_TenantId",
                table: "ExternalGroupMappings",
                columns: new[] { "ExternalGroup", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGroupMappings_RoleId",
                table: "ExternalGroupMappings",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGroupMappings_TenantId",
                table: "ExternalGroupMappings",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalGroupMappings");
        }
    }
}

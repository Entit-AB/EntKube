using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddDeploymentAppliedResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeploymentAppliedResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    Prunable = table.Column<bool>(type: "boolean", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentAppliedResources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentAppliedResources_AppDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentAppliedResources_DeploymentId",
                table: "DeploymentAppliedResources",
                column: "DeploymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentAppliedResources");
        }
    }
}

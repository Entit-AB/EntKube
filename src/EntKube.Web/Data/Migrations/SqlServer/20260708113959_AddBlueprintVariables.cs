using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddBlueprintVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlueprintVariables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlueprintId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintVariables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlueprintVariables_ClusterBlueprints_BlueprintId",
                        column: x => x.BlueprintId,
                        principalTable: "ClusterBlueprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlueprintVariableValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintVariableValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlueprintVariableValues_BlueprintVariables_VariableId",
                        column: x => x.VariableId,
                        principalTable: "BlueprintVariables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintVariables_BlueprintId_Name",
                table: "BlueprintVariables",
                columns: new[] { "BlueprintId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintVariableValues_VariableId_EnvironmentId",
                table: "BlueprintVariableValues",
                columns: new[] { "VariableId", "EnvironmentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlueprintVariableValues");

            migrationBuilder.DropTable(
                name: "BlueprintVariables");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddBlueprintRollouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "BootstrapRuns",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "RolloutTargetId",
                table: "BootstrapRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlueprintRollouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlueprintId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlueprintName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AutoAdvance = table.Column<bool>(type: "boolean", nullable: false),
                    TriggeredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintRollouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlueprintRollouts_ClusterBlueprints_BlueprintId",
                        column: x => x.BlueprintId,
                        principalTable: "ClusterBlueprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlueprintRolloutTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RolloutId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BootstrapRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlueprintRolloutTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlueprintRolloutTargets_BlueprintRollouts_RolloutId",
                        column: x => x.RolloutId,
                        principalTable: "BlueprintRollouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintRollouts_BlueprintId",
                table: "BlueprintRollouts",
                column: "BlueprintId");

            migrationBuilder.CreateIndex(
                name: "IX_BlueprintRolloutTargets_RolloutId_Order",
                table: "BlueprintRolloutTargets",
                columns: new[] { "RolloutId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlueprintRolloutTargets");

            migrationBuilder.DropTable(
                name: "BlueprintRollouts");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "BootstrapRuns");

            migrationBuilder.DropColumn(
                name: "RolloutTargetId",
                table: "BootstrapRuns");
        }
    }
}

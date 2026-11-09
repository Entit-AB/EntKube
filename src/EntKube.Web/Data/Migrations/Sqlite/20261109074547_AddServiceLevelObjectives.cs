using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddServiceLevelObjectives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SloId",
                table: "TelemetryAlertRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceLevelObjectives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    TargetPercent = table.Column<double>(type: "REAL", nullable: false),
                    WindowDays = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Service = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    LatencyThresholdMs = table.Column<double>(type: "REAL", nullable: true),
                    AlertAtBudgetSpentPercent = table.Column<double>(type: "REAL", nullable: true),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLevelObjectives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SloDailyTallies",
                columns: table => new
                {
                    SloId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Good = table.Column<long>(type: "INTEGER", nullable: false),
                    Total = table.Column<long>(type: "INTEGER", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SloDailyTallies", x => new { x.SloId, x.Day });
                    table.ForeignKey(
                        name: "FK_SloDailyTallies_ServiceLevelObjectives_SloId",
                        column: x => x.SloId,
                        principalTable: "ServiceLevelObjectives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLevelObjectives_TenantId",
                table: "ServiceLevelObjectives",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SloDailyTallies_Day",
                table: "SloDailyTallies",
                column: "Day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SloDailyTallies");

            migrationBuilder.DropTable(
                name: "ServiceLevelObjectives");

            migrationBuilder.DropColumn(
                name: "SloId",
                table: "TelemetryAlertRules");
        }
    }
}

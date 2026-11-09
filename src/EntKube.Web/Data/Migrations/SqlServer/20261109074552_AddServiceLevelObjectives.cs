using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
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
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceLevelObjectives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TargetPercent = table.Column<double>(type: "float", nullable: false),
                    WindowDays = table.Column<int>(type: "int", nullable: false),
                    CheckId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Service = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    LatencyThresholdMs = table.Column<double>(type: "float", nullable: true),
                    AlertAtBudgetSpentPercent = table.Column<double>(type: "float", nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLevelObjectives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SloDailyTallies",
                columns: table => new
                {
                    SloId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Good = table.Column<long>(type: "bigint", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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

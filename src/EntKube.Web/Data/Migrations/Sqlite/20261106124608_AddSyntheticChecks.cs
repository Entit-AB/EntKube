using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddSyntheticChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckId",
                table: "TelemetryAlertRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyntheticChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ExpectedStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpectedText = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    FailuresBeforeAlert = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    LastDurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    LastFailure = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntheticChecks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyntheticCheckResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CheckId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Passed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntheticCheckResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyntheticCheckResults_SyntheticChecks_CheckId",
                        column: x => x.CheckId,
                        principalTable: "SyntheticChecks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyntheticCheckResults_At",
                table: "SyntheticCheckResults",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_SyntheticCheckResults_CheckId_At",
                table: "SyntheticCheckResults",
                columns: new[] { "CheckId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_SyntheticChecks_TenantId_IsEnabled_NextRunAt",
                table: "SyntheticChecks",
                columns: new[] { "TenantId", "IsEnabled", "NextRunAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyntheticCheckResults");

            migrationBuilder.DropTable(
                name: "SyntheticChecks");

            migrationBuilder.DropColumn(
                name: "CheckId",
                table: "TelemetryAlertRules");
        }
    }
}

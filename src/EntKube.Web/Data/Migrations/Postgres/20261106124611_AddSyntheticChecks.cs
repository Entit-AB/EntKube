using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
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
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyntheticChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ExpectedStatus = table.Column<int>(type: "integer", nullable: true),
                    ExpectedText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    FailuresBeforeAlert = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastStatusCode = table.Column<int>(type: "integer", nullable: true),
                    LastDurationMs = table.Column<int>(type: "integer", nullable: true),
                    LastFailure = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntheticChecks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyntheticCheckResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CheckId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false)
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

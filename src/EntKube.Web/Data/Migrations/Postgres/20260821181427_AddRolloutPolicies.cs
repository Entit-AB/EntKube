using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRolloutPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeploymentRollouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecideAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TriggeredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Verdict = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SignalsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentRollouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentRollouts_AppDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolloutPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AnalysisWindowMinutes = table.Column<int>(type: "integer", nullable: false),
                    WarmupMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxErrorRatePercent = table.Column<double>(type: "double precision", nullable: true),
                    MaxLatencyP95Ms = table.Column<double>(type: "double precision", nullable: true),
                    MaxRestarts = table.Column<int>(type: "integer", nullable: true),
                    MinReadyFraction = table.Column<double>(type: "double precision", nullable: true),
                    MaxErrorBudgetBurnRate = table.Column<double>(type: "double precision", nullable: true),
                    OnFailure = table.Column<int>(type: "integer", nullable: false),
                    OnInconclusive = table.Column<int>(type: "integer", nullable: false),
                    TelemetryServiceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolloutPolicies_AppDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRollouts_DeploymentId_StartedAt",
                table: "DeploymentRollouts",
                columns: new[] { "DeploymentId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRollouts_Status",
                table: "DeploymentRollouts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutPolicies_DeploymentId",
                table: "RolloutPolicies",
                column: "DeploymentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentRollouts");

            migrationBuilder.DropTable(
                name: "RolloutPolicies");
        }
    }
}

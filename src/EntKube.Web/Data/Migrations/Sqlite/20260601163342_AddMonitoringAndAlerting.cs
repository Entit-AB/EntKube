using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddMonitoringAndAlerting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AlertName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    LabelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertIncidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertIncidents_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentHealthSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HealthStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SyncStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ReadyReplicas = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalReplicas = table.Column<int>(type: "INTEGER", nullable: true),
                    SnapshotAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentHealthSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentHealthSnapshots_AppDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeverityFilter = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationChannels_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentNotes_AlertIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "AlertIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsFiring = table.Column<bool>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_AlertIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "AlertIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_NotificationChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertIncidents_ClusterId_Fingerprint",
                table: "AlertIncidents",
                columns: new[] { "ClusterId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertIncidents_StartsAt",
                table: "AlertIncidents",
                column: "StartsAt");

            migrationBuilder.CreateIndex(
                name: "IX_AlertIncidents_Status",
                table: "AlertIncidents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentHealthSnapshots_DeploymentId_SnapshotAt",
                table: "DeploymentHealthSnapshots",
                columns: new[] { "DeploymentId", "SnapshotAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentNotes_IncidentId",
                table: "IncidentNotes",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_TenantId_Name",
                table: "NotificationChannels",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ChannelId",
                table: "NotificationDeliveries",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_IncidentId_ChannelId_IsFiring",
                table: "NotificationDeliveries",
                columns: new[] { "IncidentId", "ChannelId", "IsFiring" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentHealthSnapshots");

            migrationBuilder.DropTable(
                name: "IncidentNotes");

            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "AlertIncidents");

            migrationBuilder.DropTable(
                name: "NotificationChannels");
        }
    }
}

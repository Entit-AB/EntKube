using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    [DbContext(typeof(SqlServerApplicationDbContext))]
    [Migration("20260611000000_AddOpsTeamFeatures")]
    public partial class AddOpsTeamFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "AlertIncidents",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AssignedAt",
                table: "AlertIncidents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OnCallSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallSchedules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallShifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssigneeName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AssigneeEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StartsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallShifts_OnCallSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "OnCallSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertRoutingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatchAlertName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MatchNamespace = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MatchSeverity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MatchLabelKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MatchLabelValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRoutingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertRoutingRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertRoutingRules_NotificationChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_ScheduleId",
                table: "OnCallShifts",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_StartsAt",
                table: "OnCallShifts",
                column: "StartsAt");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRoutingRules_TenantId",
                table: "AlertRoutingRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRoutingRules_ChannelId",
                table: "AlertRoutingRules",
                column: "ChannelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertRoutingRules");

            migrationBuilder.DropTable(
                name: "OnCallShifts");

            migrationBuilder.DropTable(
                name: "OnCallSchedules");

            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "AlertIncidents");

            migrationBuilder.DropColumn(
                name: "AssignedAt",
                table: "AlertIncidents");
        }
    }
}

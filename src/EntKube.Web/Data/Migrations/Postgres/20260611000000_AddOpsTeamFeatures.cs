using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260611000000_AddOpsTeamFeatures")]
    /// <inheritdoc />
    public partial class AddOpsTeamFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "AlertIncidents" ADD COLUMN IF NOT EXISTS "AssignedTo" character varying(256);""");

            migrationBuilder.Sql(
                """ALTER TABLE "AlertIncidents" ADD COLUMN IF NOT EXISTS "AssignedAt" timestamp with time zone;""");

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "OnCallSchedules" (
                    "Id" uuid NOT NULL,
                    "TenantId" uuid NOT NULL,
                    "Name" character varying(200) NOT NULL,
                    "Description" character varying(500),
                    "IsEnabled" boolean NOT NULL DEFAULT true,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_OnCallSchedules" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_OnCallSchedules_Tenants_TenantId"
                        FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "OnCallShifts" (
                    "Id" uuid NOT NULL,
                    "ScheduleId" uuid NOT NULL,
                    "AssigneeName" character varying(256) NOT NULL,
                    "AssigneeEmail" character varying(256),
                    "StartsAt" timestamp with time zone NOT NULL,
                    "EndsAt" timestamp with time zone NOT NULL,
                    "Notes" character varying(1000),
                    CONSTRAINT "PK_OnCallShifts" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_OnCallShifts_OnCallSchedules_ScheduleId"
                        FOREIGN KEY ("ScheduleId") REFERENCES "OnCallSchedules" ("Id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AlertRoutingRules" (
                    "Id" uuid NOT NULL,
                    "TenantId" uuid NOT NULL,
                    "Name" character varying(200) NOT NULL,
                    "Priority" integer NOT NULL DEFAULT 0,
                    "ChannelId" uuid NOT NULL,
                    "MatchAlertName" character varying(200),
                    "MatchNamespace" character varying(200),
                    "MatchSeverity" character varying(20),
                    "MatchLabelKey" character varying(100),
                    "MatchLabelValue" character varying(200),
                    "IsEnabled" boolean NOT NULL DEFAULT true,
                    CONSTRAINT "PK_AlertRoutingRules" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_AlertRoutingRules_Tenants_TenantId"
                        FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_AlertRoutingRules_NotificationChannels_ChannelId"
                        FOREIGN KEY ("ChannelId") REFERENCES "NotificationChannels" ("Id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_OnCallShifts_ScheduleId"
                    ON "OnCallShifts" ("ScheduleId");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_OnCallShifts_StartsAt"
                    ON "OnCallShifts" ("StartsAt");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_AlertRoutingRules_TenantId"
                    ON "AlertRoutingRules" ("TenantId");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_AlertRoutingRules_ChannelId"
                    ON "AlertRoutingRules" ("ChannelId");
                """);
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

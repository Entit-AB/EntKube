using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260611060000_AddAlertRoutingSuppression")]
    public partial class AddAlertRoutingSuppression : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Make ChannelId nullable
            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    DROP CONSTRAINT "FK_AlertRoutingRules_NotificationChannels_ChannelId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    ALTER COLUMN "ChannelId" DROP NOT NULL;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    ADD CONSTRAINT "FK_AlertRoutingRules_NotificationChannels_ChannelId"
                    FOREIGN KEY ("ChannelId") REFERENCES "NotificationChannels" ("Id") ON DELETE CASCADE;
                """);

            // Add new columns
            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    ADD COLUMN IF NOT EXISTS "SuppressIncident" boolean NOT NULL DEFAULT false;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    ADD COLUMN IF NOT EXISTS "MatchClusterId" uuid;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    ADD CONSTRAINT "FK_AlertRoutingRules_KubernetesClusters_MatchClusterId"
                    FOREIGN KEY ("MatchClusterId") REFERENCES "KubernetesClusters" ("Id") ON DELETE SET NULL;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_AlertRoutingRules_MatchClusterId"
                    ON "AlertRoutingRules" ("MatchClusterId");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_AlertRoutingRules_MatchClusterId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    DROP CONSTRAINT IF EXISTS "FK_AlertRoutingRules_KubernetesClusters_MatchClusterId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    DROP COLUMN IF EXISTS "MatchClusterId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    DROP COLUMN IF EXISTS "SuppressIncident";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    DROP CONSTRAINT "FK_AlertRoutingRules_NotificationChannels_ChannelId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    ALTER COLUMN "ChannelId" SET NOT NULL;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AlertRoutingRules"
                    ADD CONSTRAINT "FK_AlertRoutingRules_NotificationChannels_ChannelId"
                    FOREIGN KEY ("ChannelId") REFERENCES "NotificationChannels" ("Id") ON DELETE CASCADE;
                """);
        }
    }
}

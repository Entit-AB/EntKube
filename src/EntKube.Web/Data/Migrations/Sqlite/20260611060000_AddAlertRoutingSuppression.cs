using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    public partial class AddAlertRoutingSuppression : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite does not support ALTER COLUMN, so rebuild the table to make ChannelId nullable
            // and add the two new columns at the same time.
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql("""
                CREATE TABLE "AlertRoutingRules_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AlertRoutingRules" PRIMARY KEY,
                    "TenantId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Priority" INTEGER NOT NULL DEFAULT 0,
                    "ChannelId" TEXT,
                    "MatchAlertName" TEXT,
                    "MatchNamespace" TEXT,
                    "MatchSeverity" TEXT,
                    "MatchLabelKey" TEXT,
                    "MatchLabelValue" TEXT,
                    "IsEnabled" INTEGER NOT NULL DEFAULT 1,
                    "SuppressIncident" INTEGER NOT NULL DEFAULT 0,
                    "MatchClusterId" TEXT,
                    CONSTRAINT "FK_AlertRoutingRules_Tenants_TenantId"
                        FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_AlertRoutingRules_NotificationChannels_ChannelId"
                        FOREIGN KEY ("ChannelId") REFERENCES "NotificationChannels" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_AlertRoutingRules_KubernetesClusters_MatchClusterId"
                        FOREIGN KEY ("MatchClusterId") REFERENCES "KubernetesClusters" ("Id") ON DELETE SET NULL
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "AlertRoutingRules_new"
                    ("Id","TenantId","Name","Priority","ChannelId","MatchAlertName","MatchNamespace",
                     "MatchSeverity","MatchLabelKey","MatchLabelValue","IsEnabled","SuppressIncident","MatchClusterId")
                SELECT
                    "Id","TenantId","Name","Priority","ChannelId","MatchAlertName","MatchNamespace",
                    "MatchSeverity","MatchLabelKey","MatchLabelValue","IsEnabled", 0, NULL
                FROM "AlertRoutingRules";
                """);

            migrationBuilder.DropTable("AlertRoutingRules");

            migrationBuilder.Sql("""ALTER TABLE "AlertRoutingRules_new" RENAME TO "AlertRoutingRules";""");

            migrationBuilder.CreateIndex("IX_AlertRoutingRules_TenantId", "AlertRoutingRules", "TenantId");
            migrationBuilder.CreateIndex("IX_AlertRoutingRules_ChannelId", "AlertRoutingRules", "ChannelId");
            migrationBuilder.CreateIndex("IX_AlertRoutingRules_MatchClusterId", "AlertRoutingRules", "MatchClusterId");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql("""
                CREATE TABLE "AlertRoutingRules_old" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AlertRoutingRules" PRIMARY KEY,
                    "TenantId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Priority" INTEGER NOT NULL DEFAULT 0,
                    "ChannelId" TEXT NOT NULL,
                    "MatchAlertName" TEXT,
                    "MatchNamespace" TEXT,
                    "MatchSeverity" TEXT,
                    "MatchLabelKey" TEXT,
                    "MatchLabelValue" TEXT,
                    "IsEnabled" INTEGER NOT NULL DEFAULT 1,
                    CONSTRAINT "FK_AlertRoutingRules_Tenants_TenantId"
                        FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_AlertRoutingRules_NotificationChannels_ChannelId"
                        FOREIGN KEY ("ChannelId") REFERENCES "NotificationChannels" ("Id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "AlertRoutingRules_old"
                    ("Id","TenantId","Name","Priority","ChannelId","MatchAlertName","MatchNamespace",
                     "MatchSeverity","MatchLabelKey","MatchLabelValue","IsEnabled")
                SELECT
                    "Id","TenantId","Name","Priority",COALESCE("ChannelId",'00000000-0000-0000-0000-000000000000'),
                    "MatchAlertName","MatchNamespace","MatchSeverity","MatchLabelKey","MatchLabelValue","IsEnabled"
                FROM "AlertRoutingRules"
                WHERE "SuppressIncident" = 0;
                """);

            migrationBuilder.DropTable("AlertRoutingRules");

            migrationBuilder.Sql("""ALTER TABLE "AlertRoutingRules_old" RENAME TO "AlertRoutingRules";""");

            migrationBuilder.CreateIndex("IX_AlertRoutingRules_TenantId", "AlertRoutingRules", "TenantId");
            migrationBuilder.CreateIndex("IX_AlertRoutingRules_ChannelId", "AlertRoutingRules", "ChannelId");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }
    }
}

using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260611020000_AddAlertIncidentEscalation")]
    public partial class AddAlertIncidentEscalation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "AlertIncidents"
                    ADD COLUMN IF NOT EXISTS "EscalatedAt" timestamp with time zone NULL;

                CREATE INDEX IF NOT EXISTS "IX_AlertIncidents_EscalatedAt"
                    ON "AlertIncidents" ("EscalatedAt");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlertIncidents_EscalatedAt",
                table: "AlertIncidents");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                table: "AlertIncidents");
        }
    }
}

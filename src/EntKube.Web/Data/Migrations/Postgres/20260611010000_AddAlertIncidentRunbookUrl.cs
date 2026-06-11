using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260611010000_AddAlertIncidentRunbookUrl")]
    public partial class AddAlertIncidentRunbookUrl : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "AlertIncidents"
                    ADD COLUMN IF NOT EXISTS "RunbookUrl" character varying(500) NOT NULL DEFAULT '';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunbookUrl",
                table: "AlertIncidents");
        }
    }
}

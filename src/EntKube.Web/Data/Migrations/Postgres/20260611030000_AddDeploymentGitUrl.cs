using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260611030000_AddDeploymentGitUrl")]
    public partial class AddDeploymentGitUrl : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "AppDeployments"
                    ADD COLUMN IF NOT EXISTS "GitUrl" character varying(2000) NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitUrl",
                table: "AppDeployments");
        }
    }
}

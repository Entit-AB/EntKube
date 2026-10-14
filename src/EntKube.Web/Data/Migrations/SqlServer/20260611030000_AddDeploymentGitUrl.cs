using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    // Was invisible to EF until 2026-10-14: no Designer file and no attributes, so the
    // migrations assembly never found it and a database built from migrations lacked the
    // column. The attributes are what a Designer file would have carried.
    [DbContext(typeof(SqlServerApplicationDbContext))]
    [Migration("20260611030000_AddDeploymentGitUrl")]
    public partial class AddDeploymentGitUrl : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitUrl",
                table: "AppDeployments",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitUrl",
                table: "AppDeployments");
        }
    }
}

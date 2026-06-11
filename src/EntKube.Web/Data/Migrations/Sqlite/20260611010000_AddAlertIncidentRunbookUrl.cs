using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260611010000_AddAlertIncidentRunbookUrl")]
    public partial class AddAlertIncidentRunbookUrl : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunbookUrl",
                table: "AlertIncidents",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunbookUrl",
                table: "AlertIncidents");
        }
    }
}

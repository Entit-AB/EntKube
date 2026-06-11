using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260611020000_AddAlertIncidentEscalation")]
    public partial class AddAlertIncidentEscalation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedAt",
                table: "AlertIncidents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertIncidents_EscalatedAt",
                table: "AlertIncidents",
                column: "EscalatedAt");
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

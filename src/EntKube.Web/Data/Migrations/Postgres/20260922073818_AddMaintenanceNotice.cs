using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddMaintenanceNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncedAt",
                table: "MaintenanceWindows",
                type: "timestamp with time zone",
                nullable: true);

            // Existing windows become Planned (0), which is what the CLR default happens to
            // scaffold here and what we actually want: a window recorded before this column
            // existed said nothing about being an emergency, so it has not earned the
            // exemption from the notice period.
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "MaintenanceWindows",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnouncedAt",
                table: "MaintenanceWindows");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "MaintenanceWindows");
        }
    }
}

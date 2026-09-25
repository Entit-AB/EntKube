using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddSlaApplied : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-corrected from the scaffolded false. Every ticket that predates this
            // column was raised when the SLA was assumed to apply, and its breaches and
            // §14.6 deviations were reported on that basis. Defaulting them to false would
            // quietly erase penalty history — the agreement's own record of what we owed.
            migrationBuilder.AddColumn<bool>(
                name: "SlaApplied",
                table: "Tickets",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SlaApplied",
                table: "Tickets");
        }
    }
}

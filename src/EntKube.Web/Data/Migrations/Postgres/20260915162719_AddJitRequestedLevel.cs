using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddJitRequestedLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Zero is Observe, which is both the enum's default and the least a request could
            // have asked for — the right reading for a row that predates the column.
            migrationBuilder.AddColumn<int>(
                name: "RequestedLevel",
                table: "JitGrants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // A grant requested before this column existed asked for whatever the approver
            // used to choose by hand, and the form's own default is an hour — so 60, not the
            // CLR zero the scaffolder writes. Zero would read as "asked for no time at all".
            migrationBuilder.AddColumn<int>(
                name: "RequestedMinutes",
                table: "JitGrants",
                type: "integer",
                nullable: false,
                defaultValue: 60);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedLevel",
                table: "JitGrants");

            migrationBuilder.DropColumn(
                name: "RequestedMinutes",
                table: "JitGrants");
        }
    }
}

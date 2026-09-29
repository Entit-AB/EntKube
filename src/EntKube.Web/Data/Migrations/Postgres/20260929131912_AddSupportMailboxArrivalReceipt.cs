using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSupportMailboxArrivalReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is true deliberately, and against what the scaffolder wrote. The
            // property initialiser says true, which `migrations add` does not read — it
            // writes the CLR zero — and a mailbox already configured would then be the one
            // installation where answering on arrival was off, silently, with a checkbox on
            // screen claiming otherwise. It is the behaviour that was asked for; a tenant
            // who does not want it turns it off there.
            migrationBuilder.AddColumn<bool>(
                name: "AcknowledgeOnArrival",
                table: "SupportMailboxes",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMachineGenerated",
                table: "InboundMailMessages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgeOnArrival",
                table: "SupportMailboxes");

            migrationBuilder.DropColumn(
                name: "IsMachineGenerated",
                table: "InboundMailMessages");
        }
    }
}

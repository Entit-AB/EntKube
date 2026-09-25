using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSenderAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TrustedAuthenticationServer",
                table: "SupportMailboxes",
                type: "text",
                nullable: true);

            // 0 is Unknown, and for everything already in the table that is the truth
            // rather than a placeholder: nothing checked those senders, because there was
            // nothing to check them with.
            migrationBuilder.AddColumn<int>(
                name: "SenderAuthenticity",
                table: "InboundMailMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrustedAuthenticationServer",
                table: "SupportMailboxes");

            migrationBuilder.DropColumn(
                name: "SenderAuthenticity",
                table: "InboundMailMessages");
        }
    }
}

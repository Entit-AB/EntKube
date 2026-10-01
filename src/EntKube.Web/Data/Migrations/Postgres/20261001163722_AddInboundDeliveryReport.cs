using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddInboundDeliveryReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailedRecipient",
                table: "InboundMailMessages",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeliveryReport",
                table: "InboundMailMessages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedRecipient",
                table: "InboundMailMessages");

            migrationBuilder.DropColumn(
                name: "IsDeliveryReport",
                table: "InboundMailMessages");
        }
    }
}

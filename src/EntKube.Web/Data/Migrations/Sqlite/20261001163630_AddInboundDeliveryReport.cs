using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
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
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeliveryReport",
                table: "InboundMailMessages",
                type: "INTEGER",
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

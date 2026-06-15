using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddNotificationChannelFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcknowledgeFilter",
                table: "NotificationChannels",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "FiringFilter",
                table: "NotificationChannels",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "All");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgeFilter",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "FiringFilter",
                table: "NotificationChannels");
        }
    }
}

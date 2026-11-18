using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddNotificationMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mail",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mail",
                table: "NotificationPreferences");
        }
    }
}

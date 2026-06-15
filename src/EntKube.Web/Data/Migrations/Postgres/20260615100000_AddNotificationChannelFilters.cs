using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddNotificationChannelFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "NotificationChannels" ADD COLUMN IF NOT EXISTS "AcknowledgeFilter" character varying(30) NOT NULL DEFAULT 'All';
                ALTER TABLE "NotificationChannels" ADD COLUMN IF NOT EXISTS "FiringFilter" character varying(30) NOT NULL DEFAULT 'All';
                """);
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

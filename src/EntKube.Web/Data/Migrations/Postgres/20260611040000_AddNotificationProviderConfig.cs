using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260611040000_AddNotificationProviderConfig")]
    public partial class AddNotificationProviderConfig : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationProviderConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationProviderConfigs_ProviderType",
                table: "NotificationProviderConfigs",
                column: "ProviderType",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationProviderConfigs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <summary>
    /// Creates NotificationProviderConfigs, already tenant-owned.
    ///
    /// <para><b>Why this creates rather than alters, unlike the other two providers.</b>
    /// The SQLite chain never built this table. <c>20260611040000_AddNotificationProviderConfig</c>
    /// is present as a source file but has no Designer, so it carries no <c>[Migration]</c>
    /// attribute, so EF's migrations assembly has never listed it and <c>database update</c> has
    /// never run it. (It is not alone — four more SQLite migrations are orphaned the same way.)
    /// Altering a table that has never existed is how this migration first failed, with
    /// <c>SQLite Error 1: no such index</c>.</para>
    ///
    /// <para>So there is nothing here to back-fill either: a table that was never created holds
    /// no pre-tenant rows. The Postgres and SQL Server copies of this migration do both, because
    /// there the table is real and may hold rows from when providers were installation-wide.</para>
    /// </summary>
    public partial class PerTenantNotificationProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationProviderConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationProviderConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationProviderConfigs_TenantId_ProviderType",
                table: "NotificationProviderConfigs",
                columns: new[] { "TenantId", "ProviderType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationProviderConfigs");
        }
    }
}

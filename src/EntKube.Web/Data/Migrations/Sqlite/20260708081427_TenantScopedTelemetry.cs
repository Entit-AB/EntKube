using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class TenantScopedTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelemetrySegments_Signal_MaxTs_MinTs",
                table: "TelemetrySegments");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "TelemetrySegments",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "TelemetryStorageSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryStorageSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySegments_TenantId_Signal_MaxTs_MinTs",
                table: "TelemetrySegments",
                columns: new[] { "TenantId", "Signal", "MaxTs", "MinTs" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryStorageSettings_TenantId",
                table: "TelemetryStorageSettings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetryStorageSettings");

            migrationBuilder.DropIndex(
                name: "IX_TelemetrySegments_TenantId_Signal_MaxTs_MinTs",
                table: "TelemetrySegments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "TelemetrySegments");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySegments_Signal_MaxTs_MinTs",
                table: "TelemetrySegments",
                columns: new[] { "Signal", "MaxTs", "MinTs" });
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddTelemetrySegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetrySegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Signal = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MinTs = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MaxTs = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DocCount = table.Column<long>(type: "bigint", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SealedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetrySegments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySegments_Signal_MaxTs_MinTs",
                table: "TelemetrySegments",
                columns: new[] { "Signal", "MaxTs", "MinTs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetrySegments");
        }
    }
}

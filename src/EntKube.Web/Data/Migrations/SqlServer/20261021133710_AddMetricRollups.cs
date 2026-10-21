using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddMetricRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Namespace = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Pod = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BucketStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolutionSeconds = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Sum = table.Column<double>(type: "float", nullable: false),
                    Min = table.Column<double>(type: "float", nullable: false),
                    Max = table.Column<double>(type: "float", nullable: false),
                    Last = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricRollups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollups_BucketStart",
                table: "MetricRollups",
                column: "BucketStart");

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollups_Scope",
                table: "MetricRollups",
                columns: new[] { "TenantId", "ClusterId", "Name", "Service", "Namespace", "Pod", "BucketStart", "ResolutionSeconds" },
                unique: true,
                filter: "[Service] IS NOT NULL AND [Namespace] IS NOT NULL AND [Pod] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollups_TenantId_ClusterId_Name_BucketStart",
                table: "MetricRollups",
                columns: new[] { "TenantId", "ClusterId", "Name", "BucketStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricRollups");
        }
    }
}

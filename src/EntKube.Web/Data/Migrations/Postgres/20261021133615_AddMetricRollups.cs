using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Namespace = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Pod = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BucketStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolutionSeconds = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Sum = table.Column<double>(type: "double precision", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: false),
                    Max = table.Column<double>(type: "double precision", nullable: false),
                    Last = table.Column<double>(type: "double precision", nullable: false)
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
                unique: true);

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

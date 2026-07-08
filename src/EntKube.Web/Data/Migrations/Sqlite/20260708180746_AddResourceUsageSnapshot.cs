using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddResourceUsageSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceUsageSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Fraction = table.Column<double>(type: "REAL", nullable: false),
                    SnapshotAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceUsageSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourceUsageSnapshots_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceUsageSnapshots_ClusterId_Kind_SnapshotAt",
                table: "ResourceUsageSnapshots",
                columns: new[] { "ClusterId", "Kind", "SnapshotAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceUsageSnapshots");
        }
    }
}

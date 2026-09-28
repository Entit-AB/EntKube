using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddElasticsearchKibanaSpaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KibanaSpaceId",
                table: "ElasticsearchUsers",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ElasticsearchKibanaSpaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ElasticsearchClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastAppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchKibanaSpaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchKibanaSpaces_ElasticsearchClusters_ElasticsearchClusterId",
                        column: x => x.ElasticsearchClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchKibanaSpaces_ElasticsearchClusterId_SpaceId",
                table: "ElasticsearchKibanaSpaces",
                columns: new[] { "ElasticsearchClusterId", "SpaceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElasticsearchKibanaSpaces");

            migrationBuilder.DropColumn(
                name: "KibanaSpaceId",
                table: "ElasticsearchUsers");
        }
    }
}

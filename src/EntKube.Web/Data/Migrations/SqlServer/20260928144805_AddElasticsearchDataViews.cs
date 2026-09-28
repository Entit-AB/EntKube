using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddElasticsearchDataViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ElasticsearchDataViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ElasticsearchClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    TimeFieldName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    LastAppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchDataViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchDataViews_ElasticsearchClusters_ElasticsearchClusterId",
                        column: x => x.ElasticsearchClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchDataViews_ElasticsearchClusterId_SpaceId_Title",
                table: "ElasticsearchDataViews",
                columns: new[] { "ElasticsearchClusterId", "SpaceId", "Title" },
                unique: true,
                filter: "[SpaceId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElasticsearchDataViews");
        }
    }
}

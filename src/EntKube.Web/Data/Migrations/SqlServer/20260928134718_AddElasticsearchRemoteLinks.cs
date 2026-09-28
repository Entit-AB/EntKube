using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddElasticsearchRemoteLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ElasticsearchRemoteLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocalClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RemoteClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    SearchIndexPatterns = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LastAppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchRemoteLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchRemoteLinks_ElasticsearchClusters_LocalClusterId",
                        column: x => x.LocalClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ElasticsearchRemoteLinks_ElasticsearchClusters_RemoteClusterId",
                        column: x => x.RemoteClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchRemoteLinks_LocalClusterId_Alias",
                table: "ElasticsearchRemoteLinks",
                columns: new[] { "LocalClusterId", "Alias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchRemoteLinks_RemoteClusterId",
                table: "ElasticsearchRemoteLinks",
                column: "RemoteClusterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElasticsearchRemoteLinks");
        }
    }
}

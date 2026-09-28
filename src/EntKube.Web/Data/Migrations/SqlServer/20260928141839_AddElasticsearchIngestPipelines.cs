using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddElasticsearchIngestPipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultPipelineName",
                table: "ElasticsearchIlmPolicies",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ElasticsearchIngestPipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ElasticsearchClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimestampField = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    TimestampFormats = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    GrokField = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    GrokPattern = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RenameFields = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RemoveFields = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SetFields = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CustomProcessorsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastAppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchIngestPipelines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchIngestPipelines_ElasticsearchClusters_ElasticsearchClusterId",
                        column: x => x.ElasticsearchClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchIngestPipelines_ElasticsearchClusterId_Name",
                table: "ElasticsearchIngestPipelines",
                columns: new[] { "ElasticsearchClusterId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElasticsearchIngestPipelines");

            migrationBuilder.DropColumn(
                name: "DefaultPipelineName",
                table: "ElasticsearchIlmPolicies");
        }
    }
}

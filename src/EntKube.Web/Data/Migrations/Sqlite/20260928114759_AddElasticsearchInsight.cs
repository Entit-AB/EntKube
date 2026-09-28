using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddElasticsearchInsight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HighestNodeDiskPercent",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InsightCheckedAt",
                table: "ElasticsearchClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnassignedShards",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HighestNodeDiskPercent",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "InsightCheckedAt",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "UnassignedShards",
                table: "ElasticsearchClusters");
        }
    }
}

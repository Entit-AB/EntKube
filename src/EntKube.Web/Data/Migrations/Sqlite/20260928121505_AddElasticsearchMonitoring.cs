using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddElasticsearchMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MonitoringCpuRequest",
                table: "ElasticsearchClusters",
                type: "TEXT",
                nullable: false,
                defaultValue: "50m");

            migrationBuilder.AddColumn<bool>(
                name: "MonitoringEnabled",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MonitoringIndexMetrics",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MonitoringMemory",
                table: "ElasticsearchClusters",
                type: "TEXT",
                nullable: false,
                defaultValue: "128Mi");

            migrationBuilder.AddColumn<string>(
                name: "MonitoringSelectorNote",
                table: "ElasticsearchClusters",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonitoringCpuRequest",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "MonitoringEnabled",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "MonitoringIndexMetrics",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "MonitoringMemory",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "MonitoringSelectorNote",
                table: "ElasticsearchClusters");
        }
    }
}

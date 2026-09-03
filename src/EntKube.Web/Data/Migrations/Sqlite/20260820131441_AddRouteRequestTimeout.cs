using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddRouteRequestTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestTimeoutSeconds",
                table: "ExternalRoutes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestTimeoutSeconds",
                table: "AppDeploymentRoutes",
                type: "INTEGER",
                nullable: true);

            // Existing rows stay NULL and pick up the platform default (60s) the next time
            // their HTTPRoute is regenerated. Two kinds of route EntKube created itself would
            // break under that default, so they are pinned here rather than left to bite on
            // the next apply:
            //
            //  • headscale — the ts2021 control-plane connection is a long-lived HTTP upgrade,
            //    and Gateway API's request timeout bounds the whole exchange.
            //  • harbor — a single container image layer push routinely runs past 60s.
            migrationBuilder.Sql(
                "UPDATE \"ExternalRoutes\" SET \"RequestTimeoutSeconds\" = 0 WHERE \"ServiceName\" = 'headscale';");
            migrationBuilder.Sql(
                "UPDATE \"ExternalRoutes\" SET \"RequestTimeoutSeconds\" = 3600 " +
                "WHERE \"ComponentId\" IN (SELECT \"Id\" FROM \"ClusterComponents\" WHERE \"HelmChartName\" = 'harbor');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestTimeoutSeconds",
                table: "ExternalRoutes");

            migrationBuilder.DropColumn(
                name: "RequestTimeoutSeconds",
                table: "AppDeploymentRoutes");
        }
    }
}

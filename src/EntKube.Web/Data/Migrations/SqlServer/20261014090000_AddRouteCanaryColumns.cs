using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <summary>
    /// The weighted-release columns on AppDeploymentRoutes, which the model, the Designer files and
    /// every ModelSnapshot have carried since the feature was written — and which <b>no migration on
    /// any provider ever created</b>. A database built by running the migrations did not have them,
    /// so every query that touched a route failed on a column the model insisted was there.
    ///
    /// <para>Written by hand for all three providers at once, which is why it carries its own
    /// attributes rather than a Designer file: the snapshots already describe these columns, so
    /// scaffolding would have produced an empty migration — the same gap that let them go missing.</para>
    /// </summary>
    [DbContext(typeof(SqlServerApplicationDbContext))]
    [Migration("20261014090000_AddRouteCanaryColumns")]
    public partial class AddRouteCanaryColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanaryServiceName",
                table: "AppDeploymentRoutes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CanaryServicePort",
                table: "AppDeploymentRoutes",
                type: "int",
                nullable: true);

            // Zero, so a route with no canary emits a single backend and is byte-identical to what
            // it was before the field existed — the default the model documents.
            migrationBuilder.AddColumn<int>(
                name: "CanaryWeight",
                table: "AppDeploymentRoutes",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CanaryWeight", table: "AppDeploymentRoutes");
            migrationBuilder.DropColumn(name: "CanaryServicePort", table: "AppDeploymentRoutes");
            migrationBuilder.DropColumn(name: "CanaryServiceName", table: "AppDeploymentRoutes");
        }
    }
}

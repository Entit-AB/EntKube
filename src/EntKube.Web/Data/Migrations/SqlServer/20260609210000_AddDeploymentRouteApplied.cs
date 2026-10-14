using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    // Was invisible to EF until 2026-10-14: no Designer file and no attributes, so the
    // migrations assembly never found it and a database built from migrations lacked the
    // column. The attributes are what a Designer file would have carried.
    [DbContext(typeof(SqlServerApplicationDbContext))]
    [Migration("20260609210000_AddDeploymentRouteApplied")]
    public partial class AddDeploymentRouteApplied : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClusterAppliedAt",
                table: "AppDeploymentRoutes",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClusterAppliedAt",
                table: "AppDeploymentRoutes");
        }
    }
}

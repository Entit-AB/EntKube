using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260609210000_AddDeploymentRouteApplied")]
    /// <inheritdoc />
    public partial class AddDeploymentRouteApplied : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "AppDeploymentRoutes" ADD COLUMN IF NOT EXISTS "ClusterAppliedAt" timestamp with time zone;""");
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

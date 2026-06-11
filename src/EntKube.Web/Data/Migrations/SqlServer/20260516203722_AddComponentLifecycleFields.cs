using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddComponentLifecycleFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HelmChartName",
                table: "ClusterComponents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HelmChartVersion",
                table: "ClusterComponents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HelmRepoUrl",
                table: "ClusterComponents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HelmValues",
                table: "ClusterComponents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InstalledAt",
                table: "ClusterComponents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "ClusterComponents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Namespace",
                table: "ClusterComponents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseName",
                table: "ClusterComponents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ClusterComponents",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HelmChartName",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "HelmChartVersion",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "HelmRepoUrl",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "HelmValues",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "InstalledAt",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "Namespace",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "ReleaseName",
                table: "ClusterComponents");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ClusterComponents");
        }
    }
}

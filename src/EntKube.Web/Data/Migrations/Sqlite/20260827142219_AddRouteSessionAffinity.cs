using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddRouteSessionAffinity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SessionAffinity",
                table: "ExternalRoutes",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                // "None", not the "" EF scaffolds for a string column: the column maps back to
                // SessionAffinityMode, and every row that predates this migration would fail to
                // materialise on read if it held the empty string.
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "SessionAffinityKey",
                table: "ExternalRoutes",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionAffinityTtlSeconds",
                table: "ExternalRoutes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionAffinity",
                table: "AppDeploymentRoutes",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                // "None", not the "" EF scaffolds for a string column: the column maps back to
                // SessionAffinityMode, and every row that predates this migration would fail to
                // materialise on read if it held the empty string.
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "SessionAffinityKey",
                table: "AppDeploymentRoutes",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionAffinityTtlSeconds",
                table: "AppDeploymentRoutes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionAffinity",
                table: "ExternalRoutes");

            migrationBuilder.DropColumn(
                name: "SessionAffinityKey",
                table: "ExternalRoutes");

            migrationBuilder.DropColumn(
                name: "SessionAffinityTtlSeconds",
                table: "ExternalRoutes");

            migrationBuilder.DropColumn(
                name: "SessionAffinity",
                table: "AppDeploymentRoutes");

            migrationBuilder.DropColumn(
                name: "SessionAffinityKey",
                table: "AppDeploymentRoutes");

            migrationBuilder.DropColumn(
                name: "SessionAffinityTtlSeconds",
                table: "AppDeploymentRoutes");
        }
    }
}
